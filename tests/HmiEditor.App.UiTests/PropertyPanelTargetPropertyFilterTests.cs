using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using HmiEditor.App.Views;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 「目标属性」下拉必须按控件类型过滤到**契约里真的允许**的那几项。
///
/// <para>判据的来由：下拉项与契约的 <c>bind[]</c> 之前各说各话 —— 只有 值/文本（映射成
/// <c>text</c>）、文本颜色、背景颜色、可见性四项，于是 <c>bar</c>/<c>gauge</c>/<c>knob</c>/
/// <c>slider</c>/弧 的 <c>value</c> 绑定在界面上造不出来（模型层与导出器早就支持）。
/// 反过来，选一个契约不允许的属性也不会被拦在建页期，而是导出时一条
/// <c>bind.not_bindable</c> 警告后静默丢掉 —— 画布上什么都不动。</para>
///
/// <para>与 <c>PropertyPanelTargetPropertyTests</c> 的分工：那一组管"选了哪个属性就写进哪个
/// <see cref="VariableBindingData.PropertyName"/>"，这一组只管"哪些项该出现"。</para>
/// </summary>
public class PropertyPanelTargetPropertyFilterTests
{
    private static HmiControl Make(HmiControlType type) => new()
    {
        Id = "c1",
        Type = type,
        Name = "n1",
        X = 10, Y = 20, Width = 200, Height = 100,
    };

    private static PropertyPanelView ShowPanel(HmiControl control)
    {
        var project = new HmiProject { Name = "P" };
        var screen = new HmiScreen { Id = "s1", Name = "Main" };
        project.Screens.Add(screen);

        var panel = new PropertyPanelView { CurrentScreen = screen };
        var window = new Window { Width = 900, Height = 900, Content = panel };
        window.Show();
        panel.SetProject(project);
        panel.BindControl(control);
        UiTestHost.Pump();
        return panel;
    }

    private static ComboBox TargetCombo(PropertyPanelView panel)
    {
        var found = panel.FindControl<ComboBox>("TargetPropertyCombo");
        Assert.NotNull(found);
        return found!;
    }

    /// <summary>当前控件下「目标属性」里可见项的 Tag（顺序即下拉顺序）。</summary>
    private static List<string> VisibleTargets(PropertyPanelView panel) =>
        TargetCombo(panel).Items.OfType<ComboBoxItem>()
            .Where(i => i.IsVisible)
            .Select(i => i.Tag?.ToString() ?? "")
            .ToList();

    [Theory]
    // 契约 bind：text / value / checked / color / bg_color / visible
    [InlineData(HmiControlType.Label, "Text,TextColor,BgColor,IsVisible")]
    [InlineData(HmiControlType.Bar, "Value,TextColor,IsVisible")]
    [InlineData(HmiControlType.Gauge, "Value,TextColor,IsVisible")]
    [InlineData(HmiControlType.Knob, "Value,IsVisible")]
    [InlineData(HmiControlType.Slider, "Value,IsVisible")]
    [InlineData(HmiControlType.Arc, "Value,IsVisible")]          // shape.bind：visible/enabled/value
    [InlineData(HmiControlType.Pipe, "Value,IsVisible")]
    [InlineData(HmiControlType.Roller, "Value,IsVisible")]
    [InlineData(HmiControlType.Switch, "IsChecked,IsVisible")]
    [InlineData(HmiControlType.CheckBox, "Text,IsChecked,IsVisible")]
    [InlineData(HmiControlType.TextArea, "Text,Value,TextColor,IsVisible")]
    [InlineData(HmiControlType.Value, "Text,TextColor,BgColor,IsVisible")]
    [InlineData(HmiControlType.EnumText, "Text,TextColor,BgColor,IsVisible")]
    [InlineData(HmiControlType.Table, "IsVisible")]
    public void TargetProperties_AreFilteredByContract(HmiControlType type, string expected)
    {
        var wanted = expected.Split(',').ToList();
        UiTestHost.Run(() =>
        {
            var panel = ShowPanel(Make(type));
            Assert.Equal(wanted, VisibleTargets(panel));
        });
    }

    /// <summary>
    /// 选中项必须是**可见**的那一项。否则下拉显示空白，而 <c>CurrentBindingPropertyName</c>
    /// 仍能读到隐藏项的 Tag —— 用户会以为没选，实际在往那个属性写绑定。
    /// </summary>
    [Fact]
    public void DefaultSelection_IsAlwaysAVisibleItem()
    {
        UiTestHost.Run(() =>
        {
            foreach (var type in new[] { HmiControlType.Knob, HmiControlType.Switch, HmiControlType.Table })
            {
                var panel = ShowPanel(Make(type));
                var selected = TargetCombo(panel).SelectedItem as ComboBoxItem;

                Assert.NotNull(selected);
                Assert.True(selected!.IsVisible,
                    $"{type}: 默认选中了隐藏项 {selected.Tag}（下拉会显示空白）");
            }
        });
    }

    /// <summary>
    /// 弧必须显示变量绑定区 —— 不带 <c>bind.value</c> 的弧没有进度段，而绑定只能在
    /// 这个区里建；名单里漏了 Arc 时，进度弧在界面上无路可走。
    /// </summary>
    [Fact]
    public void Arc_ShowsVariableBindingSection()
    {
        UiTestHost.Run(() =>
        {
            var panel = ShowPanel(Make(HmiControlType.Arc));
            var expander = panel.FindControl<Control>("VariableBindingExpander");
            Assert.NotNull(expander);
            Assert.True(expander!.IsVisible, "弧控件没有变量绑定区（进度弧的 value 绑定无处可建）");
        });
    }
}
