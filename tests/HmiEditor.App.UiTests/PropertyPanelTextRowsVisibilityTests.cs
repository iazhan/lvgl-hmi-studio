using System;
using System.Linq;
using Avalonia.Controls;
using HmiEditor.App.Views;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// **「字号 / 水平对齐 / 字体族」三行只对真正读这些键的控件类型显示**。
///
/// <para>名单的单一来源是 <c>PropertyPanelView.AppearanceRows.cs</c>（它同时管「内容 / 背景色 /
/// 边框 / 圆角 / 文字色 / 不透明度」）。判据按**运行时是否真的读**来定，不按"有没有文本"猜：</para>
/// <list type="bullet">
/// <item><c>font_size</c>：运行时按类型取 <c>ui_font(fs)</c>，combobox / message / tabview
/// 等硬编码 16 ⇒ 这些类型不显示；</item>
/// <item><c>align</c>：只有 <c>apply_text_style</c> / <c>apply_text_valign</c> 那几类读；
/// 输入框是单独一条（<c>build_input</c> 把 text_align 设在内部 label 上），只有水平、没有垂直；</item>
/// <item><c>font_family</c>：导出器只写 <c>font_size</c>（<c>SetFont</c>），**从不读字体族** ⇒
/// 全类型隐藏（此前的名单把它算成"文本类可见"，那是按生成器文本块猜的，不是按消费端）。</item>
/// </list>
/// </summary>
// DataRootSandbox 会改进程级数据根，必须与大队串行（见 LocalizationRefreshUiTests 的集合定义）
[Collection("Language-global")]
public class PropertyPanelTextRowsVisibilityTests
{
    // 判的是**整行**（外观组每行是一个具名 Grid）：行内输入框永远可见，由行承载显隐
    private static readonly string[] Rows = { "RowFontFamily", "RowFontSize", "RowTextAlign" };

    private static void AssertRow(PropertyPanelView panel, string row, bool expected, string because)
    {
        var control = ControlWiringFace.Find<Control>(panel, row);
        Assert.True(control.IsVisible == expected,
            $"{row} 的可见性应为 {expected}（{because}），实际 {control.IsVisible}");
    }

    [Fact]
    public void TextRows_AreVisibleForTextBearingTypes_AndHiddenForTheRest()
    {
        using var sandbox = new DataRootSandbox("panel-text-rows-visibility");

        UiTestHost.Run(() =>
        {
            var project = new HmiProject { Name = "P" };
            var screen = new HmiScreen { Id = "s1", Name = "Main" };
            project.Screens.Add(screen);

            var panel = new PropertyPanelView { CurrentScreen = screen };
            var window = new Window { Width = 1000, Height = 900, Content = panel };
            window.Show();
            UiTestHost.Pump(5);
            panel.SetProject(project);
            UiTestHost.Pump(2);

            void Bind(HmiControlType type)
            {
                var control = new HmiControl
                {
                    Id = "c_" + type, Name = "n_" + type, Type = type,
                    X = 10, Y = 10, Width = 120, Height = 40, Text = "温度",
                };
                screen.Controls.Add(control);
                panel.BindControl(control);
                UiTestHost.Pump(3);
            }

            try
            {
                // 字号：运行时真的按 font_size 取字体的类型
                foreach (var type in new[]
                         {
                             HmiControlType.Label, HmiControlType.Button, HmiControlType.CheckBox,
                             HmiControlType.TextArea, HmiControlType.Value, HmiControlType.EnumText,
                             HmiControlType.AsciiDisplay, HmiControlType.Roller, HmiControlType.Table,
                             HmiControlType.HistoryCurve,
                         })
                {
                    Bind(type);
                    AssertRow(panel, "RowFontSize", true, type.ToString());
                }

                // 水平对齐：apply_text_style 那条链上的类型 + 输入框（build_input 自己读 align）
                foreach (var type in new[]
                         {
                             HmiControlType.Label, HmiControlType.Value,
                             HmiControlType.EnumText, HmiControlType.AsciiDisplay, HmiControlType.Message,
                             HmiControlType.TextArea,
                         })
                {
                    Bind(type);
                    AssertRow(panel, "RowTextAlign", true, type.ToString());
                }

                // 按钮文字居中固定、combobox 没有 align 键 ⇒ 对齐行不显示
                foreach (var type in new[]
                         {
                             HmiControlType.Button, HmiControlType.CheckBox,
                             HmiControlType.Dropdown,
                         })
                {
                    Bind(type);
                    AssertRow(panel, "RowTextAlign", false, type.ToString());
                }

                // 字体族：导出器从不读 ⇒ 连 Label 都不显示
                Bind(HmiControlType.Label);
                AssertRow(panel, "RowFontFamily", false, "Label（导出器不读字体族）");

                // 没有文本的控件：三行全隐藏
                foreach (var type in new[]
                         {
                             HmiControlType.Rectangle, HmiControlType.Slider, HmiControlType.Led,
                             HmiControlType.Image, HmiControlType.Gauge,
                         })
                {
                    Bind(type);
                    for (var i = 0; i < Rows.Length; i++)
                        AssertRow(panel, Rows[i], false, type.ToString());
                }
            }
            finally
            {
                window.Close();
                UiTestHost.Pump();
            }
        });
    }
}
