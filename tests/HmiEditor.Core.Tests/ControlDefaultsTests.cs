using System;
using System.Linq;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using Xunit;

namespace HmiEditor.Core.Tests;

/// <summary>
/// 工具箱新建控件的默认外观（<see cref="ControlDefaults"/>）。
///
/// <para>这些默认值会直接进导出产物，所以"拖出来是什么样"必须逐个类型钉住 —— 只看代码容易
/// 看漏一格（加载动画此前就带着一块橙色底，运行时把它画成圆角方块盖住了弧）。</para>
/// </summary>
public class ControlDefaultsTests
{
    /// <summary>没有底色的类型：加载动画只有一圈弧，文字类没有框。</summary>
    [Theory]
    [InlineData(HmiControlType.Spinner)]
    [InlineData(HmiControlType.Label)]
    [InlineData(HmiControlType.CheckBox)]
    public void NewControl_HasNoBackground_ForFramelessTypes(HmiControlType type)
        => Assert.Null(ControlDefaults.NewControlBgColor(type));

    /// <summary>其余每个类型都必须有底色：没有底色的控件在 800×480 深色屏上等于看不见。</summary>
    [Fact]
    public void NewControl_HasBackground_ForEveryOtherType()
    {
        var frameless = new[] { HmiControlType.Spinner, HmiControlType.Label, HmiControlType.CheckBox };
        var missing = Enum.GetValues<HmiControlType>()
            .Where(t => !frameless.Contains(t))
            .Where(t => string.IsNullOrWhiteSpace(ControlDefaults.NewControlBgColor(t)))
            .ToList();

        Assert.True(missing.Count == 0, $"这些类型新建后没有底色：{string.Join(", ", missing)}");
    }

    /// <summary>灯珠的底色就是它的"亮色"，必须有值 —— 缺了就是一颗看不见的灯。</summary>
    [Fact]
    public void NewLed_HasLitColor() =>
        Assert.Equal("#22c55e", ControlDefaults.NewControlBgColor(HmiControlType.Led));

    /// <summary>亮度缺省是满亮（255）：128 会让新拖出来的灯比面板上选的颜色暗一半。</summary>
    [Fact]
    public void NewControl_BrightnessDefaultsToFull() =>
        Assert.Equal(255, new HmiControl().Brightness);
}
