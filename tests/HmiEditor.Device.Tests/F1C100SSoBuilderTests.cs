using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HmiEditor.Device.Build;
using Xunit;

namespace HmiEditor.Device.Tests;

/// <summary>
/// F1C100SSoBuilder 测试：SDK 定位、构建上下文解析、交叉编译命令流。
///
/// <para><b>发现模型</b>：编辑器是独立安装的软件 —— 查找顺序只有
/// 设置值 → 环境变量 → 安装目录内置 SDK（<c>&lt;install&gt;/sdk/kinseal/{runtime,toolchain}</c>）。
/// 不允许向上遍历、不允许假设 <c>f1c100s-project</c> 之类的仓库布局。这些用例把该边界钉住。</para>
///
/// <para>环境变量 <c>CHENGE_RT_DIR</c> / <c>F1C100S_GCC</c> 一律设成无效值，
/// 否则开发机上真有的 SDK 会把假树测试翻成真树测试。</para>
/// </summary>
public class F1C100SSoBuilderTests : IDisposable
{
    // 每条用例一个空 SDK 根：否则机器上（或另一程序集）装过的 SDK 会把"应为 null"翻成"找到了"
    private readonly SdkRootSandbox _sdkRoot = new();

    public void Dispose() => _sdkRoot.Dispose();

    /// <summary>
    /// 构造最小安装包：&lt;root&gt;/sdk/kinseal/{runtime, toolchain/bin/gcc}。
    /// 返回 runtime 目录。
    /// </summary>
    private static string CreateRuntimeTree(string root)
    {
        var sdk = Path.Combine(root, "sdk", "kinseal");
        var rt = Path.Combine(sdk, "runtime");

        Directory.CreateDirectory(Path.Combine(rt, "config"));
        File.WriteAllText(Path.Combine(rt, "config", "lv_conf.h"), "#define LV_COLOR_DEPTH 32\n");

        Directory.CreateDirectory(Path.Combine(rt, "src", "runtime"));
        File.WriteAllText(Path.Combine(rt, "src", "runtime", "hmi_ui_api.h"), "/* api */\n");
        File.WriteAllText(Path.Combine(rt, "src", "runtime", "screen_manager.h"), "/* sm */\n");

        Directory.CreateDirectory(Path.Combine(rt, "src", "comm"));
        Directory.CreateDirectory(Path.Combine(rt, "src", "settings"));
        Directory.CreateDirectory(Path.Combine(rt, "src", "drivers"));

        var lvgl = Path.Combine(rt, "third_party", "lvgl-9.5.0");
        Directory.CreateDirectory(lvgl);
        File.WriteAllText(Path.Combine(lvgl, "lvgl.h"), "/* lvgl */\n");
        File.WriteAllText(Path.Combine(lvgl, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.10)\n");

        Directory.CreateDirectory(Path.Combine(rt, "generated"));
        File.WriteAllText(Path.Combine(rt, "generated", "tags.h"), "enum { TAG_COUNT = 0 };\n");

        var bin = Path.Combine(sdk, "toolchain", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "arm-buildroot-linux-gnueabi-gcc"), "#!/bin/sh\nexit 0\n");

        return rt;
    }

    /// <summary>造一份生成源码 + tags.h，返回 (src, tags, outDir)。</summary>
    private static (string Src, string Tags, string OutDir) CreateSources(string root)
    {
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(outDir);
        var src = Path.Combine(outDir, "generated_hmi.c");
        File.WriteAllText(src, "int x;\n");
        var tags = Path.Combine(outDir, "tags.h");
        File.WriteAllText(tags, "enum { TAG_COUNT = 0 };\n");
        return (src, tags, outDir);
    }

    private static void NeutralizeEnv()
    {
        Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, "/no/such/rt");
        Environment.SetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar, "/no/such/gcc");
    }

    private static void RestoreEnv(string? rt, string? gcc)
    {
        Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, rt);
        Environment.SetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar, gcc);
    }

    [Fact]
    public void IsRuntimeDir_MarkerFile_Decides()
    {
        var root = TestTempDir.NewDir("rt");
        var rt = CreateRuntimeTree(root);
        try
        {
            Assert.True(F1C100SSoBuilder.IsRuntimeDir(rt));
            Assert.False(F1C100SSoBuilder.IsRuntimeDir(Path.Combine(rt, "src")));
            Assert.False(F1C100SSoBuilder.IsRuntimeDir("/no/such/dir"));
            Assert.False(F1C100SSoBuilder.IsRuntimeDir(""));
        }
        finally { TestTempDir.DeleteDir(root); }
    }

    [Fact]
    public void FindRuntimeDir_Configured_Wins()
    {
        var root = TestTempDir.NewDir("rt");
        var rt = CreateRuntimeTree(root);
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        try
        {
            NeutralizeEnv();
            Assert.Equal(rt, F1C100SSoBuilder.FindRuntimeDir(rt));
        }
        finally
        {
            RestoreEnv(prevRt, null);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void FindRuntimeDir_BundledSdkLayout()
    {
        // 安装包内置 SDK：<install>/sdk/kinseal/runtime
        var root = TestTempDir.NewDir("rt");
        var rt = CreateRuntimeTree(root);
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        try
        {
            NeutralizeEnv();
            Assert.Equal(rt, F1C100SSoBuilder.FindRuntimeDir(null, root));
        }
        finally
        {
            RestoreEnv(prevRt, null);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void FindRuntimeDir_NothingConfigured_ReturnsNull()
    {
        // 空安装目录：不向上遍历、不猜任何工程布局，直接判无
        var root = TestTempDir.NewDir("rt");
        var installDir = Path.Combine(root, "app");
        Directory.CreateDirectory(installDir);
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        try
        {
            NeutralizeEnv();
            Assert.Null(F1C100SSoBuilder.FindRuntimeDir(null, installDir));

            // 把「开发机平级仓库」摆在安装目录旁边：不能被认出来，否则又绑回本机布局
            var sibling = Path.Combine(root, "f1c100s-project", "chenge-rt");
            Directory.CreateDirectory(Path.Combine(sibling, "src", "runtime"));
            File.WriteAllText(Path.Combine(sibling, "src", "runtime", "hmi_ui_api.h"), "/* api */\n");
            Assert.Null(F1C100SSoBuilder.FindRuntimeDir(null, installDir));
        }
        finally
        {
            RestoreEnv(prevRt, null);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void FindToolchainGcc_BundledSdkLayout()
    {
        var root = TestTempDir.NewDir("rt");
        CreateRuntimeTree(root);
        var prevGcc = Environment.GetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar);
        try
        {
            NeutralizeEnv();
            var gcc = F1C100SSoBuilder.FindToolchainGcc(null, root);
            Assert.NotNull(gcc);
            Assert.EndsWith("arm-buildroot-linux-gnueabi-gcc", gcc);
            Assert.Contains(Path.Combine("sdk", "kinseal", "toolchain", "bin"), gcc);
        }
        finally
        {
            RestoreEnv(null, prevGcc);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void FindLvglRoot_FixedNameFirst_ThenGlobFallback()
    {
        var root = TestTempDir.NewDir("rt");
        var rt = CreateRuntimeTree(root);
        try
        {
            Assert.Equal(Path.Combine(rt, "third_party", "lvgl-9.5.0"),
                F1C100SSoBuilder.FindLvglRoot(rt));

            // 固定名不在时按 lvgl-* 兜底（升级版本不必改代码）
            File.Delete(Path.Combine(rt, "third_party", "lvgl-9.5.0", "lvgl.h"));
            Assert.Null(F1C100SSoBuilder.FindLvglRoot(rt));

            var other = Path.Combine(rt, "third_party", "lvgl-9.6.0");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "lvgl.h"), "/* lvgl */\n");
            File.WriteAllText(Path.Combine(other, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.10)\n");
            Assert.Equal(other, F1C100SSoBuilder.FindLvglRoot(rt));
        }
        finally { TestTempDir.DeleteDir(root); }
    }

    [Fact]
    public void ResolveContext_CompleteTree_NoMissing()
    {
        var root = TestTempDir.NewDir("rt");
        var rt = CreateRuntimeTree(root);
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        var prevGcc = Environment.GetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar);
        try
        {
            NeutralizeEnv();
            var (src, tags, outDir) = CreateSources(root);

            var ctx = F1C100SSoBuilder.ResolveContext(new F1C100SSoBuildOptions
            {
                SourcePath = src,
                OutputDir = outDir,
                TagsHeaderPath = tags,
            }, root);

            Assert.Empty(ctx.Missing);
            Assert.Equal(rt, ctx.RuntimeDir);
            Assert.EndsWith("arm-buildroot-linux-gnueabi-gcc", ctx.CompilerPath);
            Assert.Equal(Path.Combine(rt, "third_party", "lvgl-9.5.0"), ctx.LvglRoot);
            Assert.Equal(Path.Combine(rt, "config"), ctx.ConfigDir);
            Assert.Equal(outDir, ctx.TagsDir);
        }
        finally
        {
            RestoreEnv(prevRt, prevGcc);
            TestTempDir.DeleteDir(root);
        }
    }

    // ── 工具链清单（clang 版 SDK / Windows 变体）────────────────────────────

    /// <summary>在假 SDK 里放一份 clang 工具链（bin/clang + toolchain.json）。</summary>
    private static void AddClangToolchain(string root)
    {
        var toolchain = Path.Combine(root, "sdk", "kinseal", "toolchain");
        Directory.CreateDirectory(Path.Combine(toolchain, "bin"));
        Directory.CreateDirectory(Path.Combine(toolchain, "sysroot"));
        Directory.CreateDirectory(Path.Combine(toolchain, "lib"));
        File.WriteAllText(Path.Combine(toolchain, "bin", "clang"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(toolchain, "toolchain.json"), """
            {
              "kind": "clang", "cc": "clang", "target": "armv5te-linux-gnueabi",
              "floatAbi": "soft", "sysroot": "sysroot", "ld": "ld.lld",
              "libDir": "lib", "libName": "gcc", "noStdlib": true
            }
            """);
    }

    [Fact]
    public void FindToolchain_PrefersManifestDeclaredCompiler()
    {
        // clang 版 SDK 的 bin/ 下没有 gcc 那个名字 —— 只认 gcc 会漏掉整个 Windows 变体
        var root = TestTempDir.NewDir("rt");
        CreateRuntimeTree(root);
        AddClangToolchain(root);
        var prevGcc = Environment.GetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar);
        try
        {
            NeutralizeEnv();
            var found = F1C100SSoBuilder.FindToolchainGcc(null, root);
            Assert.NotNull(found);
            Assert.EndsWith(Path.Combine("toolchain", "bin", "clang"), found);
        }
        finally
        {
            RestoreEnv(null, prevGcc);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void ResolveContext_ClangManifest_PopulatesToolchainAndFlags()
    {
        var root = TestTempDir.NewDir("rt");
        CreateRuntimeTree(root);
        AddClangToolchain(root);
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        var prevGcc = Environment.GetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar);
        try
        {
            NeutralizeEnv();
            var (src, tags, outDir) = CreateSources(root);

            var ctx = F1C100SSoBuilder.ResolveContext(new F1C100SSoBuildOptions
            {
                SourcePath = src,
                OutputDir = outDir,
                TagsHeaderPath = tags,
            }, root);

            Assert.Empty(ctx.Missing);
            Assert.NotNull(ctx.Toolchain);
            Assert.Equal("clang", ctx.Toolchain!.Kind);
            Assert.EndsWith("clang", ctx.CompilerPath);

            // 编译与链接参数都要带上 target/sysroot（漏一个就按宿主架构链接）
            var compile = F1C100SSoBuilder.BuildArguments(ctx, "/assets", outDir);
            Assert.Contains(compile, a => a.StartsWith("--target="));
            Assert.Contains(compile, a => a.StartsWith("--sysroot="));

            var link = ctx.Toolchain.LinkFlags(ctx.ToolchainDir,
                Path.Combine(outDir, "libhmi_ui.so"), Path.Combine(outDir, "generated_hmi.o"));
            Assert.Contains(link, a => a.StartsWith("--ld-path="));
            Assert.Contains("-lgcc", link);
            Assert.Contains("-lc", link);
        }
        finally
        {
            RestoreEnv(prevRt, prevGcc);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void ResolveContext_MissingRuntime_ReportsMissingItems()
    {
        var root = TestTempDir.NewDir("rt");
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        var prevGcc = Environment.GetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar);
        try
        {
            NeutralizeEnv();
            var (src, _, outDir) = CreateSources(root);

            var ctx = F1C100SSoBuilder.ResolveContext(new F1C100SSoBuildOptions
            {
                SourcePath = src,
                OutputDir = outDir,
            }, root);

            Assert.Contains(ctx.Missing, m => m.Contains("chenge-rt"));
            Assert.Contains(ctx.Missing, m => m.Contains("tags.h"));
        }
        finally
        {
            RestoreEnv(prevRt, prevGcc);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void BuildArguments_ContainsExpectedFlagsAndIncludes()
    {
        var root = TestTempDir.NewDir("rt");
        var rt = CreateRuntimeTree(root);
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        var prevGcc = Environment.GetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar);
        try
        {
            NeutralizeEnv();
            var (src, tags, outDir) = CreateSources(root);

            var ctx = F1C100SSoBuilder.ResolveContext(new F1C100SSoBuildOptions
            {
                SourcePath = src,
                OutputDir = outDir,
                TagsHeaderPath = tags,
            }, root);
            var args = F1C100SSoBuilder.BuildArguments(ctx, "/usr/lib/hmi/assets", root).ToList();

            Assert.Contains("-std=c99", args);
            Assert.Contains("-fPIC", args);
            Assert.Contains("-DLV_CONF_INCLUDE_SIMPLE", args);
            Assert.Contains(args, a => a == "-DSIM_ASSET_DIR=\"/usr/lib/hmi/assets\"");
            Assert.Contains(args, a => a.StartsWith("-I") && a.Contains("config"));
            Assert.Contains(args, a => a.StartsWith("-I") && a.Contains("lvgl-9.5.0"));
            Assert.Contains(args, a => a.StartsWith("-I") && a.Contains("runtime"));
            Assert.Contains("-c", args);
            Assert.Equal(src, args[args.IndexOf("-c") + 1]);
            Assert.EndsWith("generated_hmi.o", args[args.IndexOf("-o") + 1]);
        }
        finally
        {
            RestoreEnv(prevRt, prevGcc);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public async Task BuildAsync_WithFakeGcc_ProducesSharedLibrary()
    {
        // 假 gcc 是 shell 脚本 ⇒ 仅 Unix 可执行（与 T113SoBuilderTests 同处置：
        // 显式失败而不是静默跳过；形状保持平台守卫以匹配 CA1416）
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "本用例的假 gcc 是 shell 脚本，仅 Unix 可执行；换平台请补等价的工具链桩");
        }
        var root = TestTempDir.NewDir("rt");
        var rt = CreateRuntimeTree(root);
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        var prevGcc = Environment.GetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar);
        try
        {
            NeutralizeEnv();
            // 可执行假 gcc：把 -o 后的文件写出来（模拟编译/链接两步都成功）
            var gccPath = Path.Combine(root, "sdk", "kinseal", "toolchain", "bin",
                "arm-buildroot-linux-gnueabi-gcc");
            File.WriteAllText(gccPath,
                "#!/bin/sh\n" +
                "out=\"\"\nwhile [ $# -gt 0 ]; do\n  if [ \"$1\" = \"-o\" ]; then shift; out=\"$1\"; fi\n  shift\ndone\n" +
                "if [ -n \"$out\" ]; then printf 'FAKE-ELF' > \"$out\"; fi\nexit 0\n");
            File.SetUnixFileMode(gccPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var (src, tags, outDir) = CreateSources(root);

            var builder = new F1C100SSoBuilder();
            var logs = new System.Collections.Generic.List<string>();
            builder.Log += logs.Add;

            var result = await builder.BuildAsync(new F1C100SSoBuildOptions
            {
                SourcePath = src,
                OutputDir = outDir,
                TagsHeaderPath = tags,
            }, root);

            Assert.True(result.Success, string.Join(" | ", result.Logs));
            Assert.NotNull(result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.Equal("libhmi_ui.so", Path.GetFileName(result.OutputPath));
            Assert.Contains(logs, l => l.Contains("已生成"));
        }
        finally
        {
            RestoreEnv(prevRt, prevGcc);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public async Task BuildAsync_MissingRuntime_FailsWithClearError()
    {
        var root = TestTempDir.NewDir("rt");
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        var prevGcc = Environment.GetEnvironmentVariable(F1C100SSoBuilder.GccEnvVar);
        try
        {
            NeutralizeEnv();
            var (src, _, outDir) = CreateSources(root);

            var result = await new F1C100SSoBuilder().BuildAsync(new F1C100SSoBuildOptions
            {
                SourcePath = src,
                OutputDir = outDir,
            }, root);

            Assert.False(result.Success);
            Assert.Contains("chenge-rt", result.Error);
        }
        finally
        {
            RestoreEnv(prevRt, prevGcc);
            TestTempDir.DeleteDir(root);
        }
    }
}
