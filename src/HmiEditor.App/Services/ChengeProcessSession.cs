using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using HmiEditor.App.Resources;

namespace HmiEditor.App.Services;

/// <summary>
/// 隔离预览：编辑器只与子进程 <c>chenge-preview-host</c> 打交道。
///
/// <para><b>为什么默认走这条</b>：预览里会跑用户写的 Lua，死循环/越界/原生接口 bug
/// 都可能发生。进程内加载 .so 时这些会直接把编辑器带走（未保存的工程一起没）。
/// 子进程里崩了只死预览。</para>
///
/// <para><b>通道</b>（与 <c>chenge-runtime/src/preview_host.c</c> 逐字段对应）：
/// 帧 = 共享内存（40 字节头 + 整屏 ARGB8888，头里 <c>seq</c> 每帧 +1）；
/// 输入 = 标准输入一行一条 <c>P x y down</c>；日志 = 标准错误按行转发。</para>
///
/// <para><b>父进程退出</b>：标准输入被关闭 ⇒ 宿主读到 EOF 自己退出，不留孤儿。</para>
/// </summary>
public sealed class ChengeProcessSession : IChengePreviewSession
{
    private const string ExeLinux = "chenge-preview-host";
    private const string ExeWindows = "chenge-preview-host.exe";
    private const uint FrameMagic = 0x46474843;   // 'CHGF'
    private const int SeqOffset = 24;             // 帧计数器：version 1/2 都在这个偏移
    private const int HeaderBytesOffset = 20;     // version 2 起：像素起点由宿主播报
    private const uint SupportedFrameVersion = 2;

    private readonly string _exePath;
    private readonly string _dataDir;
    private readonly string _shmName;
    private readonly string _shmPath;
    private readonly bool _designMode;
    private readonly List<string> _log = new();
    private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);

    /// <summary>像素起点：由帧头的 header_bytes 决定，不再写死 40（写死过一次，与宿主差 8 字节）。</summary>
    private int _pixelOffset = 40;

    private Process? _proc;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private Thread? _logThread;
    private Thread? _outThread;
    private Stream? _stdin;
    private volatile bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public double Fps { get; private set; }
    public bool IsOpen => _proc is { HasExited: false } && _view != null;
    public string LastError { get; private set; } = "";
    public long FrameVersion => _view == null ? 0 : (long)_view.ReadUInt64(SeqOffset);

    /// <summary>最近若干行宿主日志（输出面板/错误提示用）。</summary>
    public IReadOnlyList<string> Log
    {
        get { lock (_log) return _log.ToArray(); }
    }

    /// <param name="designMode">
    /// true = 设计画布：宿主不推进时间，只在收到 <c>R</c> 时重绘（静止的真图，可逐像素对拍）。
    /// false = 仿真预览：照旧按 tick 驱动。
    /// </param>
    public ChengeProcessSession(string exePath, string dataDir, bool designMode = false)
    {
        _exePath = exePath;
        _dataDir = dataDir;
        _designMode = designMode;
        _shmName = "/chenge-preview-" + Guid.NewGuid().ToString("N")[..12];
        _shmPath = Path.Combine("/dev/shm", _shmName.TrimStart('/'));
    }

    public static string ExecutableName => OperatingSystem.IsWindows() ? ExeWindows : ExeLinux;

    public static bool TryLocate(string? runtimeDir, out string exePath)
    {
        exePath = "";
        if (string.IsNullOrWhiteSpace(runtimeDir)) return false;
        var p = Path.Combine(runtimeDir, ExecutableName);
        if (!File.Exists(p)) return false;
        exePath = p;
        return true;
    }

    /// <summary>从磁盘目录启动（正式导出产物所在）。</summary>
    public bool Open(string projectDir)
        => StartHost(stdinProject: false, projectDir) && WaitSharedMemory();

    /// <summary>
    /// 从内存把工程推给宿主：隔离进程 + 不落盘同时成立。
    /// 分块格式见 <c>preview_host.c</c> 的 <c>read_project_blob</c>。
    /// </summary>
    public bool OpenWithBlob(IReadOnlyDictionary<string, byte[]> files)
    {
        if (!StartHost(stdinProject: true, projectDir: null)) return false;

        try
        {
            foreach (var (rel, bytes) in files)
            {
                WriteStdin(Encoding.UTF8.GetBytes($"F {rel} {bytes.Length}\n"));
                WriteStdin(bytes);
                WriteStdin(new[] { (byte)'\n' });
            }
            WriteStdin(Encoding.UTF8.GetBytes("E\n"));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
        return WaitSharedMemory();
    }

    private bool StartHost(bool stdinProject, string? projectDir)
    {
        if (OperatingSystem.IsWindows())
        {
            LastError = AppLocalization.Service["live.err.windows_unsupported"];
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(_exePath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (stdinProject)
            {
                psi.ArgumentList.Add("--project-stdin");
            }
            else
            {
                psi.ArgumentList.Add("--project");
                psi.ArgumentList.Add(projectDir!);
            }
            psi.ArgumentList.Add("--shm");
            psi.ArgumentList.Add(_shmName);
            psi.ArgumentList.Add("--data");
            psi.ArgumentList.Add(_dataDir);
            if (_designMode) psi.ArgumentList.Add("--design");

            _proc = Process.Start(psi);
            if (_proc == null)
            {
                LastError = AppLocalization.Service["live.err.host_start"];
                return false;
            }

            _stdin = _proc.StandardInput.BaseStream;
            _logThread = new Thread(ReadLog) { IsBackground = true, Name = "chenge-host-log" };
            _logThread.Start();
            _outThread = new Thread(ReadValues) { IsBackground = true, Name = "chenge-host-tags" };
            _outThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>等宿主把共享内存建好（含整屏 ftruncate，通常 &lt; 200ms）。</summary>
    private bool WaitSharedMemory()
    {
        var deadline = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < deadline)
        {
            if (_proc is { HasExited: true })
            {
                LastError = AppLocalization.Service.Format("live.err.host_exit", _proc.ExitCode.ToString(), LastLog());
                return false;
            }
            if (File.Exists(_shmPath) && new FileInfo(_shmPath).Length > _pixelOffset)
            {
                _mmf = MemoryMappedFile.CreateFromFile(_shmPath, FileMode.Open, null, 0,
                    MemoryMappedFileAccess.Read);
                _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (_view.ReadUInt32(0) == FrameMagic)
                {
                    var version = _view.ReadUInt32(4);
                    Width = (int)_view.ReadUInt32(8);
                    Height = (int)_view.ReadUInt32(12);
                    Stride = (int)_view.ReadUInt32(16);
                    if (version != SupportedFrameVersion)
                    {
                        // 不降级：version 1 的头没有 header_bytes，旧两侧各写一个常量正是错位来源。
                        // 诊断串只用 ASCII —— 面向用户的文案走资源键，不要在这里拼中文。
                        LastError = AppLocalization.Service.Format("live.err.shm_header",
                            $"version {version} (need {SupportedFrameVersion})", Stride.ToString());
                        return false;
                    }
                    _pixelOffset = (int)_view.ReadUInt32(HeaderBytesOffset);
                    if (Width > 0 && Height > 0 && Stride >= Width * 4 &&
                        _pixelOffset > 0 && _pixelOffset < 4096) return true;
                    LastError = AppLocalization.Service.Format("live.err.shm_header", $"{Width}x{Height}", Stride.ToString());
                    return false;
                }
            }
            Thread.Sleep(50);
        }

        LastError = AppLocalization.Service["live.err.shm_timeout"];
        return false;
    }

    private readonly object _stdinLock = new();

    private void WriteStdin(byte[] bytes)
    {
        lock (_stdinLock)
        {
            _stdin!.Write(bytes, 0, bytes.Length);
            _stdin.Flush();
        }
    }

    /// <summary>标准输出只承载变量回报：`V <名字> <值> <质量>`。</summary>
    private void ReadValues()
    {
        try
        {
            while (_proc != null && !_proc.StandardOutput.EndOfStream)
            {
                var line = _proc.StandardOutput.ReadLine();
                if (line == null) break;
                if (line.Length < 3 || line[0] != 'V' || line[1] != ' ') continue;

                var parts = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                // 质量：0 = GOOD（值有效），非 0 = 未初始化/坏 ⇒ 显示 ? 而不是假值
                var shown = parts[2] == "0" ? parts[1] : "?";
                lock (_tags) _tags[parts[0]] = shown;
            }
        }
        catch
        {
            // 进程退出时流会断，正常
        }
    }

    public void PollTag(string name)
    {
        if (_proc is not { HasExited: false }) return;
        try
        {
            WriteStdin(Encoding.UTF8.GetBytes($"T {name}\n"));
        }
        catch (IOException)
        {
        }
    }

    public bool TryGetTag(string name, out string value)
    {
        lock (_tags)
        {
            if (_tags.TryGetValue(name, out var v))
            {
                value = v;
                return true;
            }
        }
        value = "";
        return false;
    }

    private void ReadLog()
    {
        try
        {
            while (_proc != null && !_proc.StandardError.EndOfStream)
            {
                var line = _proc.StandardError.ReadLine();
                if (line == null) break;
                lock (_log)
                {
                    _log.Add(line);
                    if (_log.Count > 400) _log.RemoveAt(0);
                }
            }
        }
        catch
        {
            // 进程退出时流会断，正常
        }
    }

    private string LastLog()
    {
        lock (_log)
        {
            for (var i = _log.Count - 1; i >= 0; i--)
                if (_log[i].Length > 0) return _log[i];
        }
        return "";
    }

    public void Pointer(int x, int y, bool down)
    {
        if (_proc is not { HasExited: false }) return;
        try
        {
            WriteStdin(Encoding.UTF8.GetBytes($"P {x} {y} {(down ? 1 : 0)}\n"));
        }
        catch (IOException)
        {
            // 宿主已死，下一次 Pump 会看到 IsOpen=false
        }
    }

    public bool TryCopyFrame(byte[] dest)
    {
        if (_view == null || dest.Length < Stride * Height) return false;
        _view.ReadArray(_pixelOffset, dest, 0, Stride * Height);
        return true;
    }

    /// <summary>
    /// 设计模式：让宿主重绘一帧（不推进时间）。宿主收到后 render_once + seq++，
    /// 客户端按 seq 变化判断位图是否已经包含这次改动。
    /// </summary>
    public bool RenderOnce()
    {
        if (_proc is not { HasExited: false }) return false;
        try
        {
            WriteStdin(Encoding.UTF8.GetBytes("R\n"));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 整页重载：<paramref name="pageId"/> 为空 = 重建当前页（改绑定/权限的慢路径）。
    /// epoch 由客户端递增，宿主据此把更旧的补丁丢掉（补丁接上后才有消费者）。
    /// </summary>
    public bool ReloadPage(uint epoch, string? pageId)
    {
        if (_proc is not { HasExited: false }) return false;
        try
        {
            var cmd = string.IsNullOrEmpty(pageId) ? $"L {epoch}\n" : $"L {epoch} {pageId}\n";
            WriteStdin(Encoding.UTF8.GetBytes(cmd));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public bool SaveScreenshot(string path)
    {
        if (_proc is not { HasExited: false }) return false;
        try
        {
            WriteStdin(Encoding.UTF8.GetBytes("S " + path + "\n"));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_proc is { HasExited: false })
            {
                try { WriteStdin(Encoding.UTF8.GetBytes("Q\n")); } catch { }
                if (!_proc.WaitForExit(1500)) _proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 关窗时进程已经没了也正常
        }
        finally
        {
            _view?.Dispose();
            _mmf?.Dispose();
            _view = null;
            _mmf = null;
            _proc?.Dispose();
            _proc = null;
            try { if (File.Exists(_shmPath)) File.Delete(_shmPath); } catch { }
        }
    }
}
