using System.Linq;
using Hmi.Hardware.Builtin;
using Hmi.Hardware.Contracts;
using Hmi.Hardware.Loader;
using HmiEditor.Core.Models;
using Xunit;

namespace Hmi.Hardware.Tests;

public class HardwareTargetTests
{
    [Fact]
    public void Registry_LoadBuiltin_ReturnsTwoTargets()
    {
        var registry = new HardwareTargetRegistry();
        registry.LoadBuiltinTargets(new IHardwareTarget[]
        {
            new AllwinnerF1C100STarget(),
            new RockchipRK3506Target()
        });

        Assert.Equal(2, registry.GetAllTargets().Count());
    }

    [Fact]
    public void Registry_GetTarget_ReturnsCorrectTarget()
    {
        var registry = new HardwareTargetRegistry();
        registry.LoadBuiltinTargets(new IHardwareTarget[]
        {
            new AllwinnerF1C100STarget(),
            new RockchipRK3506Target()
        });

        var kinseal = registry.GetTarget(AllwinnerF1C100STarget.TargetId);
        Assert.NotNull(kinseal);
        Assert.Equal("Allwinner F1C100S (kinseal)", kinseal!.Metadata.Name);
    }

    [Fact]
    public void F1C100S_Display_DefaultResolution_800x480()
    {
        // 板级事实：800x480 RGB666 电阻触摸面板（docs/hardware/f1c100s-kinseal/01-board-facts.md）
        var target = new AllwinnerF1C100STarget();
        Assert.Equal(800, target.Display.DefaultWidth);
        Assert.Equal(480, target.Display.DefaultHeight);
    }

    [Fact]
    public void RK3506_Display_DefaultResolution_480x272()
    {
        var target = new RockchipRK3506Target();
        Assert.Equal(480, target.Display.DefaultWidth);
        Assert.Equal(272, target.Display.DefaultHeight);
    }

    [Fact]
    public void F1C100S_ValidateProject_HighResolution_ReturnsWarning()
    {
        var target = new AllwinnerF1C100STarget();
        var project = new HmiProject();
        var screen = new HmiScreen { Name = "Big", Width = 1280, Height = 800 };
        project.Screens.Add(screen);

        var result = target.ValidateProject(project);
        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "RESOLUTION_EXCEEDS_DEFAULT");
    }

    [Fact]
    public void F1C100S_GenerateInitializationCode_ContainsDisplayCreate()
    {
        var target = new AllwinnerF1C100STarget();
        var code = target.GenerateInitializationCode(new HmiProject());
        Assert.Contains("lv_display_create", code);
        Assert.Contains("800, 480", code);
    }

    [Fact]
    public void RK3506_LvglConfig_DefaultColorDepth_16()
    {
        var target = new RockchipRK3506Target();
        Assert.Equal(16, target.LvglConfig.DefaultColorDepth);
    }

    [Fact]
    public void Registry_GetTarget_UnknownId_ReturnsNull()
    {
        var registry = new HardwareTargetRegistry();
        Assert.Null(registry.GetTarget("non-existent"));
    }

    [Fact]
    public void F1C100S_RecommendedInterfaces_ContainsSerialOnly()
    {
        var target = new AllwinnerF1C100STarget();
        Assert.Contains(CommunicationInterface.Serial, target.RecommendedInterfaces);
        Assert.DoesNotContain(CommunicationInterface.Ethernet, target.RecommendedInterfaces);
    }

    [Fact]
    public void F1C100S_SupportedCommunicationDrivers_OnlyModbusRtu()
    {
        // chenge-rt 当前只有 modbus_rtu.c（HMI_WITH_COMM=0），驱动清单按"运行时已有源码"声明
        var target = new AllwinnerF1C100STarget();
        Assert.Single(target.SupportedCommunicationDrivers);
        Assert.Contains("modbus-rtu", target.SupportedCommunicationDrivers);
    }

    [Fact]
    public void RK3506_RecommendedInterfaces_ContainsSerialOnly()
    {
        var target = new RockchipRK3506Target();
        Assert.Contains(CommunicationInterface.Serial, target.RecommendedInterfaces);
        Assert.DoesNotContain(CommunicationInterface.Ethernet, target.RecommendedInterfaces);
    }

    [Fact]
    public void RK3506_SupportedCommunicationDrivers_OnlyModbusRtu()
    {
        var target = new RockchipRK3506Target();
        Assert.Single(target.SupportedCommunicationDrivers);
        Assert.Contains("modbus-rtu", target.SupportedCommunicationDrivers);
    }
}
