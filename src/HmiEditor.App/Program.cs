using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using HmiEditor.App.Services;
using HmiEditor.Core.Diagnostics;
using HmiEditor.Core.Services;
using HmiEditor.Export.Chenge;
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

        // 设计画布增量重载自检（K）：改一个变量的前后，比字节/耗时，并用真宿主切页比像素。
        if (args.Length > 0 && args[0] == "--selftest-kdelta")
        {
            Environment.ExitCode = RunKDeltaSelftest(args);
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

    /// <summary>
    /// `--selftest-kdelta`：设计画布增量重载（<c>K</c>）的自检，判据全用真宿主。
    ///
    /// <para>工程是现场造的：<c>pages</c> 页，只有一页的 Bar 绑定了某个变量。然后
    /// ① 整包导出一次；② 删掉那个变量（后面的 tag 序号整体前移，被绑页必然过期）；
    /// ③ 走 <see cref="DesignIncrementalReload"/> 算增量包并比字节/耗时；
    /// ④ 两个宿主 A/B 对拍：A 打开最终整包当"正确答案"，B 打开旧整包再发增量 <c>K</c>，
    /// 都切到被绑那一页取帧比像素；⑤ 负对照：把增量包里的页去掉再发一次，画面必须与 A 不同
    /// —— 这一条证明"缺页 = 宿主里那页还是旧的"，不是可以省的优化。</para>
    /// </summary>
    private static int RunKDeltaSelftest(string[] args)
    {
        var pages = 20;
        for (var i = 1; i < args.Length; i++)
            if (args[i] == "--pages" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n >= 2)
                pages = n;

        var runtime = ChengeExportService.LocateRuntime(null);
        if (runtime == null || !ChengeProcessSession.TryLocate(runtime.RuntimeDir, out var hostExe))
        {
            Console.WriteLine("FAIL: 找不到隔离宿主（设置里指运行时目录，或 CHENGE_RUNTIME_DIR）");
            return 2;
        }

        var options = new ChengeExportOptions();
        var project = BuildKDeltaProject(pages);
        const string currentScreen = "s00";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var beforePack = new ChengeExporter().ExportToMemory(project, null, options, null, out var beforeResult);
        var beforeMs = watch.ElapsedMilliseconds;
        if (!beforeResult.Success)
        {
            Console.WriteLine("FAIL: 整包导出失败：" + beforeResult.Summary);
            return 3;
        }
        var pageRefs = ChengeIncrementalExport.PageTagRefsOf(beforePack);
        var boundPage = KDeltaPage;
        Console.WriteLine($"页面 tag 引用：" + string.Join(", ",
            pageRefs.Where(kv => kv.Value.Count > 0).Select(kv => $"{kv.Key}={kv.Value.Count}")) +
            $"（共 {pageRefs.Count} 页）");
        if (pageRefs.TryGetValue(boundPage, out var boundRefs) && boundRefs.Count == 0)
        {
            Console.WriteLine("导出问题：" + string.Join(" | ", beforeResult.Issues.Items.Select(i => $"{i.Severity} {i.Code}@{i.Location}:{i.Message}")));
            Console.WriteLine($"FAIL: {boundPage} 里没有任何 tag 引用，绑定没导出成功");
            return 3;
        }

        // 删掉被引用的变量：所有引用过它的页（以及序号被顶掉的页）都会过期。
        // 引用它的地方要一并清掉 —— 编辑器删变量就是这么做的，留着会让导出直接报
        // bind.tag_missing（那样就没有"改完继续跑"可言了）。
        var victim = project.VariableManager.Variables.First(v => v.Id == KDeltaBoundVariable);
        project.VariableManager.Variables.Remove(victim);
        foreach (var control in project.Screens.SelectMany(s => s.Controls))
            control.VariableBindings.Clear();
        var afterPack = new ChengeExporter().ExportToMemory(project, null, options, null, out var afterResult);
        if (!afterResult.Success)
        {
            Console.WriteLine("FAIL: 删变量后整包导出失败：" + afterResult.Summary);
            return 3;
        }

        watch.Restart();
        var plan = DesignIncrementalReload.Build(project, null, null, options, currentScreen,
            beforeResult.PageStats, beforeResult.TagFingerprints, pageRefs);
        var planMs = watch.ElapsedMilliseconds;
        if (plan.Failed)
        {
            Console.WriteLine("FAIL: 增量包导出失败：" + plan.Result.Summary);
            return 3;
        }

        var beforeBytes = beforePack.Sum(kv => (long)kv.Value.Length);
        var afterBytes = afterPack.Sum(kv => (long)kv.Value.Length);
        var planBytes = plan.Payload.Sum(kv => (long)kv.Value.Length);
        Console.WriteLine($"导出：整包 {beforePack.Count} 文件 {beforeBytes} B / {beforeMs}ms；" +
                          $"删变量后整包 {afterPack.Count} 文件 {afterBytes} B；" +
                          $"增量 {plan.Payload.Count} 文件 {planBytes} B / {planMs}ms");
        Console.WriteLine($"增量包页面：{string.Join(",", plan.IncludedPageIds)}（判据：{plan.Reason}，" +
                          $"整包模式={plan.WholeProject}）");
        if (plan.WholeProject || planBytes * 3 > beforeBytes)
        {
            Console.WriteLine("FAIL: 增量包没有变便宜（退化整包或字节数没降下来）");
            return 4;
        }
        if (!plan.IncludedPageIds.Contains(boundPage))
        {
            Console.WriteLine($"FAIL: 受影响页 {boundPage} 没进增量包");
            return 4;
        }

        // 两版该页的 JSON 差在哪（切页像素一样时，先确认"内容本身是不是真的一样"）
        {
            var key = $"pages/{boundPage}.json";
            var oldJson = System.Text.Encoding.UTF8.GetString(beforePack[key]);
            var newJson = System.Text.Encoding.UTF8.GetString(afterPack[key]);
            Console.WriteLine($"{boundPage} JSON：改动前 {oldJson.Length} B（bind×{oldJson.Split("\"bind\"").Length - 1}），" +
                              $"改动后 {newJson.Length} B（bind×{newJson.Split("\"bind\"").Length - 1}）");
        }

        var dataRoot = AppPaths.TempSubdir("chenge-kdelta");
        var truth = CapturePage(hostExe, Path.Combine(dataRoot, "truth"), afterPack, null, boundPage);
        if (truth.Pixels == null)
        {
            Console.WriteLine("FAIL: 正确答案宿主取帧失败：" + truth.Log);
            return 5;
        }

        var incremental = CapturePage(hostExe, Path.Combine(dataRoot, "incremental"), beforePack,
            plan.Payload, boundPage);
        if (incremental.Pixels == null)
        {
            Console.WriteLine("FAIL: 增量宿主取帧失败：" + incremental.Log);
            return 5;
        }

        var noPages = new Dictionary<string, byte[]>(plan.Payload.Where(kv =>
            !kv.Key.StartsWith("pages/", StringComparison.Ordinal)), StringComparer.Ordinal);
        var missing = CapturePage(hostExe, Path.Combine(dataRoot, "missing-pages"), beforePack,
            noPages, boundPage);
        if (missing.Pixels == null)
        {
            Console.WriteLine("FAIL: 负对照宿主取帧失败：" + missing.Log);
            return 5;
        }

        var sameIncremental = DiffPixels(truth.Pixels, incremental.Pixels);
        var sameMissing = DiffPixels(truth.Pixels, missing.Pixels);
        Console.WriteLine($"切页：真值 [{truth.Log}] / 增量 [{incremental.Log}] / 漏页 [{missing.Log}]");
        // 前两路必须真的切过去；漏页那一路"切不过去（建页失败）"也是合格结果 ——
        // 那正是"宿主里留着旧页"的表现，比画面略有不同更严重。
        if (!truth.Log.Contains("切页生效=True", StringComparison.Ordinal)
            || !incremental.Log.Contains("切页生效=True", StringComparison.Ordinal))
        {
            Console.WriteLine("FAIL: 真值/增量那两路切页没生效（画面还停在上一页），像素对比无意义");
            return 5;
        }
        Console.WriteLine($"切页比像素（{boundPage}）：增量包 vs 整包 差异 {sameIncremental} px；" +
                          $"只发元数据（漏页） vs 整包 差异 {sameMissing} px");
        Console.WriteLine($"帧非黑比：真值 {Lit(truth.Pixels):P1} / 增量 {Lit(incremental.Pixels):P1}" +
                          $" / 漏页 {Lit(missing.Pixels):P1}");
        if (sameIncremental != 0)
        {
            Console.WriteLine("FAIL: 增量包切页后的画面与整包不一致（受影响页没带对）");
            return 6;
        }
        var missingSwitched = missing.Log.Contains("切页生效=True", StringComparison.Ordinal);
        if (missingSwitched && sameMissing == 0)
        {
            Console.WriteLine("FAIL: 漏带受影响页竟然也一致 —— 判据失效，别信这一条");
            return 6;
        }

        Console.WriteLine("OK: 增量负载显著小于整包；受影响页切页画面与整包逐像素一致；漏页必错");
        return 0;
    }

    /// <summary>
    /// 起一个宿主、可选先发一次 <c>K</c>、再切到指定页取一帧。
    ///
    /// <para>用**仿真**模式而不是 <c>--design</c>：<c>L &lt;epoch&gt; &lt;page&gt;</c> 在宿主里只是
    /// 挂一个切页请求（<c>page_mgr_script_open</c> → pending op），要 <c>runtime_tick</c> 才执行，
    /// 而 <c>--design</c> 恰恰不推进时间 —— 那样切页永远不生效，三份工程都会拍到同一页。</para>
    ///
    /// <para>页里的变量是内部变量（无设备），仿真的 tick 不会改它们，所以画面静止可比像素。</para>
    /// </summary>
    private static (byte[]? Pixels, string Log) CapturePage(string hostExe, string dataDir,
        IReadOnlyDictionary<string, byte[]> pack, IReadOnlyDictionary<string, byte[]>? kPayload, string pageId)
    {
        Directory.CreateDirectory(dataDir);
        using var proc = new ChengeProcessSession(hostExe, dataDir, designMode: false);
        if (!proc.OpenWithBlob(pack)) return (null, "open: " + proc.LastError);

        var deadline = Environment.TickCount64 + 8000;
        while (proc.FrameVersion == 0 && Environment.TickCount64 < deadline) Thread.Sleep(20);
        if (proc.FrameVersion == 0) return (null, "no first frame: " + proc.LogTail);

        if (kPayload != null)
        {
            if (!proc.ReloadProject(1000, kPayload)) return (null, "K send failed");
            Thread.Sleep(600);
            var rejected = proc.Log.Any(l => l.Contains("rc=-", StringComparison.Ordinal));
            if (rejected) return (null, "K rejected: " + proc.LogTail);
        }

        var buf = new byte[proc.Stride * proc.Height];
        if (!proc.TryCopyFrame(buf)) return (null, "frame copy failed");
        var before = buf.ToArray();

        var mark = proc.FrameVersion;
        proc.ReloadPage(1001, pageId);
        var pageDeadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < pageDeadline) Thread.Sleep(50);
        if (!proc.TryCopyFrame(buf)) return (null, "frame copy failed");

        // 切页真的生效了吗：切页前后的帧必须不同（切页无效时 seq 也会因重绘而前进，
        // 只看 seq 会把"还停在首页"当成切成功 —— 这一点踩过）
        var switched = DiffPixels(before, buf) > 0;
        return (buf, $"seq {mark}->{proc.FrameVersion} 切页生效={switched} " +
                     $"hash={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buf))[..12]}");
    }

    /// <summary>帧里非黑像素的占比（判断"这一帧到底画了东西没有"）。</summary>
    private static double Lit(byte[] pixels)
    {
        var hit = 0;
        var total = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4 * 97, total++)
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) hit++;
        return total == 0 ? 0 : (double)hit / total;
    }

    /// <summary>两份帧里不同的像素个数（逐 4 字节像素比）。</summary>
    private static int DiffPixels(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var diff = 0;
        for (var i = 0; i + 3 < n; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) diff++;
        return diff + Math.Abs(a.Length - b.Length) / 4;
    }

    /// <summary>自检里被删的那个变量（第 7 页引用它；取末尾那位，删掉后旧页的 tag 序号越界）。</summary>
    private const string KDeltaBoundVariable = "v7";

    /// <summary>自检里绑了变量的那一页。</summary>
    private const string KDeltaPage = "p07";

    /// <summary>自检用的工程：<paramref name="pages"/> 页，只有第 7 页绑一个变量（其余页只有标签）。</summary>
    private static HmiEditor.Core.Models.HmiProject BuildKDeltaProject(int pages)
    {
        var project = new HmiEditor.Core.Models.HmiProject
        {
            Name = "kdelta",
            HardwareTargetId = "f1c100s-kinseal",
            ProjectVersion = "1.0.0",
        };
        for (var i = 0; i < 8; i++)
        {
            project.VariableManager.Variables.Add(new HmiEditor.Core.Models.Variable
            {
                Id = "v" + i, Name = "变量" + i, DataType = "Int16", Access = "rw",
                SimulatedValue = (i * 10).ToString(),
            });
        }
        for (var p = 0; p < pages; p++)
        {
            var screen = new HmiEditor.Core.Models.HmiScreen
            {
                Id = $"s{p:00}", Name = $"p{p:00}", Width = 800, Height = 480,
                BgColor = p % 2 == 0 ? "#101820" : "#203040",
            };
            screen.Controls.Add(new HmiEditor.Core.Models.HmiControl
            {
                Id = $"lbl{p:00}", Type = HmiEditor.Core.Models.HmiControlType.Label,
                Name = $"Lbl{p:00}", X = 20, Y = 20, Width = 400, Height = 40,
                Text = $"页面 {p:00}", FontSize = 24, TextColor = "#FFFFFF",
            });
            if (p == 7)
            {
                // 绑的是**最后一个**变量：它被删掉后，旧页里的 tag 序号直接越界，
                // 宿主建页失败 —— 这是"缺页 = 旧内容"唯一能稳定看出来的现象。
                // 不用实时曲线：折线随时间取样，两台宿主跑的时间不同就不是同一张图，
                // 像素对比会假红。也不用 value 控件：契约里它不能绑 value（只有
                // text/color/bg_color/visible/enabled）。
                screen.Controls.Add(new HmiEditor.Core.Models.HmiControl
                {
                    Id = "bar1", Type = HmiEditor.Core.Models.HmiControlType.Bar,
                    Name = "Bar1", X = 40, Y = 100, Width = 400, Height = 40,
                    MinValue = 0, MaxValue = 100, IndicatorColor = "#00E676",
                    VariableBindings =
                    {
                        new HmiEditor.Core.Models.VariableBindingData
                        {
                            PropertyName = "Value", VariableId = KDeltaBoundVariable,
                            Direction = HmiEditor.Core.Models.BindingDirection.ReadOnly,
                        },
                    },
                });
            }
            project.Screens.Add(screen);
        }
        return project;
    }

    /// <summary>安装文件日志器（写 %APPDATA%/chenge-hmi/logs）。</summary>

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
