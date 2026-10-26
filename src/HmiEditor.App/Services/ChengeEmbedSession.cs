using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HmiEditor.App.Services;

/// <summary>
/// 进程内嵌入运行时（`libchenge_runtime.so` / 将来的 `.dll`）。
///
/// <para><b>它替掉的是什么</b>：原来的预览是"导出到临时目录 → 起 <c>chenge-runtime</c>
/// 进程 → 截一张 BMP"。那条路只能看、不能点，而且每看一次要等进程启动。
/// 这里直接加载库：帧是一块 ARGB8888 内存（LVGL DIRECT 整屏），指针由
/// <see cref="Pointer"/> 注进去，因此预览可操作、且与板上是同一份渲染代码。</para>
///
/// <para><b>线程</b>：库是单线程的（LVGL 不是线程安全的）。本类自己养一条
/// 后台线程按 ~60Hz 调 <c>tick</c>，帧在 tick 之后拷进 <see cref="Buffer"/>；
/// UI 线程只做"取最新帧 → 贴到位图"，不直接碰库里的状态。</para>
///
/// <para><b>崩溃隔离</b>：本类是进程内实现，工程里的 Lua 死循环/越界会带崩编辑器。
/// 需要隔离时走独立宿主进程（同一份库编出的 <c>chenge-preview-host</c>），
/// 接口保持 <see cref="IChengePreviewSession"/> 一致。</para>
/// </summary>
public sealed class ChengeEmbedSession : IChengePreviewSession
{
    private const string LibLinux = "libchenge_runtime.so";
    private const string LibWindows = "chenge_runtime.dll";

    private readonly IntPtr _lib;
    private readonly OpenFn _open;
    private readonly OpenMemFn _openMem;
    private readonly ProjectClearFn _projectClear;
    private readonly ProjectPutFn _projectPut;
    private readonly TickFn _tick;
    private readonly PointerFn _pointer;
    private readonly FrameFn _frame;
    private readonly ScreenFn _screen;
    private readonly SaveBmpFn _saveBmp;
    private readonly TagBoolFn _tagBool;
    private readonly TagNumFn _tagNum;
    private readonly TagKindFn _tagKind;
    private readonly CloseFn _close;
    private readonly LastErrorFn _lastError;

    private readonly object _frameLock = new();
    private byte[] _buffer = Array.Empty<byte>();
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _opened;
    private long _frames;
    private string _lastErrorText = "";

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public string DataDir { get; }
    public string ProjectDir { get; private set; } = "";

    /// <summary>最近一秒的帧数（状态栏用；取不到精确值时报 0 也不影响功能）。</summary>
    public double Fps { get; private set; }

    public bool IsOpen => _opened;

    /// <summary>进程内模式没有单独的日志通道（HMI_LOG 走 stderr，直接进编辑器控制台）。</summary>
    public IReadOnlyList<string> Log => Array.Empty<string>();

    /// <summary>进程内模式直接读 tagdb，不需要"轮询"这个动作。</summary>
    public void PollTag(string name)
    {
    }

    public bool TryGetTag(string name, out string value)
    {
        value = "";
        if (!_opened) return false;

        var kind = TagKind(name);
        if (kind < 0) return false;
        if (kind == 1)
        {
            if (!TryGetBool(name, out var b)) return false;
            value = b ? "1" : "0";
            return true;
        }
        if (!TryGetNumber(name, out var d)) return false;
        value = d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private int TagKind(string name)
    {
        using var n = Utf8(name);
        return _tagKind(n.Ptr);
    }
    public string LastError => _lastErrorText;

    public ChengeEmbedSession(string libraryPath, string dataDir)
    {
        _lib = NativeLibrary.Load(libraryPath);
        DataDir = dataDir;

        _open = Bind<OpenFn>("chenge_embed_open");
        _openMem = Bind<OpenMemFn>("chenge_embed_open_mem");
        _projectClear = Bind<ProjectClearFn>("chenge_embed_project_clear");
        _projectPut = Bind<ProjectPutFn>("chenge_embed_project_put");
        _tick = Bind<TickFn>("chenge_embed_tick");
        _pointer = Bind<PointerFn>("chenge_embed_pointer");
        _frame = Bind<FrameFn>("chenge_embed_frame");
        _screen = Bind<ScreenFn>("chenge_embed_screen");
        _saveBmp = Bind<SaveBmpFn>("chenge_embed_save_bmp");
        _tagBool = Bind<TagBoolFn>("chenge_embed_tag_bool");
        _tagNum = Bind<TagNumFn>("chenge_embed_tag_num");
        _tagKind = Bind<TagKindFn>("chenge_embed_tag_kind");
        _close = Bind<CloseFn>("chenge_embed_close");
        _lastError = Bind<LastErrorFn>("chenge_embed_last_error");

        var abi = Bind<AbiFn>("chenge_embed_abi")();
        if (abi != 1)
            throw new InvalidOperationException($"运行时库 ABI 是 {abi}，编辑器要 1（版本不匹配，重新编译运行时）");
    }

    /// <summary>库文件名：按当前平台挑（Windows 的 .dll 还没做，先给名字让错误信息可读）。</summary>
    public static string LibraryName => OperatingSystem.IsWindows() ? LibWindows : LibLinux;

    public static bool TryLocate(string? runtimeDir, out string libraryPath)
    {
        libraryPath = "";
        if (string.IsNullOrWhiteSpace(runtimeDir)) return false;
        var p = Path.Combine(runtimeDir, LibraryName);
        if (!File.Exists(p)) return false;
        libraryPath = p;
        return true;
    }

    public bool Open(string projectDir)
    {
        var err = new byte[512];
        using var p = Utf8(projectDir);
        using var d = Utf8(DataDir);
        var rc = _open(p.Ptr, d.Ptr, err, err.Length);
        if (rc != 0)
        {
            _lastErrorText = ReadError(err);
            return false;
        }
        ProjectDir = projectDir;
        AfterOpen();
        return true;
    }

    /// <summary>
    /// 内存工程：工程文件由调用方先 <see cref="PutFile"/> 进来，不落盘。
    /// 适合"预览未保存的模型"，也避开中文用户目录这类路径问题。
    /// </summary>
    public bool OpenMemory()
    {
        var err = new byte[512];
        using var d = Utf8(DataDir);
        var rc = _openMem(d.Ptr, err, err.Length);
        if (rc != 0)
        {
            _lastErrorText = ReadError(err);
            return false;
        }
        ProjectDir = "";
        AfterOpen();
        return true;
    }

    public void ClearProjectFiles() => _projectClear();

    public bool PutFile(string relativePath, byte[] bytes)
    {
        using var p = Utf8(relativePath);
        return _projectPut(p.Ptr, bytes, bytes.Length) == 0;
    }

    private void AfterOpen()
    {
        _opened = true;
        if (_screen(out var w, out var h) == 0 && w > 0 && h > 0)
        {
            Width = w;
            Height = h;
        }
        Stride = Width * 4;
        lock (_frameLock) _buffer = new byte[Stride * Height];

        _stop = false;
        _thread = new Thread(TickLoop) { IsBackground = true, Name = "chenge-embed-tick" };
        _thread.Start();
    }

    private void TickLoop()
    {
        var last = Environment.TickCount64;
        long framesAtLast = 0;
        while (!_stop)
        {
            _tick();

            var ptr = _frame(out var w, out var h, out var stride);
            if (ptr != IntPtr.Zero && w == Width && h == Height)
            {
                lock (_frameLock)
                {
                    if (_buffer.Length != stride * h) _buffer = new byte[stride * h];
                    Marshal.Copy(ptr, _buffer, 0, _buffer.Length);
                }
            }
            _frames++;
            Interlocked.Increment(ref _version);

            var now = Environment.TickCount64;
            if (now - last >= 1000)
            {
                Fps = (_frames - framesAtLast) * 1000.0 / (now - last);
                framesAtLast = _frames;
                last = now;
            }
            Thread.Sleep(16);
        }
    }

    private long _version;

    /// <summary>帧序号：UI 侧比较它来跳过"没有新帧"的刷新。</summary>
    public long FrameVersion => Interlocked.Read(ref _version);

    public void Pointer(int x, int y, bool down) => _pointer(x, y, down ? 1 : 0);

    public bool TryCopyFrame(byte[] dest)
    {
        lock (_frameLock)
        {
            if (_buffer.Length == 0 || dest.Length != _buffer.Length) return false;
            Buffer.BlockCopy(_buffer, 0, dest, 0, _buffer.Length);
            return true;
        }
    }

    /// <summary>存 BMP（调试用）。注意：tick 线程可能正在写帧，截出来的图可接受但非严格同步。</summary>
    public bool SaveScreenshot(string path)
    {
        using var p = Utf8(path);
        return _saveBmp(p.Ptr) == 0;
    }

    /// <summary>进程内模式没有"命令驱动重绘"：帧由调用方的 tick 线程推进。</summary>
    public bool RenderOnce() => false;

    public bool TryGetBool(string tagName, out bool value)
    {
        using var n = Utf8(tagName);
        var v = _tagBool(n.Ptr, out var i);
        value = i != 0;
        return v != 0;
    }

    public bool TryGetNumber(string tagName, out double value)
    {
        using var n = Utf8(tagName);
        var v = _tagNum(n.Ptr, out var d);
        value = d;
        return v != 0;
    }

    public void Stop()
    {
        _stop = true;
        if (_thread is { IsAlive: true }) _thread.Join(300);
        _thread = null;
        if (_opened)
        {
            _close();
            _opened = false;
        }
    }

    public void Dispose()
    {
        Stop();
        if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib);
    }

    private T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    private string ReadError(byte[] buf)
    {
        var s = System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
        if (string.IsNullOrEmpty(s))
        {
            var p = _lastError();
            s = p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
        }
        return s;
    }

    /// <summary>UTF-8 字符串的临时非托管副本（用完必须释放，见各处 using）。</summary>
    private static CoMem Utf8(string s) => new(Marshal.StringToCoTaskMemUTF8(s));

    private readonly struct CoMem : IDisposable
    {
        public IntPtr Ptr { get; }
        public CoMem(IntPtr ptr) => Ptr = ptr;
        public void Dispose()
        {
            if (Ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(Ptr);
        }
    }

    // ── C ABI（见 chenge-runtime/include/chenge_embed.h，ABI=1） ──

    private delegate int AbiFn();
    private delegate int OpenFn(IntPtr dir, IntPtr dataDir, byte[] err, int errLen);
    private delegate int OpenMemFn(IntPtr dataDir, byte[] err, int errLen);
    private delegate void ProjectClearFn();
    private delegate int ProjectPutFn(IntPtr path, byte[] data, int len);
    private delegate void TickFn();
    private delegate void PointerFn(int x, int y, int down);
    private delegate IntPtr FrameFn(out int w, out int h, out int stride);
    private delegate int ScreenFn(out int w, out int h);
    private delegate int SaveBmpFn(IntPtr path);
    private delegate int TagBoolFn(IntPtr name, out int value);
    private delegate int TagNumFn(IntPtr name, out double value);
    private delegate int TagKindFn(IntPtr name);
    private delegate void CloseFn();
    private delegate IntPtr LastErrorFn();
}

/// <summary>
/// 预览会话的两种实现共用的面：进程内（本库）与将来的隔离进程宿主。
/// 编辑器只依赖这个接口，换实现不动界面。
/// </summary>
public interface IChengePreviewSession : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Stride { get; }
    double Fps { get; }
    bool IsOpen { get; }
    string LastError { get; }
    long FrameVersion { get; }

    /// <summary>宿主日志（隔离模式有；进程内模式为空，日志直接进编辑器控制台）。</summary>
    IReadOnlyList<string> Log { get; }

    /// <summary>请求刷新一个变量的值（隔离模式发一条查询；进程内模式无需动作）。</summary>
    void PollTag(string name);

    /// <summary>取上一次轮询到的值（显示用字符串，质量差时以 "?" 表示）。</summary>
    bool TryGetTag(string name, out string value);

    void Pointer(int x, int y, bool down);
    bool TryCopyFrame(byte[] dest);
    bool SaveScreenshot(string path);

    /// <summary>
    /// 设计模式请求重绘一帧（不推进时间）。隔离宿主走 <c>R</c> 命令；
    /// 进程内模式由调用方 tick 驱动，没有这条通道 ⇒ 返回 false。
    /// </summary>
    bool RenderOnce();
}
