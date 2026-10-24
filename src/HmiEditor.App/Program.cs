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

        var projectDir = args[1];
        if (!Directory.Exists(projectDir))
        {
            Console.WriteLine($"FAIL: 工程目录不存在：{projectDir}");
            return 2;
        }

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
        try
        {
            using var session = new ChengeEmbedSession(lib, dataDir);
            if (!session.Open(projectDir))
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

    /// <summary>安装文件日志器（写 %APPDATA%/zhishu-hmi/logs）。</summary>
    private static void InstallLogging()
    {
        AppLog.Current = new FileLogger(FileLogger.DefaultLogDirectory);
        AppLog.Info($"=== zhishu-hmi 启动 (pid={Environment.ProcessId}, " +
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
