using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HmiEditor.App.Views;
using HmiEditor.Core.Coverage;
using HmiEditor.Core.Models;
using HmiEditor.Export.Chenge;
using Xunit;
using Xunit.Abstractions;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 契约 × 类型 × 属性的覆盖矩阵（方案 52 的验收判据）。
///
/// <para>三条判据：① 契约里每个属性在每个类型上都**有且只有一条**结论登记；② 登记为闭环的行
/// 在该类型下真的可见、不可编辑的行不出现；③ 改动那个编辑器后，**登记的那个模型字段**发生变化
/// （不是"某个字段变了"，那会让脏写混过去）。导出侧的键与值由
/// <c>CoverageMatrixExportTests</c> 管，两边读同一张登记表。</para>
/// </summary>
public class CoverageMatrixTests
{
    private readonly ITestOutputHelper _out;

    public CoverageMatrixTests(ITestOutputHelper output) => _out = output;

    // ── ① 登记 completeness ──

    [Fact]
    public void EveryContractProperty_IsRegisteredExactlyOnce()
    {
        var contract = LoadContract();
        var missing = new List<string>();
        var duplicated = new List<string>();

        foreach (var (type, props) in contract)
        {
            var registered = WidgetPropertyCoverage.ForType(type).Select(c => c.Property).ToList();
            foreach (var prop in props)
                if (!registered.Contains(prop)) missing.Add($"{type}.{prop}");
            duplicated.AddRange(registered.GroupBy(p => p).Where(g => g.Count() > 1)
                .Select(g => $"{type}.{g.Key}×{g.Count()}"));
            foreach (var extra in registered.Except(props))
                missing.Add($"{type}.{extra}（登记了契约没有的属性）");
        }

        Assert.True(missing.Count == 0, "契约属性没有登记结论：" + string.Join(", ", missing.Take(40)));
        Assert.True(duplicated.Count == 0, "同一属性登记了多次：" + string.Join(", ", duplicated.Take(20)));
    }

    [Fact]
    public void EveryRegistration_HasADecisionAndEvidence()
    {
        var bad = WidgetPropertyCoverage.All
            .Where(c => string.IsNullOrWhiteSpace(c.Evidence))
            .Select(c => $"{c.Type}.{c.Property}")
            .ToList();
        Assert.True(bad.Count == 0, "没有证据/理由的登记：" + string.Join(", ", bad.Take(30)));
    }

    [Fact]
    public void ClosedRegistrations_HaveEditorModelAndExport()
    {
        var bad = WidgetPropertyCoverage.All
            .Where(c => c.IsClosed && (c.Editor == null || c.Model == null || c.ExportKey == null))
            .Select(c => $"{c.Type}.{c.Property}(editor={c.Editor},model={c.Model},export={c.ExportKey})")
            .ToList();
        Assert.True(bad.Count == 0, "闭环却缺层的登记：" + string.Join(", ", bad.Take(30)));
    }

    [Fact]
    public void ContractCoverageNumbers_AreReported()
    {
        var byDecision = WidgetPropertyCoverage.All
            .GroupBy(c => c.Decision)
            .ToDictionary(g => g.Key, g => g.Count());
        _out.WriteLine("登记总数 " + WidgetPropertyCoverage.All.Count);
        foreach (var (decision, count) in byDecision.OrderBy(k => k.Key))
            _out.WriteLine($"  {decision}: {count}");

        var types = WidgetPropertyCoverage.All.Select(c => c.Type).Distinct().ToList();
        _out.WriteLine("类型数 " + types.Count);
        Assert.Equal(74, types.Count);   // 34 控件 + 40 图元
    }

    // ── ② 面板显隐 ──

    [Fact]
    public void PanelVisibility_MatchesTheRegistration()
    {
        UiTestHost.Run(() =>
        {
            var dpi = new HmiScreen { Id = "s0", Name = "S0", Width = 1024, Height = 600 };
            var project = new HmiProject();
            project.Screens.Add(dpi);
            var panel = new PropertyPanelView();
            var window = new Window { Width = 900, Height = 900, Content = panel };
            window.Show();
            panel.SetProject(project);

            var problems = new List<string>();
            foreach (var (name, type) in WidgetTypes())
            {
                var control = Make(type);
                panel.BindControl(control);
                UiTestHost.Pump(2);

                foreach (var reg in WidgetPropertyCoverage.ForType(name))
                {
                    if (reg.Editor == null) continue;
                    var editor = panel.FindControl<Control>(reg.Editor);
                    if (editor == null)
                    {
                        problems.Add($"{type}.{reg.Property}: 找不到编辑器 {reg.Editor}");
                        continue;
                    }
                    var row = RowOf(editor);
                    var visible = row?.IsVisible ?? editor.IsVisible;
                    if (visible != reg.IsClosed)
                        problems.Add($"{type}.{reg.Property}: 行可见={visible}，登记={reg.Decision}");
                }
            }

            Assert.True(problems.Count == 0,
                "面板显隐与登记不一致：" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(40)));
            window.Close();
            UiTestHost.Pump();
        });
    }

    // ── ③ 编辑后模型变化（只认登记的那个字段） ──

    [Fact]
    public void ClosedEditors_MutateTheRegisteredModelField()
    {
        UiTestHost.Run(() =>
        {
            var dpi = new HmiScreen { Id = "s0", Name = "S0", Width = 1024, Height = 600 };
            var project = new HmiProject();
            project.Screens.Add(dpi);
            var panel = new PropertyPanelView();
            var window = new Window { Width = 900, Height = 900, Content = panel };
            window.Show();
            panel.SetProject(project);

            var problems = new List<string>();
            var skipped = 0;
            foreach (var (name, type) in WidgetTypes())
            {
                var control = Make(type);
                panel.BindControl(control);
                UiTestHost.Pump(2);

                foreach (var reg in WidgetPropertyCoverage.ForType(name).Where(r => r.IsClosed))
                {
                    var editor = panel.FindControl<Control>(reg.Editor!);
                    if (editor == null)
                    {
                        problems.Add($"{type}.{reg.Property}: 找不到编辑器 {reg.Editor}");
                        continue;
                    }

                    var prop = typeof(HmiControl).GetProperty(reg.Model!, BindingFlags.Public | BindingFlags.Instance);
                    if (prop == null)
                    {
                        problems.Add($"{type}.{reg.Property}: 模型没有字段 {reg.Model}");
                        continue;
                    }

                    if (reg.Model is "Points" or "VisualStates" or "Children" or "Cells")
                    {
                        skipped++;
                        continue;   // 复合结构：导出侧与各自的专项用例覆盖
                    }

                    // 资产类下拉：选项由资产库异步填充，测试宿主没有资产提供者，选不动
                    if (reg.Editor is "ImageAssetCombo" or "SvgAssetCombo" or "CboVsImage")
                    {
                        skipped++;
                        continue;
                    }

                    var before = prop.GetValue(control);
                    var changed = false;
                    foreach (var attempt in Attempts(editor))
                    {
                        attempt();
                        UiTestHost.Pump();
                        if (!Equals(before, prop.GetValue(control))) { changed = true; break; }
                    }
                    if (!changed)
                        problems.Add($"{type}.{reg.Property}: 改 {reg.Editor} 后 {reg.Model} 没变（{before}）");
                }
            }

            _out.WriteLine($"跳过（复合结构/驱动不动）：{skipped}");
            Assert.True(problems.Count == 0,
                "编辑器没有写到登记的字段：" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(40)));
            window.Close();
            UiTestHost.Pump();
        });
    }

    // ── 工具 ──

    /// <summary>契约里的 (类型, 属性集合)；类型名与登记表一致（widgets 用原名，figures 前缀 figure:）。</summary>
    private static IEnumerable<(string Type, List<string> Props)> LoadContract()
    {
        var root = ChengeSchema.Load().Root;
        var common = root.GetProperty("common").EnumerateObject().Select(p => p.Name).ToList();
        foreach (var widget in root.GetProperty("widgets").EnumerateObject())
            yield return (widget.Name, widget.Value.GetProperty("props").EnumerateObject().Select(p => p.Name)
                .Concat(common).ToList());
        foreach (var figure in root.GetProperty("figures").EnumerateObject())
            yield return ("figure:" + figure.Name,
                figure.Value.GetProperty("props").EnumerateObject().Select(p => p.Name)
                    .Concat(root.GetProperty("figure_common").EnumerateObject().Select(p => p.Name))
                    .Concat(common).ToList());
    }

    private static IEnumerable<(string Name, HmiControlType Type)> WidgetTypes() =>
        WidgetPropertyCoverage.WidgetTypes
            .Select(n => (Name: n, Ok: WidgetPropertyCoverage.TryWidgetType(n, out var t), Type: t))
            .Where(x => x.Ok)
            .Select(x => (x.Name, x.Type));

    private static HmiControl Make(HmiControlType type) => new()
    {
        Id = "c1",
        Type = type,
        Name = "n1",
        Text = "标签",
        X = 10, Y = 20, Width = 120, Height = 30,
        MinValue = 0, MaxValue = 100, Value = 50,
        StepValue = 10,
        BgColor = "#0EA5E9",
        IndicatorColor = "#22C55E",
        TextColor = "#111827",
        BorderColor = "#334155",
        BorderWidth = 2,
        Radius = 4,
        FontSize = 14,
        Opacity = 90,
        Rotation = 0,
        Options = "A\nB",
        RollerOptions = "A\nB",
        TabTitles = "页1,页2",
        PlaceholderText = "占位",
        Unit = "℃",
        HistTableGroup = "g",
        HistTableColumns = "time:时间",
        RecipeGroup = "r",
        RecipeColumns = "name:名称",
        TrendGroup = "t",
        CurveVariableIds = "v1",
        AlarmGroupFilter = "温度",
        AlarmColumns = "time,name",
        AlarmLevelFilter = "1~4",
        ShapeKind = "rect",
        KeyboardTarget = "c1",
        QrText = "x",
        MessageText = "m",
        EnumTextValue = "运行",
        StateImages = "1:img",
        FrameImages = "img1\nimg2",
        XyTagX = "v1",
        XyTagY = "v1",
        ChartSeries = "v1:标签",
        ValveType = "Ball",
        PipeDirection = "Horizontal",
        BarDirection = "Horizontal",
        CurveDirection = "LeftToRight",
        AsciiEncoding = "utf8",
        MessageLevel = "info",
        ChartKind = "bar",
        AlarmMode = "active",
        AlarmOrder = "newest_first",
        AlarmAckOn = "click",
        AlarmGrid = "all",
        AlarmScroll = "auto",
        GaugeLabelPos = "inside",
        InputCommit = "blur",
        StretchMode = "Fit",
        LabelLongMode = "Wrap",
        LabelFontStyle = "normal",
    };

    private static Control? RowOf(Control editor)
    {
        for (var v = editor.GetVisualParent(); v != null; v = v.GetVisualParent())
        {
            if (v is Grid g && g.Name != null && g.Name.StartsWith("Row", StringComparison.Ordinal))
                return g;
        }
        return null;
    }

    /// <summary>
    /// 依次尝试把编辑器拨到不同的值（颜色框会拒非法色值，文本/数字各给两支）。
    /// 程序化写 <c>Text</c> 不触发 <c>TextChanged</c>，必须补发事件 —— 那一跳才是生产路径。
    /// </summary>
    private static IEnumerable<Action> Attempts(Control editor)
    {
        switch (editor)
        {
            case FANumberBox nb:
                yield return () => nb.Value = nb.Value + 3;
                yield return () => nb.Value = nb.Value - 7;
                yield break;
            case ComboBox cb when cb.ItemCount >= 2:
                yield return () => cb.SelectedIndex = (cb.SelectedIndex + 1) % cb.ItemCount;
                yield return () => cb.SelectedIndex = (cb.SelectedIndex + 1) % cb.ItemCount;
                yield break;
            case CheckBox chk:
                yield return () => chk.IsChecked = !(chk.IsChecked ?? false);
                yield break;
            case TextBox tb:
                foreach (var value in new[] { "覆盖率探针", "#123456", "0,0\n20,20", "1,1" })
                {
                    yield return () =>
                    {
                        tb.Text = value;
                        tb.RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent));
                    };
                }
                yield break;
            default:
                yield break;
        }
    }
}
