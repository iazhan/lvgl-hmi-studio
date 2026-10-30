using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
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
    private readonly string _shmMapName;
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

    /// <summary>
    /// 活着的宿主进程。编辑器被强杀（kill / 关终端 / 崩溃）时 <see cref="Dispose"/> 不会跑，
    /// 宿主就变成孤儿：每个都占着 800×480 的共享内存和一份 LVGL 缓冲。实测一次工作区里堆了
    /// 53 个、/dev/shm 368MB —— 再开一个新会话就抢不到资源，画布一片空白（"点两次开关才好"
    /// 正是第二次抢到了）。所以：进程收到 SIGTERM/SIGINT 时统一收尸。
    /// </summary>
    private static readonly List<Process> s_live = new();
    private static bool s_exitHookInstalled;

    private static void InstallExitHook()
    {
        if (s_exitHookInstalled) return;
        s_exitHookInstalled = true;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillLive();
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
        {
            try
            {
                PosixSignalRegistration.Create(sig, ctx =>
                {
                    KillLive();
                    ctx.Cancel = false;      /* 收完尸照常按默认行为退出 */
                });
            }
            catch
            {
                // 不支持的平台（Windows 只有 SIGINT/SIGTERM 的一支）忽略
            }
        }
    }

    private static void KillLive()
    {
        Process[] snapshot;
        lock (s_live) snapshot = s_live.ToArray();
        foreach (var p in snapshot)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>
    /// 清掉上一轮留下的孤儿宿主（父进程已死 ⇒ ppid=1）。编辑器被 SIGKILL、测试进程被运行器
    /// 直接杀掉时，上面的退出钩子也跑不到；这些宿主会一直攥着共享内存与 LVGL 缓冲。
    /// 返回清掉的个数。
    /// </summary>
    public static int ReapOrphans()
    {
        if (!OperatingSystem.IsLinux()) return 0;

        var killed = 0;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;

            // 认进程按 **cmdline** 而不是 stat 的 comm：内核把 comm 截到 15 字符，
            // "chenge-preview-host" 会变成 "chenge-preview-"，按全名匹配一个都认不出来。
            string[] argv;
            try { argv = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0'); }
            catch { continue; }
            if (!argv.Any(a => a.EndsWith(ExeLinux, StringComparison.Ordinal))) continue;

            string stat;
            try { stat = File.ReadAllText(Path.Combine(dir, "stat")); } catch { continue; }

            // /proc/<pid>/stat: pid (comm) state ppid ... —— comm 里可能有空格，取最后一个 ')'
            var close = stat.LastIndexOf(')');
            if (close < 0) continue;
            var rest = stat[(close + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length < 2 || rest[1] != "1") continue;      // 第 2 段是 ppid

            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                killed++;
                try { File.Delete(Path.Combine("/dev/shm", ShmNameOf(p))); } catch { }
            }
            catch
            {
                // 正好自己退了
            }
        }
        return killed;
    }

    /// <summary>从宿主命令行里取 <c>--shm /name</c>，用来删掉它那份共享内存文件。</summary>
    private static string ShmNameOf(Process p)
    {
        try
        {
            var cmd = File.ReadAllText($"/proc/{p.Id}/cmdline").Split('\0');
            for (var i = 0; i + 1 < cmd.Length; i++)
                if (cmd[i] == "--shm") return cmd[i + 1].TrimStart('/');
        }
        catch
        {
            // 读不到就算了，shm 文件留着无害（下次按名字复用的极少数情况下会重新建）
        }
        return "";
    }

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
        // Linux：POSIX 共享内存落在 /dev/shm 下，客户端按**路径**打开；
        // Windows：宿主用 CreateFileMapping 建**命名**映射，名字就是去掉前导 '/' 的那串，
        //          客户端按名字 MemoryMappedFile.OpenExisting 打开。头布局两边完全一致。
        _shmPath = Path.Combine("/dev/shm", _shmName.TrimStart('/'));
        _shmMapName = _shmName.TrimStart('/');
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
        try
        {
            // 编码必须显式给 UTF-8：不指定时 .NET 按 Console.OutputEncoding 解码子进程输出，
            // Windows 上那是控制台代码页（中文机 = GBK），而宿主一律写 UTF-8 ——
            // 表现就是实时预览的日志区全是乱码/问号（等价于"看不到日志"），
            // 变量回报（V 行）里的中文变量名也会对不上。
            var psi = new ProcessStartInfo(_exePath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
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

            InstallExitHook();
            lock (s_live) s_live.Add(_proc);

            _stdin = _proc.StandardInput.BaseStream;
            _logThread = new Thread(ReadLog) { IsBackground = true, Name = "chenge-host-log" };
            _logThread.Start();
            _outThread = new Thread(ReadValues) { IsBackground = true, Name = "chenge-host-tags" };
            _outThread.Start();
            StartFpsMonitor();
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
            if (OperatingSystem.IsWindows())
            {
                // 命名映射：宿主还没建出来时抛 FileNotFoundException，重试到超时
                try
                {
                    _mmf = MemoryMappedFile.OpenExisting(_shmMapName, MemoryMappedFileRights.Read);
                    _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
                catch (FileNotFoundException)
                {
                    Thread.Sleep(50);
                    continue;
                }
            }

            if (_view != null || (File.Exists(_shmPath) && new FileInfo(_shmPath).Length > _pixelOffset))
            {
                if (_view == null)
                {
                    _mmf = MemoryMappedFile.CreateFromFile(_shmPath, FileMode.Open, null, 0,
                        MemoryMappedFileAccess.Read);
                    _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
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
                        _pixelOffset > 0 && _pixelOffset < 4096)
                    {
                        MarkAlive();   // 刚建好，心跳从现在起算
                        return true;
                    }
                    LastError = AppLocalization.Service.Format("live.err.shm_header", $"{Width}x{Height}", Stride.ToString());
                    return false;
                }
            }
            Thread.Sleep(50);
        }

        LastError = AppLocalization.Service.Format("live.err.shm_timeout", LastLog());
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

    /// <summary>
    /// 宿主实际推进的帧率（按共享内存的 <c>seq</c> 增量算）。
    /// <para>实时预览据此与"界面真重绘了几次"对照：两者一起看才知道是宿主给不出帧，
    /// 还是给了帧但界面贴图/重绘跟不上。<c>Fps</c> 以前只有进程内后端会填，
    /// 隔离后端一直是 0 —— 界面上那个 host 数字因此永远是个 0。</para>
    /// </summary>
    private void StartFpsMonitor()
    {
        new Thread(() =>
        {
            var last = FrameVersion;
            var lastMs = Environment.TickCount64;

            while (!_disposed && IsOpen)
            {
                Thread.Sleep(500);
                var nowMs = Environment.TickCount64;
                var version = FrameVersion;
                var span = nowMs - lastMs;

                if (span < 400) continue;
                Fps = (version - last) * 1000.0 / span;
                last = version;
                lastMs = nowMs;
            }
            Fps = 0;
        })
        { IsBackground = true, Name = "chenge-host-fps" }.Start();
    }

    /// <summary>
    /// 宿主最近一次回应（任何一行 stdout）的时刻。判活用它而不是"帧序号有没有推进"：
    /// 画面静止时宿主本来就不产新帧，按帧判会把正常空闲当成卡死。
    /// </summary>
    private long _lastReplyMs;

    public long LastReplyMs => Interlocked.Read(ref _lastReplyMs);

    private void MarkAlive() => Interlocked.Exchange(ref _lastReplyMs, Environment.TickCount64);

    /// <summary>心跳：问一句宿主还在不在（H 命令，宿主回一行 A）。</summary>
    public bool Ping()
    {
        if (_proc is not { HasExited: false }) return false;
        try
        {
            WriteStdin(Encoding.UTF8.GetBytes("H\n"));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>标准输出只承载变量回报：`V <名字> <值> <质量>`。</summary>
    private void ReadValues()
    {
        var stdout = _proc?.StandardOutput;
        if (stdout == null) return;
        try
        {
            // 判 EOF 只看 ReadLine 返回 null，不先问 EndOfStream：后者要"探"一次缓冲，
            // Windows 的匿名管道上可能提前给出 true —— 线程就此静默退出，界面上表现为
            // "变量监视/日志永远是空的"，而宿主明明在输出。
            while (stdout.ReadLine() is { } line)
            {
                MarkAlive();   // 任何一行都算"宿主还活着"
                if (line.Length < 3 || line[0] != 'V' || line[1] != ' ') continue;

                var parts = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                // 质量：0 = GOOD（值有效），非 0 = 未初始化/坏 ⇒ 显示 ? 而不是假值
                var shown = parts[2] == "0" ? parts[1] : "?";
                lock (_tags) _tags[parts[0]] = shown;
            }
        }
        catch (Exception ex) when (ex is not (ObjectDisposedException or IOException))
        {
            // 宿主退出/被 Dispose 时流会断，属正常；其它异常必须留痕 ——
            // 此前这里是空 catch，"日志一片空白"连原因都查不到。
            EditorLog.File($"[host] 变量回报通道异常：{ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // 正常收尾
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
        // 诊断开关：HMI_HOST_LOG=<目录> 时把宿主 stderr 全量落盘（一行一条）。
        // 内存里只留最后 400 行、UI 又只显示最后一行，所以"宿主为什么拒了这份页面"
        // 这类问题不落盘就只能猜。默认不写 —— 编辑器是独立安装的软件，不乱建文件。
        StreamWriter? sink = null;
        var logDir = Environment.GetEnvironmentVariable("HMI_HOST_LOG");
        if (!string.IsNullOrWhiteSpace(logDir))
        {
            try
            {
                Directory.CreateDirectory(logDir);
                var name = Path.GetFileName(_shmName.TrimEnd('/'));
                var path = Path.Combine(logDir, $"host-{name}-{Environment.ProcessId}.log");
                sink = new StreamWriter(path, append: true) { AutoFlush = true };
            }
            catch
            {
                sink = null;
            }
        }

        var stderr = _proc?.StandardError;
        if (stderr == null)
        {
            sink?.Dispose();
            return;
        }

        try
        {
            // 同 ReadValues：EOF 只认 ReadLine 返回 null（Windows 匿名管道上
            // EndOfStream 的探测会提前结束循环，宿主日志就整段丢掉）。
            while (stderr.ReadLine() is { } line)
            {
                lock (_log)
                {
                    _log.Add(line);
                    if (_log.Count > 400) _log.RemoveAt(0);
                }
                // 同时落盘：Windows 上编辑器没有控制台，界面上那几行日志一旦被新会话冲掉
                // （崩溃重启会换进程），现场就没了。文件位置见 FileLogger。
                EditorLog.File("[host] " + line);
                try { sink?.WriteLine(line); } catch { /* 磁盘满/被删：诊断不能拖垮预览 */ }
            }
        }
        catch (Exception ex) when (ex is not (ObjectDisposedException or IOException))
        {
            EditorLog.File($"[host] 日志通道异常：{ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // 宿主退出/被 Dispose 时流会断，属正常
        }
        finally
        {
            sink?.Dispose();
        }
    }

    /// <summary>宿主最后一行日志（启动失败时贴出来：它自己会说"工程加载失败：…"）。</summary>
    public string LogTail => LastLog();

    /// <summary>
    /// 宿主进程是否已经退出。预览页每拍都查：进程死了还继续泵命令的话，界面表现为
    /// "隔离进程无响应"——其实进程早没了，日志也没人贴出来。
    /// </summary>
    public bool HostExited => _proc is { HasExited: true };

    /// <summary>宿主退出码（<see cref="HostExited"/> 为真时才有意义）。</summary>
    public int HostExitCode
    {
        get
        {
            try { return _proc is { HasExited: true } p ? p.ExitCode : 0; }
            catch { return -1; }
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
    /// 几何补丁：<c>W &lt;epoch&gt; &lt;seq&gt; &lt;widgetId&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt;</c>。
    /// 宿主只改位置尺寸、不重建控件（重建会丢事件与绑定）；x/y 是相对父容器的坐标，
    /// 与模型里的 X/Y 语义一致。
    /// </summary>
    public bool PatchRect(uint epoch, uint seq, string widgetId, int x, int y, int w, int h)
    {
        if (_proc is not { HasExited: false }) return false;
        try
        {
            WriteStdin(Encoding.UTF8.GetBytes($"W {epoch} {seq} {widgetId} {x} {y} {w} {h}\n"));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 用**最新页面 JSON** 重建当前页（设计画布停手后的精确刷新）。
    ///
    /// 不能用 <see cref="ReloadPage"/>：那条从宿主里的工程快照重读，而拖动只改了编辑器
    /// 模型，快照还是旧坐标 —— 会把刚拖走的控件拉回原位（实测踩过）。
    /// </summary>
    public bool ReloadPageJson(uint epoch, byte[] json)
    {
        if (_proc is not { HasExited: false } || json.Length == 0) return false;
        try
        {
            lock (_stdinLock)
            {
                var head = Encoding.UTF8.GetBytes($"J {epoch} {json.Length}\n");
                _stdin!.Write(head, 0, head.Length);
                _stdin.Write(json, 0, json.Length);
                _stdin.WriteByte((byte)'\n');
                _stdin.Flush();
            }
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
            if (_proc != null)
            {
                lock (s_live) s_live.Remove(_proc);
                _proc.Dispose();
            }
            _proc = null;
            try { if (File.Exists(_shmPath)) File.Delete(_shmPath); } catch { }
        }
    }
}
