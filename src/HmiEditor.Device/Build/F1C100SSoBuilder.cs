using System.Diagnostics;
using HmiEditor.Core.Services;

namespace HmiEditor.Device.Build;

/// <summary>libhmi_ui.so 交叉编译选项（kinseal / F1C100S）。</summary>
public sealed class F1C100SSoBuildOptions
{
    /// <summary>待编译的生成源码（generated_hmi.c）。</summary>
    public required string SourcePath { get; init; }

    /// <summary>产物输出目录（libhmi_ui.so 写入此处）。</summary>
    public required string OutputDir { get; init; }

    /// <summary>工程标签契约头 tags.h（由编辑器导出）。为空时回退运行时目录旁的同名文件。</summary>
    public string? TagsHeaderPath { get; init; }

    /// <summary>交叉编译器完整路径（arm-buildroot-linux-gnueabi-gcc）。空 = 自动探测。</summary>
    public string? ToolchainGcc { get; init; }

    /// <summary>
    /// 设备侧运行时目录（chenge-rt，含 config/lv_conf.h、src/runtime、third_party/lvgl-9.5.0）。
    /// 空 = 按 <see cref="F1C100SSoBuilder.FindRuntimeDir"/> 的顺序探测
    /// （环境变量 CHENGE_RT_DIR → 安装目录内置 SDK），不猜目标机上的其它目录。
    /// </summary>
    public string? RuntimeDir { get; init; }

    /// <summary>
    /// 板端资产目录（生成代码 IMG_PATH = "A:" SIM_ASSET_DIR "/&lt;文件&gt;"）。
    /// 默认 /usr/lib/hmi/assets —— 板端 LVGL FS 的 'A:' 经 stdio 驱动落到绝对路径，
    /// 资产目录由板端构建部署（见 docs/hardware/f1c100s-kinseal/01-board-facts.md §4）。
    /// </summary>
    public string AssetDir { get; init; } = "/usr/lib/hmi/assets";
}

/// <summary>交叉编译结果。</summary>
public sealed class F1C100SSoBuildResult
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Logs { get; init; } = Array.Empty<string>();
}

/// <summary>解析后的构建上下文（供测试与诊断）。</summary>
public sealed class F1C100SBuildContext
{
    public string SourcePath { get; init; } = "";
    public string OutputDir { get; init; } = "";
    public string CompilerPath { get; init; } = "";
    /// <summary>工具链根目录（<c>&lt;sdk&gt;/toolchain</c>）。没有清单时为空。</summary>
    public string ToolchainDir { get; init; } = "";
    /// <summary>工具链声明（clang/gcc 与所需 flag）。没有清单时为 null —— 按 gcc 行为走。</summary>
    public KinSealSdkToolchain? Toolchain { get; init; }
    public string RuntimeDir { get; init; } = "";
    public string LvglRoot { get; init; } = "";
    public string ConfigDir { get; init; } = "";
    public string TagsDir { get; init; } = "";
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();
}

/// <summary>
/// kinseal（F1C100S）libhmi_ui.so 交叉编译器 —— 用 buildroot 工具链把编辑器生成的
/// SharedLibrary 源码编译为 .so，供主仓库板端构建/OTA 使用。
///
/// 编译模型与 chenge-rt Makefile 的 UI_SO 目标一致：
///   -std=c99 -fPIC 编译（符号不解析），-shared -nostartfiles 链接；
///   全部 LVGL/宿主符号由板端 hmi-rt（-rdynamic + RTLD_GLOBAL）在 dlopen 时解析，
///   因此只需要头文件（LVGL 源码头 + chenge-rt 契约头），无需链接库。
///
/// 与 T113SoBuilder 的分工：T113 走 Tina SDK/独立工具包（arm-openwrt-linux-*），
/// kinseal 走主仓库的 buildroot 2020.02 工具链 + chenge-rt 源码树 —— 两套布局无交集，
/// 故分开两个构建器，不合成一个"多目标"开关（目标由工程 HardwareTargetId 选择）。
/// </summary>
public sealed class F1C100SSoBuilder
{
    /// <summary>工具链环境变量覆盖（完整路径优先）。</summary>
    public const string GccEnvVar = "F1C100S_GCC";

    /// <summary>运行时目录环境变量覆盖（chenge-rt 根）。</summary>
    public const string RuntimeDirEnvVar = "CHENGE_RT_DIR";

    /// <summary>运行时目录的判别文件（缺它就不是 chenge-rt）。</summary>
    private const string RuntimeMarker = "src/runtime/hmi_ui_api.h";

    /// <summary>工具链可执行名（buildroot gnueabi 软浮点，见其 .config BR2_GCC_TARGET_CPU）。</summary>
    private const string GccName = "arm-buildroot-linux-gnueabi-gcc";

    /// <summary>操作日志（每条一行，供 UI 展示）。</summary>
    public event Action<string>? Log;

    /// <summary>
    /// 安装目录内置 SDK 的相对路径 —— 随安装包分发，不依赖目标机上的任何工程目录。
    /// 布局：<c>&lt;install&gt;/sdk/kinseal/{runtime, toolchain/bin}</c>。
    /// </summary>
    public const string BundledSdkRelativePath = "sdk/kinseal";

    /// <summary>
    /// 查找设备侧运行时目录，顺序：设置值 → 环境变量 → 安装目录内置 SDK。
    /// 找不到返回 null（由调用方给出"去哪配"的明确提示）；<b>不猜测</b>目标机上的目录 ——
    /// 编辑器是独立安装的软件，别人的机器上没有本项目的仓库布局。
    /// </summary>
    public static string? FindRuntimeDir(string? configured) =>
        FindRuntimeDir(configured, AppContext.BaseDirectory);

    /// <summary>
    /// 同上，但显式指定安装目录（模拟器发现 LVGL 树、以及测试传假安装包时用）。
    /// </summary>
    public static string? FindRuntimeDir(string? configured, string? installDir)
    {
        if (!string.IsNullOrWhiteSpace(configured) && IsRuntimeDir(configured))
            return Path.GetFullPath(configured);

        var fromEnv = Environment.GetEnvironmentVariable(RuntimeDirEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && IsRuntimeDir(fromEnv))
            return Path.GetFullPath(fromEnv);

        // 装好的 SDK 是一个包（runtime/ + toolchain/），运行时根在它下面一层
        var installedRuntime = Path.Combine(KinSealSdkInstaller.SdkRoot, "runtime");
        if (IsRuntimeDir(installedRuntime))
            return Path.GetFullPath(installedRuntime);

        if (string.IsNullOrWhiteSpace(installDir)) return null;
        // 随安装包分发的 SDK（标准安装包 + 扁平安装包）
        foreach (var candidate in new[]
                 {
                     Path.Combine(installDir, "sdk", "kinseal", "runtime"),
                     Path.Combine(installDir, "runtime"),
                 })
        {
            if (IsRuntimeDir(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>目录是否为 chenge-rt 运行时根（判别文件存在）。</summary>
    public static bool IsRuntimeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(Path.Combine(path, RuntimeMarker.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return false; }
    }

    /// <summary>
    /// 查找交叉编译器，顺序：设置值 → 环境变量 → 下载安装的 SDK → 随安装包内置的 SDK → PATH。
    /// </summary>
    public static string? FindToolchainGcc(string? configured) =>
        FindToolchainGcc(configured, AppContext.BaseDirectory);

    /// <summary>内部重载：指定安装目录（测试用）。</summary>
    internal static string? FindToolchainGcc(string? configured, string? installDir)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var fromEnv = Environment.GetEnvironmentVariable(GccEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        var sdkRoots = new List<string>
        {
            // 用户显式下载安装的 SDK 优先（可能是新版本）；随包内置的作兜底
            KinSealSdkInstaller.SdkRoot,
        };
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            sdkRoots.Add(Path.Combine(installDir, "sdk", "kinseal"));
            sdkRoots.Add(installDir);           // 扁平安装包：<install>/toolchain/bin
        }

        foreach (var sdkRoot in sdkRoots)
        {
            var toolchainDir = Path.Combine(sdkRoot, "toolchain");

            // 有清单就按清单找编译器（clang 版 SDK 的 bin/ 下不是 gcc 那个名字）
            var declared = KinSealSdkToolchain.Load(toolchainDir);
            if (declared is not null)
            {
                var declaredExe = declared.ExecutablePath(toolchainDir);
                if (File.Exists(declaredExe)) return declaredExe;
            }

            var gcc = Path.Combine(toolchainDir, "bin", GccName);
            if (File.Exists(gcc)) return gcc;
        }

        return Which(GccName);
    }

    /// <summary>LVGL 源码根（含 lvgl.h 与 CMakeLists.txt；运行时自持 third_party/lvgl-9.5.0）。</summary>
    public static string? FindLvglRoot(string? runtimeDir)
    {
        if (string.IsNullOrWhiteSpace(runtimeDir)) return null;
        var thirdParty = Path.Combine(runtimeDir, "third_party");
        if (!Directory.Exists(thirdParty)) return null;

        // 固定名优先，其次按版本目录名兜底（升级 LVGL 时不必改这里）
        var candidates = new List<string> { Path.Combine(thirdParty, "lvgl-9.5.0") };
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(thirdParty, "lvgl-*")
                .OrderBy(d => d, StringComparer.Ordinal));
        }
        catch { /* 枚举失败只影响兜底分支 */ }

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "lvgl.h")) &&
                File.Exists(Path.Combine(candidate, "CMakeLists.txt")))
                return candidate;
        }
        return null;
    }

    /// <summary>解析完整构建上下文（不执行编译），缺失项列入 <see cref="F1C100SBuildContext.Missing"/>。</summary>
    public static F1C100SBuildContext ResolveContext(F1C100SSoBuildOptions options) =>
        ResolveContext(options, AppContext.BaseDirectory);

    /// <summary>内部重载：指定安装目录（测试用）。</summary>
    internal static F1C100SBuildContext ResolveContext(F1C100SSoBuildOptions options, string? installDir)
    {
        var missing = new List<string>();
        var sourcePath = Path.GetFullPath(options.SourcePath);
        var outputDir = Path.GetFullPath(options.OutputDir);

        if (!File.Exists(sourcePath)) missing.Add($"生成源码不存在: {sourcePath}");
        if (!Directory.Exists(outputDir)) missing.Add($"输出目录不存在: {outputDir}");

        var runtimeDir = FindRuntimeDir(options.RuntimeDir, installDir);
        if (runtimeDir is null)
            missing.Add($"未找到设备侧运行时目录（.so 编译需要 hmi_ui_api.h 等契约头）——"
                + $"请在 设置→真机部署 指定 chenge-rt 目录，或设环境变量 {RuntimeDirEnvVar}，"
                + $"或随安装包放在 <安装目录>/{BundledSdkRelativePath}/runtime");

        var compiler = FindToolchainGcc(options.ToolchainGcc, installDir);
        if (compiler is null)
            missing.Add($"未找到交叉编译器 ——请在 设置→真机部署 指定路径，"
                + $"或设环境变量 {GccEnvVar}，或随安装包放在 <安装目录>/{BundledSdkRelativePath}/toolchain/bin，"
                + $"或加入 PATH（SDK 的 toolchain/toolchain.json 决定用 gcc 还是 clang）");

        // 工具链清单：放在 bin 的上一级（<sdk>/toolchain/toolchain.json）。没有就按 gcc 行为走
        // —— 老 SDK（只有 buildroot gcc）与手工配的编译器路径都不需要它。
        var toolchainDir = compiler is null ? "" : ToolchainDirOf(compiler);
        var toolchain = toolchainDir == "" ? null : KinSealSdkToolchain.Load(toolchainDir);

        var lvgl = FindLvglRoot(runtimeDir);
        if (lvgl is null)
            missing.Add($"未找到 LVGL 源码根（运行时目录下的 third_party/lvgl-9.5.0；"
                + $"随安装包分发时应为 <安装目录>/{BundledSdkRelativePath}/runtime/third_party/lvgl-9.5.0）");

        var configDir = runtimeDir is null ? "" : Path.Combine(runtimeDir, "config");
        if (configDir == "" || !File.Exists(Path.Combine(configDir, "lv_conf.h")))
            missing.Add("未找到运行时 LVGL 配置 lv_conf.h（运行时目录下的 config/）");

        var tagsDir = "";
        if (!string.IsNullOrWhiteSpace(options.TagsHeaderPath) && File.Exists(options.TagsHeaderPath))
        {
            tagsDir = Path.GetDirectoryName(Path.GetFullPath(options.TagsHeaderPath)) ?? "";
        }
        else if (runtimeDir is not null &&
                 File.Exists(Path.Combine(runtimeDir, "generated", "tags.h")))
        {
            tagsDir = Path.Combine(runtimeDir, "generated");
        }
        else
        {
            missing.Add("未找到标签契约头 tags.h（请先编译转换生成工程 tags.h）");
        }

        return new F1C100SBuildContext
        {
            SourcePath = sourcePath,
            OutputDir = outputDir,
            CompilerPath = compiler ?? "",
            RuntimeDir = runtimeDir ?? "",
            LvglRoot = lvgl ?? "",
            ConfigDir = configDir,
            TagsDir = tagsDir,
            ToolchainDir = toolchain is null ? "" : toolchainDir,
            Toolchain = toolchain,
            Missing = missing
        };
    }

    /// <summary>
    /// 把路径规范成 C 字符串字面量里安全的形式：一律用正斜杠。
    /// Windows 路径里的 <c>\</c> 在 C 字符串里是转义符（<c>\U</c> 是通用字符名、<c>\a</c> 是响铃），
    /// 直接拼进 <c>-DSIM_ASSET_DIR="…"</c> 会让生成代码里的路径变成乱码 —— 而 Windows 的
    /// 文件 API 接受正斜杠，所以统一换掉即可。
    /// </summary>
    private static string AsCStringPath(string path) =>
        path.Replace('\\', '/');

    /// <summary>
    /// 由编译器可执行路径反推工具链根：<c>&lt;sdk&gt;/toolchain/bin/&lt;cc&gt;</c> → <c>&lt;sdk&gt;/toolchain</c>。
    /// 手工指定/在 PATH 里的编译器不在这个布局下，返回空串（等于"没有清单"）。
    /// </summary>
    private static string ToolchainDirOf(string compilerPath)
    {
        var bin = Path.GetDirectoryName(Path.GetFullPath(compilerPath));
        if (bin is null) return "";
        var root = Path.GetFullPath(Path.Combine(bin, ".."));
        return File.Exists(Path.Combine(root, "toolchain.json")) ? root : "";
    }

    /// <summary>
    /// 构建编译参数（不含编译器本身），供诊断与测试。
    /// 头文件集合与 chenge-rt Makefile 编 generated/screens.c 时一致（板端能编，这里就能编）。
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(F1C100SBuildContext ctx, string assetDir, string workDir)
    {
        var rt = ctx.RuntimeDir;
        var args = new List<string>();
        // clang 需要显式 target/float-abi/sysroot（gcc 由 spec 隐式，清单里为空）
        if (ctx.Toolchain is not null && !string.IsNullOrEmpty(ctx.ToolchainDir))
            args.AddRange(ctx.Toolchain.CompileFlags(ctx.ToolchainDir));

        args.AddRange(new[]
        {
            "-std=c99", "-D_GNU_SOURCE", "-Wno-unused-parameter",
            "-fPIC",
            "-DLV_CONF_INCLUDE_SIMPLE",
            $"-DSIM_ASSET_DIR=\"{AsCStringPath(assetDir)}\"",
            "-I" + ctx.ConfigDir,
            "-I" + ctx.LvglRoot,
            "-I" + Path.Combine(rt, "third_party"),
            "-I" + rt,
            "-I" + Path.Combine(rt, "src", "runtime"),
            "-I" + Path.Combine(rt, "src", "comm"),
            "-I" + Path.Combine(rt, "src", "settings"),
            "-I" + Path.Combine(rt, "src", "drivers"),
            "-I" + ctx.TagsDir,
            "-c", ctx.SourcePath,
            "-o", Path.Combine(workDir, "generated_hmi.o")
        });
        return args;
    }

    /// <summary>
    /// 交叉编译 generated_hmi.c → libhmi_ui.so。
    /// 任一前置缺失时返回失败并给出明确原因（不抛异常）。
    /// </summary>
    public Task<F1C100SSoBuildResult> BuildAsync(F1C100SSoBuildOptions options, IProgress<double>? progress = null, CancellationToken ct = default) =>
        BuildAsync(options, AppContext.BaseDirectory, progress, ct);

    /// <summary>内部重载：指定探测起始目录（测试用）。</summary>
    internal async Task<F1C100SSoBuildResult> BuildAsync(F1C100SSoBuildOptions options, string? installDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var logs = new List<string>();
        void Info(string msg) { logs.Add(msg); Log?.Invoke(msg); }

        try
        {
            var ctx = ResolveContext(options, installDir);
            if (ctx.Missing.Count > 0)
            {
                foreach (var m in ctx.Missing) Info($"[.so] ✗ {m}");
                return new F1C100SSoBuildResult { Success = false, Error = string.Join("；", ctx.Missing), Logs = logs };
            }

            Info($"[.so] 工具链: {ctx.CompilerPath}");
            Info($"[.so] 运行时: {ctx.RuntimeDir}");
            Info($"[.so] LVGL: {ctx.LvglRoot}");

            var workDir = AppPaths.TempSubdir("so-build-f1c100s");
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(ctx.OutputDir);

            // 1. 编译
            progress?.Report(0.1);
            var compileArgs = BuildArguments(ctx, options.AssetDir, workDir);
            Info($"[.so] 编译 {Path.GetFileName(ctx.SourcePath)} ...");
            var (compileExit, compileOut) = await RunAsync(ctx.CompilerPath, compileArgs, ct);
            foreach (var line in SplitLines(compileOut))
                Info($"[.so] {line}");
            if (compileExit != 0)
            {
                var err = $"交叉编译失败 (exit {compileExit})";
                Info($"[.so] ✗ {err}");
                return new F1C100SSoBuildResult { Success = false, Error = err, Logs = logs };
            }
            progress?.Report(0.6);

            // 2. 链接（符号由宿主 dlopen 时解析，-nostartfiles 不链接 CRT）
            var outputPath = Path.Combine(ctx.OutputDir, "libhmi_ui.so");
            Info("[.so] 链接 libhmi_ui.so ...");
            var linkArgs = ctx.Toolchain is not null && !string.IsNullOrEmpty(ctx.ToolchainDir)
                ? ctx.Toolchain.LinkFlags(ctx.ToolchainDir, outputPath, Path.Combine(workDir, "generated_hmi.o"))
                : new[]
                {
                    "-shared", "-nostartfiles", "-o", outputPath,
                    Path.Combine(workDir, "generated_hmi.o")
                };
            var (linkExit, linkOut) = await RunAsync(ctx.CompilerPath, linkArgs, ct);
            foreach (var line in SplitLines(linkOut))
                Info($"[.so] {line}");
            if (linkExit != 0 || !File.Exists(outputPath))
            {
                var err = $"链接失败 (exit {linkExit})";
                Info($"[.so] ✗ {err}");
                return new F1C100SSoBuildResult { Success = false, Error = err, Logs = logs };
            }
            progress?.Report(1.0);
            Info($"[.so] ✓ 已生成 {outputPath}");
            return new F1C100SSoBuildResult { Success = true, OutputPath = outputPath, Logs = logs };
        }
        catch (OperationCanceledException)
        {
            Info("[.so] 已取消");
            return new F1C100SSoBuildResult { Success = false, Error = "已取消", Logs = logs };
        }
        catch (Exception ex)
        {
            Info($"[.so] ✗ {ex.Message}");
            return new F1C100SSoBuildResult { Success = false, Error = ex.Message, Logs = logs };
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return (-1, "进程启动失败");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await ChildProcessRunner.WaitForExitOrKillAsync(process, ct);
        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    /// <summary>在 PATH 中查找可执行文件（用户已自行把工具链加进 PATH 时的兜底）。</summary>
    private static string? Which(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
            catch { /* 忽略非法路径 */ }
        }
        return null;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
