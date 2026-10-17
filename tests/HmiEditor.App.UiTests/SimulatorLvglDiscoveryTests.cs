using System;
using System.IO;
using HmiEditor.App.Services;
using HmiEditor.Device.Build;
using HmiEditor.TestSupport;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 「编译并运行」的 LVGL 源码发现边界。
///
/// <para><b>为什么需要这些用例</b>：编辑器是用户单独安装的软件，目标机上不会有本项目的
/// 仓库布局。发现顺序只允许：用户配置 → 环境变量 <c>LVGL_DIR</c> → 安装目录内置 SDK →
/// 工程工作目录同级（生成模板默认 <c>../lvgl</c>）。向上遍历、猜 <c>f1c100s-project</c>
/// 之类的路径都属于把开发机布局写进产品，必须钉死不许回归。</para>
///
/// <para><b>内置 SDK 布局</b>由 <see cref="F1C100SSoBuilder.BundledSdkRelativePath"/> 定义：
/// <c>&lt;install&gt;/sdk/kinseal/{runtime, toolchain, lvgl}</c>。</para>
/// </summary>
public class SimulatorLvglDiscoveryTests : IDisposable
{
    // 每条用例一个空 SDK 根：机器上装过 SDK 时，"找不到"的断言不该被翻成"找到了"
    private readonly SdkRootSandbox _sdkRoot = new();

    public void Dispose() => _sdkRoot.Dispose();

    /// <summary>造一个可用的 LVGL 源码目录（只需 lvgl.h + CMakeLists.txt 作为判别文件）。</summary>
    private static string CreateLvglDir(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "lvgl.h"), "/* lvgl */\n");
        File.WriteAllText(Path.Combine(path, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.10)\n");
        return path;
    }

    [Fact]
    public void BundledLvglDir_FoundUnderInstallDir()
    {
        var root = TestTempDir.NewDir("lvglfind");
        var prevLvgl = Environment.GetEnvironmentVariable("LVGL_DIR");
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        try
        {
            // 中和环境变量：开发机上可能真有一条可用的 LVGL，不中和会把假树测试翻成真树测试
            Environment.SetEnvironmentVariable("LVGL_DIR", "/no/such/lvgl");
            Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, "/no/such/rt");

            var expected = CreateLvglDir(Path.Combine(root, "sdk", "kinseal", "lvgl"));

            var found = SimulatorBuildService.FindLvglDirectory(
                null, Path.Combine(root, "work"), root);

            Assert.Equal(expected, found);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LVGL_DIR", prevLvgl);
            Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, prevRt);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void BundledRuntimeLvgl_FoundUnderInstallDir()
    {
        // SDK 只带设备侧运行时：LVGL 树随运行时自持在 third_party/lvgl-*
        var root = TestTempDir.NewDir("lvglfind");
        var prevLvgl = Environment.GetEnvironmentVariable("LVGL_DIR");
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable("LVGL_DIR", "/no/such/lvgl");
            Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, "/no/such/rt");

            var runtime = Path.Combine(root, "sdk", "kinseal", "runtime");
            Directory.CreateDirectory(Path.Combine(runtime, "src", "runtime"));
            File.WriteAllText(Path.Combine(runtime, "src", "runtime", "hmi_ui_api.h"), "/* api */\n");
            var expected = CreateLvglDir(Path.Combine(runtime, "third_party", "lvgl-9.5.0"));

            var found = SimulatorBuildService.FindLvglDirectory(
                null, Path.Combine(root, "work"), root);

            Assert.Equal(expected, found);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LVGL_DIR", prevLvgl);
            Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, prevRt);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void ConfiguredDir_WinsOverBundled()
    {
        var root = TestTempDir.NewDir("lvglfind");
        var prevLvgl = Environment.GetEnvironmentVariable("LVGL_DIR");
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable("LVGL_DIR", "/no/such/lvgl");
            Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, "/no/such/rt");

            CreateLvglDir(Path.Combine(root, "sdk", "kinseal", "lvgl"));
            var mine = CreateLvglDir(Path.Combine(root, "my-lvgl"));

            var found = SimulatorBuildService.FindLvglDirectory(
                mine, Path.Combine(root, "work"), root);

            Assert.Equal(mine, found);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LVGL_DIR", prevLvgl);
            Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, prevRt);
            TestTempDir.DeleteDir(root);
        }
    }

    [Fact]
    public void NothingConfigured_ReturnsNull_EvenWithSiblingRepoPresent()
    {
        var root = TestTempDir.NewDir("lvglfind");
        var installDir = Path.Combine(root, "app");
        Directory.CreateDirectory(installDir);
        var prevLvgl = Environment.GetEnvironmentVariable("LVGL_DIR");
        var prevRt = Environment.GetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable("LVGL_DIR", "/no/such/lvgl");
            Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, "/no/such/rt");

            // 把"开发机平级仓库"摆在安装目录旁边：绝不能被认出来，否则又绑回本机布局
            var sibling = Path.Combine(root, "f1c100s-project", "chenge-rt");
            Directory.CreateDirectory(Path.Combine(sibling, "src", "runtime"));
            File.WriteAllText(Path.Combine(sibling, "src", "runtime", "hmi_ui_api.h"), "/* api */\n");
            CreateLvglDir(Path.Combine(sibling, "third_party", "lvgl-9.5.0"));

            Assert.Null(SimulatorBuildService.FindLvglDirectory(null, Path.Combine(root, "work"), installDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LVGL_DIR", prevLvgl);
            Environment.SetEnvironmentVariable(F1C100SSoBuilder.RuntimeDirEnvVar, prevRt);
            TestTempDir.DeleteDir(root);
        }
    }
}
