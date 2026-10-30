using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using HmiEditor.App.Services;
using HmiEditor.Core.Diagnostics;
using HmiEditor.Core.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HmiEditor.App;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 最早时机安装日志与全局异常处理：启动阶段（含 Avalonia 初始化）的异常
        // 之前完全没有落点，用户侧表现为"闪退且无任何日志"。
        InstallLogging();
        InstallGlobalExceptionHandling();

        // 无界面自检：验证 P/Invoke 与运行时库的接线（CI 与"装完先自检"用）。
        // 它不启动 Avalonia，所以能在无显示环境里跑。
        if (args.Length > 0 && args[0] == "--selftest-embed")
        {
            Environment.ExitCode = RunEmbedSelftest(args);
            return;
        }

        // 隔离宿主自检（Windows 上唯一可用的后端）：不启动 Avalonia，直接在控制台
        // 把"找得到运行时 → 起得来 → 出得了帧 → 点得动"四步逐条判掉。
        // 用户在别的机器上出问题时，这一条命令的输出就是全部线索。
        if (args.Length > 0 && args[0] == "--selftest-host")
        {
            Environment.ExitCode = RunHostSelftest(args);
            return;
        }

        try
        {
            // Avalonia 12.x 在 Linux/Wayland 下弹窗定位异常的修复：
            // 启用独立窗口弹出模式（而非嵌入视图），使弹窗能正确定位到屏幕坐标。
            AppContext.SetSwitch("Avalonia.Popups.UsePopupWindow", true);

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Error("Avalonia 启动失败", ex);
            throw;
        }
    }

    /// <summary>
    /// `--selftest-embed &lt;工程目录&gt;`：加载运行时库、建页、取一帧、点一下按钮、读变量。
    ///
    /// <para>它挡的是 P/Invoke 那层最容易错的接线：C ABI 的参数顺序、UTF-8 指针、
    /// 帧的字节序、以及"点了没反应"（指针注入没接上）。任何一条不过就非零退出，
    /// 输出里带原因；库找不到/ABI 不匹配也在这里先暴露，而不是等用户点开预览窗。</para>
    /// </summary>
    private static int RunEmbedSelftest(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：--selftest-embed <工程目录>   （库目录取 CHENGE_RUNTIME_DIR 或工程所在的运行时目录）");
            return 64;
        }

        var projectDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (projectDir == null || !Directory.Exists(projectDir))
        {
            Console.WriteLine($"FAIL: 工程目录不存在：{projectDir}");
            return 2;
        }
        var useMemory = args.Contains("--mem");

        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir))
        {
            Console.WriteLine("FAIL: 没有 CHENGE_RUNTIME_DIR（要指向 make EMBED=1 产物的目录）");
            return 2;
        }
        if (!ChengeEmbedSession.TryLocate(runtimeDir, out var lib))
        {
            Console.WriteLine($"FAIL: {runtimeDir} 下没有 {ChengeEmbedSession.LibraryName}");
            return 2;
        }

        var dataDir = AppPaths_ForSelftest();

        // 隔离宿主自检：只验"共享内存帧 + stdin 输入"这两条通道（变量在子进程里，读不到）
        if (args.Contains("--iso"))
        {
            if (!ChengeProcessSession.TryLocate(runtimeDir, out var hostExe))
            {
                Console.WriteLine($"FAIL: {runtimeDir} 下没有 {ChengeProcessSession.ExecutableName}（make PREVIEW=1）");
                return 2;
            }
            using var proc = new ChengeProcessSession(hostExe, dataDir);
            var opened = useMemory
                ? proc.OpenWithBlob(ReadProjectFiles(projectDir))
                : proc.Open(projectDir);
            if (!opened)
            {
                Console.WriteLine($"FAIL: 宿主打开失败：{proc.LastError}");
                return 3;
            }
            Thread.Sleep(500);
            var buf = new byte[proc.Stride * proc.Height];
            if (!proc.TryCopyFrame(buf))
            {
                Console.WriteLine("FAIL: 读不到共享内存帧");
                return 4;
            }
            var step = 4 * 97;
            var nz = 0;
            var total = 0;
            for (var i = 0; i + 2 < buf.Length; i += step, total++)
                if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 0) nz++;
            Console.WriteLine($"frame {proc.Width}x{proc.Height} stride={proc.Stride} 非黑 {(double)nz / total:P1}");

            proc.Pointer(100, 148, true);
            Thread.Sleep(120);
            proc.Pointer(100, 148, false);
            Thread.Sleep(500);

            var hit = proc.Log.Any(l => l.Contains("tag-write"));
            if (!hit)
            {
                Console.WriteLine("FAIL: 点击没写进变量");
                return 7;
            }

            // 变量监视通道：T 查询 → V 回报（编辑器右侧面板走的就是这条）
            proc.PollTag("启动");
            Thread.Sleep(300);
            var got = proc.TryGetTag("启动", out var tagValue);
            Console.WriteLine(got ? $"变量监视：启动 = {tagValue}" : "FAIL: 变量监视读不到");
            if (!got || tagValue != "1") return 8;

            // 整包重载（K）：同一份工程再推一次，进程不重启、画面还在。
            // 这条通道是"改变量表不白屏"的唯一安全路径，接线错了要在这里拦住。
            {
                var epoch = (uint)Environment.TickCount;
                var reloaded = proc.ReloadProject(epoch, ReadProjectFiles(projectDir));
                Thread.Sleep(600);
                var tail = proc.Log.Where(l => l.Contains("reload:") || l.Contains("K epoch")).ToList();

                if (!reloaded)
                {
                    Console.WriteLine("FAIL: 整包重载发送失败");
                    return 9;
                }
                if (proc.HostExited)
                {
                    Console.WriteLine($"FAIL: 整包重载后宿主退出（code={proc.HostExitCode}）：{proc.LogTail}");
                    return 9;
                }
                if (!proc.TryCopyFrame(buf))
                {
                    Console.WriteLine("FAIL: 整包重载后读不到帧");
                    return 9;
                }
                nz = 0;
                total = 0;
                for (var i = 0; i + 2 < buf.Length; i += step, total++)
                    if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 0) nz++;
                var ratio = total == 0 ? 0 : (double)nz / total;
                Console.WriteLine($"整包重载后 非黑 {ratio:P1}  {string.Join(" | ", tail)}");
                if (ratio < 0.01)
                {
                    Console.WriteLine("FAIL: 整包重载后画面是黑的");
                    return 9;
                }
            }

            Console.WriteLine("OK: 隔离宿主 帧 + 点击写变量 + 变量监视 + 整包重载 全部通过");
            return 0;
        }

        try
        {
            using var session = new ChengeEmbedSession(lib, dataDir);
            var opened = false;
            if (useMemory)
            {
                // 把目录里的工程文件塞进内存表，再从内存启动（验证 project_put/open_mem 这条路）
                session.ClearProjectFiles();
                var n = 0;
                foreach (var file in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
                    if (!session.PutFile(rel, File.ReadAllBytes(file)))
                    {
                        Console.WriteLine($"FAIL: project_put 失败：{rel}");
                        return 3;
                    }
                    n++;
                }
                Console.WriteLine($"内存工程：{n} 个文件");
                opened = session.OpenMemory();
            }
            else
            {
                opened = session.Open(projectDir);
            }
            if (!opened)
            {
                Console.WriteLine($"FAIL: 打开工程失败：{session.LastError}");
                return 3;
            }

            Thread.Sleep(300);   // 让 tick 线程跑几帧
            var frame = new byte[session.Stride * session.Height];
            if (!session.TryCopyFrame(frame))
            {
                Console.WriteLine("FAIL: 取不到帧");
                return 4;
            }

            var step = 4 * 97;
            var nonBlack = 0;
            var total = 0;
            for (var i = 0; i + 2 < frame.Length; i += step, total++)
                if (frame[i] != 0 || frame[i + 1] != 0 || frame[i + 2] != 0) nonBlack++;
            var ratio = total == 0 ? 0 : (double)nonBlack / total;
            Console.WriteLine($"frame {session.Width}x{session.Height} stride={session.Stride} 非黑 {ratio:P1}");
            if (ratio < 0.01)
            {
                Console.WriteLine("FAIL: 画面几乎全黑");
                return 5;
            }

            if (!session.TryGetBool("启动", out var before))
            {
                Console.WriteLine("FAIL: 工程里没有变量「启动」（本自检按 spec/golden/g01 写）");
                return 6;
            }
            Console.WriteLine($"点击前 启动={before}");

            session.Pointer(100, 148, true);
            Thread.Sleep(80);
            session.Pointer(100, 148, false);
            Thread.Sleep(300);

            if (!session.TryGetBool("启动", out var after) || !after)
            {
                Console.WriteLine($"FAIL: 点击没写进变量（启动={after}）");
                return 7;
            }
            Console.WriteLine($"点击后 启动={after}");
            Console.WriteLine("OK: 库加载 / 建页 / 出图 / 点击写变量 全部通过");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 9;
        }
    }

    /// <summary>
    /// 自检用的数据目录（运行期日志/趋势都落这里，不碰工程目录）。
    /// 走 <see cref="AppPaths.TempSubdir"/>：应用数据根只允许 AppPaths 解析。
    /// </summary>
    private static string AppPaths_ForSelftest() => AppPaths.TempSubdir("chenge-embed-selftest");

    /// <summary>
    /// `--selftest-host [工程目录]`：隔离宿主的无界面自检。
    ///
    /// <para>不带参数就用**随包示例工程** <c>&lt;运行时目录&gt;/sample</c>，所以在用户机上
    /// 只需要一条命令，不用先找工程、也不用先配运行时目录。四步各自给结论：
    /// ① 定位运行时（找不到就列出找过的目录）② 起进程并等到首帧
    /// ③ 取一帧看是否真有像素 ④ 注一次点击再看帧序号是否推进。</para>
    /// </summary>
    private static int RunHostSelftest(string[] args)
    {
        var runtime = ChengeExportService.LocateRuntime(null);
        if (runtime == null)
        {
            Console.WriteLine("FAIL: 找不到运行时。已找过：");
            foreach (var dir in ChengeExportService.RuntimeSearchDirs(null))
                Console.WriteLine("  · " + dir);
            return 2;
        }

        // 自检要的是**隔离宿主**（吃共享内存 + stdin 协议），不是截图用的进程版：
        // 同一目录里两个都在时，按 BinaryPath 拿到的可能是进程版 —— 它会正常进主循环，
        // 只是永远不建共享内存，现象是"等不到首帧"（这里踩过一次）。
        if (!ChengeProcessSession.TryLocate(runtime.RuntimeDir, out var hostExe))
        {
            Console.WriteLine($"FAIL: {runtime.RuntimeDir} 下没有 {ChengeProcessSession.ExecutableName}");
            return 2;
        }
        Console.WriteLine($"运行时：{hostExe}");
        var projectDir = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                         ?? Path.Combine(runtime.RuntimeDir, "sample");
        if (!Directory.Exists(projectDir))
        {
            Console.WriteLine($"FAIL: 工程目录不存在：{projectDir}（也可以自己给一个导出过的工程目录）");
            return 2;
        }
        Console.WriteLine($"工程  ：{projectDir}");

        var dataDir = AppPaths.TempSubdir("chenge-host-selftest");
        using var proc = new ChengeProcessSession(hostExe, dataDir);
        if (!proc.Open(projectDir))
        {
            Console.WriteLine($"FAIL: 宿主打开失败：{proc.LastError}");
            Console.WriteLine("宿主日志：\n" + proc.LogTail);
            return 3;
        }
        Console.WriteLine($"通道  ：{proc.Width}x{proc.Height} stride={proc.Stride}");

        // 等首帧：宿主建页在子进程里，慢机器上要几百毫秒
        var version = proc.FrameVersion;
        var deadline = Environment.TickCount64 + 8000;
        while (proc.FrameVersion == 0 && Environment.TickCount64 < deadline) Thread.Sleep(50);
        if (proc.FrameVersion == 0)
        {
            Console.WriteLine("FAIL: 等不到首帧（共享内存一直没更新）");
            Console.WriteLine("宿主日志：\n" + proc.LogTail);
            return 4;
        }

        var buf = new byte[proc.Stride * proc.Height];
        if (!proc.TryCopyFrame(buf))
        {
            Console.WriteLine("FAIL: 读不到共享内存帧");
            Console.WriteLine("宿主日志：\n" + proc.LogTail);
            return 5;
        }

        var step = 4 * 97;
        var nz = 0;
        var total = 0;
        for (var i = 0; i + 2 < buf.Length; i += step, total++)
            if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 0) nz++;

        var lit = (double)nz / Math.Max(total, 1);
        Console.WriteLine($"首帧  ：非黑 {lit:P1}（{version} → {proc.FrameVersion}）");
        if (lit < 0.01)
        {
            Console.WriteLine("FAIL: 画面几乎全黑 —— 帧是空的（建页失败？字体/资源没读到？）");
            Console.WriteLine("宿主日志：\n" + proc.LogTail);
            return 6;
        }

        // 点一下示例里的按钮（40,110 起 160x48）：按下会改它的按下态 ⇒ 像素变 ⇒ 帧序号推进。
        // 点空白也能"通"，但像素不变就不会推帧序号（宿主只在画面真变时才发帧），
        // 那样的"没推进"会让人误判成指针没接上。
        var before = proc.FrameVersion;
        proc.Pointer(120, 134, true);
        Thread.Sleep(200);
        proc.Pointer(120, 134, false);
        Thread.Sleep(400);
        Console.WriteLine($"点击  ：帧序号 {before} → {proc.FrameVersion}"
                          + (proc.FrameVersion > before
                              ? "（推进了 —— stdin 指针通道通）"
                              : "（没推进：指针没接上，或这个位置没有会变的控件）"));

        // 整包重载（K）：改变量表后"不重启进程也不白屏"靠这条通道。
        // 用同一份工程重推一次，只验接线与"画面还在"；语义（按名字保值）在
        // 运行时的 repro 脚本里逐情形验过。
        {
            var epoch = (uint)Environment.TickCount;
            var files = ReadProjectFiles(projectDir);
            var sent = proc.ReloadProject(epoch, files);
            Thread.Sleep(700);
            var reloadLog = proc.Log.Where(l => l.Contains("reload:") || l.Contains("K epoch")).ToList();

            if (!sent)
            {
                Console.WriteLine("FAIL: 整包重载发送失败（K 命令写不进 stdin）");
                return 8;
            }
            if (proc.HostExited)
            {
                Console.WriteLine($"FAIL: 整包重载后宿主退出（code={proc.HostExitCode}）");
                Console.WriteLine("宿主日志：\n" + proc.LogTail);
                return 8;
            }
            if (!proc.TryCopyFrame(buf))
            {
                Console.WriteLine("FAIL: 整包重载后读不到帧");
                return 8;
            }
            nz = 0;
            total = 0;
            for (var i = 0; i + 2 < buf.Length; i += step, total++)
                if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 0) nz++;
            var after = (double)nz / Math.Max(total, 1);
            Console.WriteLine($"重载  ：非黑 {after:P1}　{string.Join(" | ", reloadLog)}");
            if (after < 0.01)
            {
                Console.WriteLine("FAIL: 整包重载后画面是黑的");
                return 8;
            }
        }

        Console.WriteLine("宿主日志（尾部）：\n" + proc.LogTail);
        Console.WriteLine("OK: 隔离宿主可用（定位 / 起进程 / 出帧 / 指针 / 整包重载 五步都过了）");
        return 0;
    }

    /// <summary>把一个已导出的工程目录读成"相对路径 → 字节"，模拟编辑器内存里的产物。</summary>
    private static Dictionary<string, byte[]> ReadProjectFiles(string dir)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            files[rel] = File.ReadAllBytes(file);
        }
        return files;
    }

    /// <summary>安装文件日志器（写 %APPDATA%/chenge-hmi/logs）。</summary>
    private static void InstallLogging()
    {
        AppLog.Current = new FileLogger(FileLogger.DefaultLogDirectory);
        AppLog.Info($"=== chenge-hmi 启动 (pid={Environment.ProcessId}, " +
                    $"os={Environment.OSVersion}, clr={Environment.Version}) ===");
    }

    /// <summary>
    /// 安装三层全局异常处理。UI 线程异常记录后标记 Handled，避免一次未处理异常
    /// 直接终止整个编辑器。
    ///
    /// 注意这是「最后一道防线」而非主要手段：视图层的事件处理器异常已由
    /// <see cref="HmiEditor.App.Services.UiOperationGuard"/> 收敛（落盘日志 + 界面可见），
    /// 目前仓库内 35 处 <c>async void</c> 全部自带 try/catch 或走该守卫。
    /// </summary>
    private static void InstallGlobalExceptionHandling()
    {
        // 1) UI 线程（Dispatcher）—— async void 事件处理器的异常最终会到这里
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("UI 线程未处理异常（已拦截，编辑器继续运行）", e.Exception);
            e.Handled = true;
        };

        // 2) 后台线程未捕获异常（无法阻止进程终止，但至少留下日志）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error(
                $"非 UI 线程致命异常 (terminating={e.IsTerminating})",
                e.ExceptionObject as Exception);
        };

        // 3) 未被观察的 Task 异常（async void / 忘记 await 的常见后果）
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("未观察的 Task 异常（已标记观察，避免进程终止）", e.Exception);
            e.SetObserved();
        };
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // Avalonia 12.1 + FluentAvaloniaUI 3.0.2 在 Linux 下存在默认字体($Default)创建失败
        // 的兼容问题（Could not create glyphTypeface）。仅 Linux 需要显式指定系统字体族
        // 绕过；Windows/macOS 无此问题，保持平台默认字体。
        if (OperatingSystem.IsLinux())
        {
            var fontOptions = new FontManagerOptions
            {
                DefaultFamilyName = "Noto Sans CJK SC"
            };
            builder = builder.With(fontOptions);
        }

        return builder;
    }
}
