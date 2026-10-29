using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using HmiEditor.App.Views;
using HmiEditor.Core.Coverage;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// **行所在容器 × 登记表分区** 的一致性守卫。
///
/// <para><b>为什么需要</b>：面板按控件类型把行摆进五个顶层区，而“这一行属于哪个区”的**唯一
/// 文字依据**是 <see cref="PropertyCoverage.Section"/>。此前没有任何判据读那个字段 ——
/// 实际发生过两种漂移：① 交互行（二次确认 / 按下保持 / 长按）留在「数据与显示」；
/// ② 同名属性在不同类型上落在不同区（<c>text</c>：文本类在「内容与文字」，消息条 /
/// 二维码 / 枚举文本在「数据与显示」）。两种都只能靠人眼发现。</para>
///
/// <para><b>判据</b>：对每个 <c>Closed</c> 登记（有编辑器名的），沿**逻辑树**找出它落在哪个
/// 顶层容器里，断言该容器属于 <see cref="Section"/> 声明允许的容器集合。用逻辑树而不是
/// 可视树：隐藏行（绝大多数行对当前类型是隐藏的）不在可视树里，走可视树会漏掉一大半。</para>
///
/// <para>允许集合按区的语义给：<c>behavior</c> 的行既可能在「绑定与状态」（状态覆盖、
/// 初始选中），也可能在「动作与高级」的「交互行为」组；<c>figure</c> 的外观公共行
/// （fill_color / border_color …）与「外观样式」共用同一批控件，所以两处都算对。</para>
/// </summary>
public class PropertyPanelSectionPlacementTests
{
    private const string Geometry = "GeometryExpander";
    private const string Content = "BasicContentPanel";
    private const string Appearance = "AppearanceExpander";
    private const string BindingState = "BindingStateSection";
    private const string Interaction = "InteractionExpander";
    private const string ActionsAdvanced = "ActionsAdvancedSection";
    private const string DataDisplay = "DataDisplaySection";

    /// <summary>分区名 → 允许承载它的顶层容器（命中任意一个即算对）。</summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["geometry"] = new[] { Geometry },
        ["content"] = new[] { Content },
        ["appearance"] = new[] { Appearance },
        ["behavior"] = new[] { BindingState, Interaction },
        ["advanced"] = new[] { ActionsAdvanced },
        ["data"] = new[] { DataDisplay },
        ["figure"] = new[] { Appearance, "ExclusiveParamExpander" },
    };

    [Fact]
    public void EveryEditorRow_SitsInTheSectionItsRegistrationDeclares()
    {
        UiTestHost.Run(() =>
        {
            var screen = new HmiScreen { Id = "s0", Name = "S0", Width = 1024, Height = 600 };
            var project = new HmiProject();
            project.Screens.Add(screen);
            var panel = new PropertyPanelView();
            var window = new Window { Width = 900, Height = 900, Content = panel };
            window.Show();
            panel.SetProject(project);

            var problems = new List<string>();
            var checkedRows = 0;

            foreach (var (typeName, type, control) in Fixtures())
            {
                panel.BindControl(control);
                UiTestHost.Pump(2);

                // 按名字在**逻辑树**里找：图元 kind 级的行是代码里动态建出来的
                // （`FigureParamHost.Children`），它们不在 XAML 的 NameScope 里，
                // `FindControl` 找不到 —— 只能按 Name 属性扫一遍。
                var byName = panel.GetLogicalDescendants().OfType<Control>()
                    .Where(c => !string.IsNullOrEmpty(c.Name))
                    .GroupBy(c => c.Name!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                foreach (var reg in WidgetPropertyCoverage.ForType(typeName))
                {
                    if (reg.Editor == null) continue;
                    if (!byName.TryGetValue(reg.Editor, out var editor))
                    {
                        problems.Add($"{typeName}.{reg.Property}: 面板里没有 {reg.Editor}");
                        continue;
                    }

                    if (!Allowed.TryGetValue(reg.Section, out var allowed))
                    {
                        problems.Add($"{typeName}.{reg.Property}: 登记的分区名 \"{reg.Section}\" 不在已知五个区 + figure 里");
                        continue;
                    }

                    var containers = AncestorNames(editor);
                    if (containers.Count == 0)
                    {
                        problems.Add($"{typeName}.{reg.Property}: {reg.Editor} 不在任何分区容器里（行脱离了面板？）");
                        continue;
                    }

                    checkedRows++;
                    if (!allowed.Any(containers.Contains))
                        problems.Add($"{typeName}.{reg.Property}: 登记为 {reg.Section}（应在 {string.Join("|", allowed)}），"
                                     + $"实际在 {string.Join(" < ", containers)}");
                }
            }

            Assert.True(checkedRows > 500, $"只核到 {checkedRows} 行 —— 判据近乎空跑（夹具或取数坏了）");
            Assert.True(problems.Count == 0,
                "行的分区与登记表不一致：" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Take(40)));

            window.Close();
            UiTestHost.Pump();
        });
    }

    /// <summary>
    /// 编辑器所在的**分区容器链**（由内向外，只留本判据认识的容器名）。
    /// 走逻辑树：隐藏行不在可视树里。
    /// </summary>
    private static List<string> AncestorNames(Control editor)
    {
        var known = new HashSet<string>(Allowed.Values.SelectMany(v => v), StringComparer.Ordinal)
        {
            Geometry, Content, Appearance, BindingState, Interaction, ActionsAdvanced, DataDisplay,
        };

        var names = new List<string>();
        for (ILogical? node = editor; node != null; node = node.LogicalParent)
        {
            if (node is Control { Name: { Length: > 0 } name } && known.Contains(name))
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// 夹具：每种控件各一个（图元按 kind 各一个 —— kind 级行是动态生成的，必须真的绑上才有）。
    /// 字段值与 <c>CoverageMatrixTests.Make</c> 同口径：让每个编辑器都有非空值可显示。
    /// </summary>
    private static IEnumerable<(string Type, HmiControlType ControlType, HmiControl Control)> Fixtures()
    {
        foreach (var name in WidgetPropertyCoverage.WidgetTypes)
        {
            if (!WidgetPropertyCoverage.TryWidgetType(name, out var type)) continue;
            yield return (name, type, new HmiControl
            {
                Id = "c_" + name,
                Type = type,
                Name = "n_" + name,
                Text = "标签",
                X = 10, Y = 20, Width = 120, Height = 30,
                MinValue = 0, MaxValue = 100, Value = 50, StepValue = 10,
                BgColor = "#0EA5E9", IndicatorColor = "#22C55E", TextColor = "#111827",
                BorderColor = "#334155", BorderWidth = 2, Radius = 4, FontSize = 14, Opacity = 90,
                Options = "A\nB", RollerOptions = "A\nB", TabTitles = "页1,页2",
                PlaceholderText = "占位", Unit = "℃",
                HistTableGroup = "g", HistTableColumns = "time:时间",
                RecipeGroup = "r", RecipeColumns = "name:名称",
                TrendGroup = "t", CurveVariableIds = "v1",
                AlarmGroupFilter = "温度", AlarmColumns = "time,name", AlarmLevelFilter = "1~4",
                ShapeKind = "rect", KeyboardTarget = "c1",
                QrText = "x", MessageText = "m", EnumTextValue = "运行",
                StateImages = "1:img", FrameImages = "img1\nimg2",
                XyTagX = "v1", XyTagY = "v1", ChartSeries = "v1:标签",
                ValveType = "Ball", PipeDirection = "Horizontal", BarDirection = "Horizontal",
                CurveDirection = "LeftToRight", AsciiEncoding = "utf8", MessageLevel = "info",
                ChartKind = "bar", AlarmMode = "active", AlarmOrder = "newest_first",
                AlarmAckOn = "click", AlarmGrid = "all", AlarmScroll = "auto",
                GaugeLabelPos = "inside", InputCommit = "blur", StretchMode = "Fit",
                LabelLongMode = "Wrap", LabelFontStyle = "normal",
            });
        }

        foreach (var kind in FigurePropertyCatalog.Kinds)
        {
            yield return ("figure:" + kind, HmiControlType.Shape, new HmiControl
            {
                Id = "f_" + kind,
                Type = HmiControlType.Shape,
                ShapeKind = kind,
                Name = "fig_" + kind,
                X = 10, Y = 20, Width = 120, Height = 60,
                BgColor = "#0EA5E9", BorderColor = "#334155", BorderWidth = 2, Radius = 4,
            });
        }
    }
}
