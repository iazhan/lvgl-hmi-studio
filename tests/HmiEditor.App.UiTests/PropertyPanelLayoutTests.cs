using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using HmiEditor.App.Views;
using HmiEditor.Core.Models;
using HmiEditor.TestSupport;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 属性面板的**布局**判据：行定义要够、隐藏行不能占位。
///
/// <para>两条都是"看起来像属性丢了/面板空了"的根因，而且都不会报错：</para>
/// <list type="number">
/// <item>Avalonia 的 Grid 对超出行定义数的 <c>Grid.Row</c> 不报错，而是把它们全部叠在
/// 最后一行 —— 面板上表现为"参数被压成一行"（曾经：控件细节区 31 条定义装 68 行）。</item>
/// <item><c>RowSpacing</c> 对**隐藏行**照样生效 —— 88 行 × 4px ≈ 350px 空白，面板上表现为
/// "顶部一大片空白"。<c>Expander Grid</c> 的全局样式给 4，所以两个参数区必须显式写 0。</item>
/// </list>
/// </summary>
public class PropertyPanelLayoutTests
{
    private const string AxamlRelative = "src/HmiEditor.App/Views/PropertyPanelView.axaml";

    [Fact]
    public void EveryGrid_DeclaresEnoughRowsForTheHighestRowItUses()
    {
        var path = Path.Combine(SourceScanner.RepoRoot, AxamlRelative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"未找到 {AxamlRelative}");

        var lines = File.ReadAllLines(path);
        var problems = new List<string>();

        // 深度栈：进 <Grid ...RowDefinitions="..."> 时压栈，遇 </Grid> 出栈并结算
        var open = new Stack<(int Depth, int RowCount, int StartLine)>();
        var depth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, @"<Grid\b"))
            {
                depth++;
                var m = Regex.Match(line, @"RowDefinitions=""([^""]*)""");
                if (m.Success)
                {
                    var count = m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
                    open.Push((depth, count, i));
                }
            }

            if (line.Contains("</Grid>", StringComparison.Ordinal))
            {
                if (open.Count > 0 && open.Peek().Depth == depth)
                {
                    var (_, count, start) = open.Pop();
                    var maxRow = 0;
                    for (var k = start; k <= i; k++)
                        foreach (Match r in Regex.Matches(lines[k], @"Grid\.Row=""(\d+)"""))
                            maxRow = Math.Max(maxRow, int.Parse(r.Groups[1].Value));

                    if (maxRow >= count)
                        problems.Add(
                            $"第 {start + 1} 行起的 Grid：只声明了 {count} 行（0..{count - 1}），"
                            + $"却用到 Grid.Row={maxRow} —— 超出的控件会全部叠在最后一行上");
                }
                depth--;
            }
        }

        Assert.True(problems.Count == 0,
            $"{AxamlRelative} 的行定义不够：\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// 「内容 / 背景色 / 边框 / 圆角 / 文字色 / 字号 / 对齐 / 不透明度」只对**运行时真的会读**
    /// 的类型显示。全类型都显示的话，用户在 Slider 上改背景色既不进导出也不进位图。
    /// </summary>
    [Fact]
    public void AppearanceRows_FollowWhatTheRuntimeActuallyReads()
    {
        UiTestHost.Run(() =>
        {
            var screen = new HmiScreen { Id = "s1", Name = "S1", Width = 1024, Height = 600 };
            var project = new HmiProject();
            project.Screens.Add(screen);

            var panel = new PropertyPanelView { CurrentScreen = screen };
            var window = new Window { Width = 900, Height = 900, Content = panel };
            window.Show();
            panel.SetProject(project);

            bool Visible(string name) => ControlWiringFace.Find<Control>(panel, name).IsVisible;

            // 判的是**整行**（行 Grid），不是行内的输入框 —— 输入框永远可见，由行承载显隐
            string[] rows =
            {
                "RowText", "RowBgColor", "RowRadius", "RowBorderWidth", "RowBorderColor",
                "RowTextColor", "RowFontSize", "RowTextAlign",
            };

            // 标签：内容 / 背景 / 边框 / 圆角 / 文字色 / 字号 / 对齐 全都真的生效
            panel.BindControl(new HmiControl { Type = HmiControlType.Label, Name = "L1", Text = "x" });
            UiTestHost.Pump();
            foreach (var row in rows)
                Assert.True(Visible(row), $"标签上 {row} 应可见（运行时会读）");

            // 滑块：指示器色之外，通用框样式（背景 / 边框 / 圆角）与不透明度也由运行时的
            // 建页收口统一应用，所以这些行都要出现；文字/字号/对齐它没有。
            panel.BindControl(new HmiControl { Type = HmiControlType.Slider, Name = "S1" });
            UiTestHost.Pump();
            foreach (var row in new[] { "RowBgColor", "RowRadius", "RowBorderWidth", "RowBorderColor", "RowOpacity" })
                Assert.True(Visible(row), $"滑块上 {row} 应可见（建页收口会应用）");
            foreach (var row in new[] { "RowText", "RowTextColor", "RowFontSize", "RowTextAlign" })
                Assert.False(Visible(row), $"滑块上 {row} 不该出现（运行时不读这个键）");

            // 字体族：导出器只写 font_size，全类型都不该出现
            panel.BindControl(new HmiControl { Type = HmiControlType.Label, Name = "L2", Text = "x" });
            UiTestHost.Pump();
            Assert.False(Visible("RowFontFamily"), "字体族导出器不读，不该显示");
        });
    }

    /// <summary>
    /// 外观组一行都不适用时（如 Keyboard：导出器根本不产出这个控件），整组要收起，
    /// 否则面板上挂一个空白卡片，用户还会以为设了有用。
    /// </summary>
    [Fact]
    public void AppearanceGroup_IsHiddenEntirelyWhenNoRowApplies()
    {
        UiTestHost.Run(() =>
        {
            var screen = new HmiScreen { Id = "s1", Name = "S1", Width = 1024, Height = 600 };
            var project = new HmiProject();
            project.Screens.Add(screen);

            var panel = new PropertyPanelView { CurrentScreen = screen };
            var window = new Window { Width = 900, Height = 900, Content = panel };
            window.Show();
            panel.SetProject(project);

            panel.BindControl(new HmiControl { Type = HmiControlType.Label, Name = "L1", Text = "x" });
            UiTestHost.Pump();
            Assert.True(ControlWiringFace.Find<Control>(panel, "AppearanceExpander").IsVisible,
                "标签有底色/文字色/字号/对齐，外观组应显示");

            panel.BindControl(new HmiControl { Type = HmiControlType.Keyboard, Name = "K1" });
            UiTestHost.Pump();
            Assert.False(ControlWiringFace.Find<Control>(panel, "AppearanceExpander").IsVisible,
                "键盘控件导出器直接拒绝，外观组应收起（不能留一张空卡片）");
        });
    }

    /// <summary>
    /// 外观组里隐藏的行不许占位：整组高度就等于可见行的高度，首行必须贴着顶部
    /// （此前是 12 个空 Grid 各占 RowSpacing ⇒ 顶部一片空白）。
    ///
    /// <para>做法上每个类型**新建一个面板、先 BindControl 再 Show** —— 同一个面板反复换控件时
    /// 首轮布局看到的还是旧可见性，量出来的位置不可信。</para>
    /// </summary>
    [Fact]
    public void AppearanceGroup_HasNoLeadingGap()
    {
        UiTestHost.Run(() =>
        {
            var gaps = new List<string>();

            foreach (var (type, expectedRows) in new[]
                     {
                         // 指示器颜色 + 背景/圆角/边框×2 + 不透明度（visible 已归入 GeometryExpander）
                         (HmiControlType.Slider, 6),
                         // 背景/圆角/边框×2/文字色/字号/对齐×2/字形/字距/行距/超长 + 不透明度
                         (HmiControlType.Label, 13),
                     })
            {
                var screen = new HmiScreen { Id = "s1", Name = "S1", Width = 1024, Height = 600 };
                var project = new HmiProject();
                project.Screens.Add(screen);

                var panel = new PropertyPanelView { CurrentScreen = screen };
                panel.SetProject(project);
                panel.BindControl(new HmiControl { Id = "c", Name = "n", Type = type, Text = "x" });

                var window = new Window { Width = 900, Height = 900, Content = panel };
                window.Show();
                UiTestHost.Pump(5);

                var root = ControlWiringFace.Find<StackPanel>(panel, "AppearanceExpander");

                var visible = root.Children.OfType<Grid>().Where(c => c.IsVisible).Cast<Control>().ToList();
                if (visible.Count != expectedRows)
                    gaps.Add($"{type}: 可见行 {visible.Count} ≠ {expectedRows}");
                if (visible.Count > 0 && visible[0].Bounds.Y > 25)
                    gaps.Add($"{type}: 首行距顶部 {visible[0].Bounds.Y:0.#}px（分区标题后仍有隐藏行空洞）");
                if (visible.Count > 0 && Math.Abs(root.Bounds.Height - visible[^1].Bounds.Bottom) > 1)
                    gaps.Add($"{type}: 组高 {root.Bounds.Height:0.#} ≠ 内容底 {visible[^1].Bounds.Bottom:0.#}");

                window.Close();
                UiTestHost.Pump();
            }

            Assert.True(gaps.Count == 0, string.Join("\n", gaps));
        });
    }

    /// <summary>
    /// 隐藏行不许占位：旋转那组行是 <c>Grid.Row=33</c>，上面 33 行对消息条全是隐藏的，
    /// 所以它到网格顶部的距离必须是 0（允许 1px 的布局取整误差）。按 4px 行距算，
    /// 33 个隐藏行会顶出 130px 以上的空白 —— 那正是用户看到的那一片。
    /// </summary>
    [Fact]
    public void HiddenRows_DoNotLeaveVerticalGap()
    {
        UiTestHost.Run(() =>
        {
            var screen = new HmiScreen { Id = "s1", Name = "S1", Width = 1024, Height = 600 };
            var project = new HmiProject();
            project.Screens.Add(screen);

            var panel = new PropertyPanelView { CurrentScreen = screen };
            var window = new Window { Width = 900, Height = 900, Content = panel };
            window.Show();
            panel.SetProject(project);
            panel.BindControl(new HmiControl
            {
                Type = HmiControlType.Message,
                Name = "Msg1",
                MessageText = "1",
            });
            UiTestHost.Pump();

            var rotation = ControlWiringFace.Find<Control>(panel, "RotationBox");
            Assert.True(rotation.IsVisible, "旋转角度行对消息条应可见（所有控件通用）");

            // Bounds 是相对父 Grid 的坐标；上面 33 行全隐藏 ⇒ 应贴着网格顶部
            var top = rotation.Bounds.Y;
            Assert.True(top < 8,
                $"旋转角度行距参数网格顶部 {top:0.#}px —— 隐藏行仍在占 RowSpacing（预期 < 8px）");
        });
    }
}
