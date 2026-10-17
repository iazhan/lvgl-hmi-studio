using Hmi.Hardware.Builtin;
using Hmi.Hardware.Contracts;
using Hmi.Hardware.Loader;
using HmiEditor.CodeGen;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using Xunit;

namespace Hmi.Hardware.Tests;

/// <summary>
/// kinseal（Allwinner F1C100S）目标插件的声明契约。
///
/// <para>每个断言的值都来自设备侧运行时的实际形态（证据链写在
/// <c>docs/hardware/f1c100s-kinseal/01-board-facts.md</c>，源头在姊妹仓库
/// <c>f1c100s-project/chenge-rt/</c>）：运行时在 dlopen 时严格比对
/// LVGL/ABI 版本，声明值漂移 = 板上拒载，所以这里逐项钉住。</para>
/// </summary>
public class F1C100STargetTests
{
    private static readonly AllwinnerF1C100STarget Target = new();

    // ── 标识 ──

    [Fact]
    public void TargetId_MatchesTheRuntimeSelfReportedId()
    {
        // chenge-rt src/runtime/board_profile.h：HMI_BOARD_ID = "f1c100s-kinseal"
        // （hmi_runtime.h 里 HMI_TARGET_ID = HMI_BOARD_ID）—— 生成物头部写这个 ID
        Assert.Equal("f1c100s-kinseal", Target.Metadata.Id);
        Assert.Equal("f1c100s-kinseal", AllwinnerF1C100STarget.TargetId);
    }

    // ── 显示 ──

    [Fact]
    public void Display_Is800x480_Rgb666_32Bit()
    {
        Assert.Equal(800, Target.Display.DefaultWidth);
        Assert.Equal(480, Target.Display.DefaultHeight);
        Assert.Equal(DisplayInterface.RGB666, Target.Display.Interface);
        // 运行时按 32 位编译（LV_COLOR_DEPTH 32 + fbdev bpp=32 契约）；
        // 不声明 RGB565 —— 那不是本运行时的形态
        Assert.Single(Target.Display.SupportedColorDepths);
        Assert.Contains(ColorDepth.ARGB8888, Target.Display.SupportedColorDepths);
    }

    // ── 资源预算 ──

    [Fact]
    public void Resources_RamIs32MiB_FlashIsTheOtaSoGate()
    {
        // 32 MiB 是 SoC 内置 DDR 总量（板上 MemAvailable 仅 11–13 MB，无 swap）
        Assert.Equal(32L * 1024 * 1024, Target.Resources.RamSizeBytes);
        // Flash 轴语义 = 可部署产物体积：SD 启动、NAND 未用、rootfs 分区随卡，
        // 唯一代码钉死的上限是 OTA 校验门（hmi_ota.sh MAX_BYTES=8388608）
        Assert.Equal(8L * 1024 * 1024, Target.Resources.FlashSizeBytes);
        // 与 chenge-rt config/lv_conf.h 的 LV_MEM_SIZE 一致
        Assert.Equal(8L * 1024 * 1024, Target.Resources.RecommendedLvglHeapBytes);
        Assert.Equal(800, Target.Resources.MaxTextureSize);
    }

    // ── LVGL / ABI 契约 ──

    [Fact]
    public void LvglConfig_MatchesTheRuntimeBuild()
    {
        Assert.Equal(32, Target.LvglConfig.DefaultColorDepth);
        Assert.Equal("9.5", Target.LvglConfig.LvglVersion);
        Assert.Equal(3u, Target.LvglConfig.AbiVersion);
        // 单缓冲 PARTIAL 渲染（board_fbdev.c：set_buffers 第二缓冲为 NULL）
        Assert.False(Target.LvglConfig.UseDoubleBuffering);
        Assert.Contains("LV_USE_FREETYPE", Target.LvglConfig.EnabledFeatures);
        // PNG 走内建 lodepng（LV_USE_LIBPNG=0，rootfs 不装 libpng）
        Assert.Contains("LV_USE_LODEPNG", Target.LvglConfig.EnabledFeatures);
        Assert.DoesNotContain("LV_USE_PNG", Target.LvglConfig.EnabledFeatures);
    }

    [Fact]
    public void DeclaredLvglVersion_IsKnownToTheEditor()
    {
        Assert.Contains(Target.LvglConfig.LvglVersion, LvglApiCompatChecker.SupportedLvglVersions);
    }

    [Fact]
    public void ValidateProject_SameMajorLvglVersion_IsAccepted()
    {
        var result = Target.ValidateProject(new HmiProject { LvglVersion = "9.4" });

        Assert.DoesNotContain(result.Issues, i => i.Code == "LVGL_VERSION_MISMATCH");
    }

    [Fact]
    public void ValidateProject_Lvgl8Project_IsAnError()
    {
        // 8.3 工程 + 9.5 运行时：生成物留着 9.x 已改名符号 ⇒ 板上编译不过
        var result = Target.ValidateProject(new HmiProject { LvglVersion = "8.3" });

        var issue = Assert.Single(result.Issues, i => i.Code == "LVGL_VERSION_MISMATCH");
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateProject_OversizedScreen_WarnsButStaysValid()
    {
        var project = new HmiProject();
        project.Screens.Add(new HmiScreen { Name = "Big", Width = 1024, Height = 600 });

        var result = Target.ValidateProject(project);

        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "RESOLUTION_EXCEEDS_DEFAULT");
    }

    // ── 工具链 ──

    [Fact]
    public void Toolchain_IsBuildrootGnueabi_Arm926ejSoftFloat()
    {
        Assert.Equal("arm-buildroot-linux-gnueabi-", Target.Toolchain.CrossCompilerPrefix);
        // 与 buildroot .config 的 BR2_GCC_TARGET_CPU="arm926ej-s" + gnueabi(soft-float) 同值
        Assert.Contains("-mcpu=arm926ej-s", Target.Toolchain.CompileFlags);
        Assert.Contains("-mfloat-abi=soft", Target.Toolchain.CompileFlags);
    }

    // ── 通信 ──

    [Fact]
    public void Communication_SerialOnly_ModbusRtuOnly()
    {
        // kinseal 无网口/CAN；ttyS0 是调试控制台不列入
        Assert.Single(Target.RecommendedInterfaces);
        Assert.Contains(CommunicationInterface.Serial, Target.RecommendedInterfaces);
        Assert.Single(Target.SupportedCommunicationDrivers);
        Assert.Contains("modbus-rtu", Target.SupportedCommunicationDrivers);
    }

    // ── 初始化样例 ──

    [Fact]
    public void GenerateInitializationCode_ContainsDisplayCreate()
    {
        var code = Target.GenerateInitializationCode(new HmiProject());

        Assert.Contains("lv_display_create", code);
        Assert.Contains("800, 480", code);
    }

    // ── 编辑器默认值与目标对齐 ──

    [Fact]
    public void EditorDefaults_AlignWithTheTargetDisplay()
    {
        // 「新工程默认模板/工厂兜底」与「目标板分辨率」三处必须同一个值：
        // 漂移的后果是 kinseal 工程里新屏幕默认 1024×600，每屏都吃分辨率告警
        var project = new HmiProject();

        Assert.Equal(Target.Display.DefaultWidth, project.ScreenDefaultsConfig.DefaultWidth);
        Assert.Equal(Target.Display.DefaultHeight, project.ScreenDefaultsConfig.DefaultHeight);
        Assert.Equal(ScreenFactory.FallbackWidth, Target.Display.DefaultWidth);
        Assert.Equal(ScreenFactory.FallbackHeight, Target.Display.DefaultHeight);
    }

    // ── 注册表接线 ──

    [Fact]
    public void Registry_RegisterBuiltin_RetrievableById()
    {
        var registry = new HardwareTargetRegistry();
        registry.LoadBuiltinTargets(new IHardwareTarget[] { Target });

        var found = registry.GetTarget("f1c100s-kinseal");

        Assert.NotNull(found);
        Assert.Same(Target, found);
    }
}
