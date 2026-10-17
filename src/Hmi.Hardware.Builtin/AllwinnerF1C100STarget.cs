using Hmi.Hardware.Contracts;
using HmiEditor.Core.Models;

namespace Hmi.Hardware.Builtin;

/// <summary>
/// Allwinner F1C100S 硬件目标实现 —— kinseal 板（本 fork 的目标板）。
/// 800x480 RGB666 电阻触摸面板，板载 32 MiB DDR（无 FPU/GPU），SD 卡启动。
///
/// 板级事实源在姊妹仓库 <c>f1c100s-project/chenge-rt/</c>（设备侧运行时）与
/// <c>f1c100s-os/chenge-bsp/board/kinseal/</c>（构建/烧写）；本类的每个声明值
/// 都在注释里给出可核对的出处，详见 <c>docs/hardware/f1c100s-kinseal/01-board-facts.md</c>。
/// </summary>
public sealed class AllwinnerF1C100STarget : IHardwareTarget
{
    /// <summary>
    /// 目标 ID。与运行时自报 ID 一致：chenge-rt 的 <c>hmi_runtime.h</c> 里
    /// <c>HMI_TARGET_ID = HMI_BOARD_ID</c>，kinseal 即 <c>"f1c100s-kinseal"</c>
    /// （见其 <c>src/runtime/board_profile.h</c>）—— 生成物头部写的就是这个 ID。
    /// </summary>
    public const string TargetId = "f1c100s-kinseal";

    public HardwareTargetMetadata Metadata => new(
        Id: TargetId,
        Name: "Allwinner F1C100S (kinseal)",
        Vendor: "Allwinner",
        Architecture: "ARM926EJ-S (ARMv5TEJ, no FPU)",
        // 对齐 chenge-rt 的 HMI_RUNTIME_VERSION_STR（src/runtime/hmi_runtime.h）
        Version: "1.1.0"
    );

    public DisplayConfig Display => new(
        DefaultWidth: 800,
        DefaultHeight: 480,
        // 运行时按 32 位编译（chenge-rt config/lv_conf.h：LV_COLOR_DEPTH 32；
        // fbdev 契约为 bpp=32 / XRGB8888，见 src/plat/board_fbdev.c）——
        // 不列 RGB565：那不是本运行时的编译形态。
        SupportedColorDepths: new[] { ColorDepth.ARGB8888 },
        Interface: DisplayInterface.RGB666,
        MaxRefreshRate: 60
    );

    // RAM：32 MiB 是 SoC 内置 DDR 的总量（f1c100s-project/docs/kinseal/14：MemAvailable 仅 11–13 MB），
    // 无 swap ⇒ 仪表盘的 RAM 轴必须按 32 MiB 判读，大工程会在这里报警。
    //
    // Flash：SD 卡启动，NAND（128 MiB）未被使用；genimage 的 rootfs 分区 size=0（随卡），
    // 没有固定的"板载 Flash 容量"可声明。唯一由代码钉死的部署上限是 OTA 校验门
    // chenge-bsp/board/common/hmi/hmi_ota.sh 的 MAX_BYTES=8388608（8 MiB）——
    // 仪表盘 flash 轴的语义是"可部署产物体积"，故取 8 MiB。
    public ResourceConstraints Resources => new(
        FlashSizeBytes: 8L * 1024 * 1024,
        RamSizeBytes: 32L * 1024 * 1024,
        // 与 chenge-rt config/lv_conf.h 的 LV_MEM_SIZE 一致（8 MiB，LV_USE_STDLIB_MALLOC）
        RecommendedLvglHeapBytes: 8 * 1024 * 1024,
        MaxTextureSize: 800
    );

    public LvglConfig LvglConfig => new(
        DefaultColorDepth: 32,
        // 本字段语义 = 板载 RAM 容量（MB）：T113=128、RK3506=64，见各自 Resources
        DefaultMemorySize: 32,
        // 单缓冲 PARTIAL 渲染：board_fbdev.c 用 lv_display_set_buffers(disp, buf, NULL, ...)
        // （第二个缓冲为 NULL）+ flush 侧 180° 软件旋转（kinseal 面板倒装）
        UseDoubleBuffering: false,
        // LV_USE_FREETYPE：工程字体走宿主 hmi_resolve_font（lv_freetype_font_create，
        // 构建期子集 TTF）；PNG 走内建 lodepng（不是 libpng：LV_USE_LIBPNG=0，
        // rootfs 不装 libpng）。均见 chenge-rt config/lv_conf.h。
        EnabledFeatures: new[] { "LV_USE_FREETYPE", "LV_USE_LODEPNG" },
        CustomDefines: new Dictionary<string, string>
        {
            ["LV_COLOR_DEPTH"] = "32",
            ["LV_MEM_SIZE"] = "(8U * 1024U * 1024U)"
        },
        // 与 chenge-rt src/runtime/hmi_ui_api.h 的 HMI_UI_LVGL_VERSION(0x905)/
        // HMI_UI_ABI_VERSION(3u) 一致 —— 宿主 dlopen 时严格比对，不符即拒载
        LvglVersion: "9.5",
        AbiVersion: 3u
    );

    public ToolchainConfig Toolchain => new(
        // chenge-rt Makefile 板端构建用的 buildroot 2020.02 工具链
        CrossCompilerPrefix: "arm-buildroot-linux-gnueabi-",
        CompilerPath: "",
        // 工具链自身即按 ARM926EJ-S 软浮点配置（buildroot .config：
        // BR2_GCC_TARGET_CPU="arm926ej-s" + BR2_arm926t=y，gnueabi=soft-float）；
        // chenge-rt 板端 Makefile 不传架构 flag，靠工具链默认值。此处显式写出同值。
        CompileFlags: new[] { "-mcpu=arm926ej-s", "-mfloat-abi=soft" },
        LinkerScriptPath: "",
        // .so 链接为 -shared -nostartfiles，符号由宿主 -rdynamic 解析：
        // 不链 freetype（宿主静态吸收）、不链 dl（宿主进程已载）。
        LibraryDependencies: new[] { "lvgl", "m" }
    );

    // kinseal 物理通讯口：仅第二串口（UART2 → /dev/ttyS1）；
    // ttyS0 是调试控制台，无网口、无 CAN（chenge-rt src/runtime/board_profile.h）
    public IReadOnlyList<CommunicationInterface> RecommendedInterfaces =>
        new[] { CommunicationInterface.Serial };

    // chenge-rt src/comm/modbus_rtu.c 存在，但当前构建 HMI_WITH_COMM=0（通信首期只搬框架）；
    // 驱动清单按"运行时已有源码"声明，不代表默认启用
    public IReadOnlyList<string> SupportedCommunicationDrivers =>
        new[] { "modbus-rtu" };

    public ValidationResult ValidateProject(HmiProject project)
    {
        var issues = new List<ValidationIssue>();

        foreach (var screen in project.Screens)
        {
            if (screen.Width > Display.DefaultWidth || screen.Height > Display.DefaultHeight)
            {
                issues.Add(new(ValidationSeverity.Warning,
                    "RESOLUTION_EXCEEDS_DEFAULT",
                    $"Screen '{screen.Name}' ({screen.Width}x{screen.Height}) 超过默认分辨率",
                    new object[] { screen.Name, screen.Width, screen.Height }));
            }
        }

        // 工程 LVGL 目标 ↔ 本板运行时（未声明 LvglVersion 的目标自动跳过）
        if (TargetValidation.CheckProjectLvglVersion(LvglConfig, project.LvglVersion) is { } lvglIssue)
            issues.Add(lvglIssue);

        return new(issues.All(i => i.Severity != ValidationSeverity.Error), issues);
    }

    public IReadOnlyDictionary<string, object> GetTemplateParameters() => new Dictionary<string, object>
    {
        ["target_id"] = Metadata.Id,
        ["color_depth"] = LvglConfig.DefaultColorDepth,
        ["lvgl_heap_size"] = Resources.RecommendedLvglHeapBytes,
        ["cross_compiler_prefix"] = Toolchain.CrossCompilerPrefix
    };

    // 生成物是注入 C 源码的文本，与 T113 目标同风格用英文（文案本地化不覆盖这段）
    public string GenerateInitializationCode(HmiProject project) =>
        "// kinseal (F1C100S): display/touch init is done by the host hmi-rt (fbdev + /etc/hmi-cal);\n" +
        "// the project deploys as libhmi_ui.so, so init does not belong to project code\n" +
        "lv_init();\n" +
        "lv_display_create(800, 480);\n";
}
