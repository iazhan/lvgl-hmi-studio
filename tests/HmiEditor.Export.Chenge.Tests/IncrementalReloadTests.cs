using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HmiEditor.Core.Models;
using HmiEditor.Core.Models.Alarm;
using Xunit;
using Xunit.Abstractions;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 设计画布"元数据变了"之后走的那条增量通道（宿主 <c>K</c>）：
/// 元数据包必须与整包同源、页面要么带上要么确实没变、告警不能少报。
/// </summary>
public class IncrementalReloadTests : IDisposable
{
    private readonly string _dir = TestTempDir.NewDir("chenge-incremental");
    private readonly ITestOutputHelper _out;

    public IncrementalReloadTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    private static readonly ChengeExportOptions Options = new();

    /// <summary>
    /// 20 页，其中只有 <see cref="BoundScreen"/> 那一页的 Bar 绑定了 <see cref="BoundVariable"/>。
    /// 每页底色不同，真机上切页是能看出来的。
    /// </summary>
    private const string BoundScreen = "s07";
    private const string BoundVariable = "v3";

    private static HmiProject BuildTwentyPages()
    {
        var project = new HmiProject
        {
            Name = "二十页",
            HardwareTargetId = "f1c100s-kinseal",
            ProjectVersion = "1.0.0",
        };

        for (var i = 0; i < 8; i++)
        {
            project.VariableManager.Variables.Add(new Variable
            {
                Id = "v" + i,
                Name = "变量" + i,
                DataType = "Int16",
                Access = "rw",
                SimulatedValue = (i * 10).ToString(),
            });
        }

        for (var p = 0; p < 20; p++)
        {
            var screen = new HmiScreen
            {
                Id = $"s{p:00}",
                Name = $"p{p:00}",
                Width = 800,
                Height = 480,
                BgColor = p % 2 == 0 ? "#101820" : "#202A34",
            };
            screen.Controls.Add(new HmiControl
            {
                Id = $"lbl{p:00}",
                Type = HmiControlType.Label,
                Name = $"Lbl{p:00}",
                X = 20, Y = 20, Width = 400, Height = 40,
                Text = $"页面 {p:00}",
                FontSize = 24,
                TextColor = "#FFFFFF",
            });
            if ($"s{p:00}" == BoundScreen)
            {
                screen.Controls.Add(new HmiControl
                {
                    Id = "bar1",
                    Type = HmiControlType.Bar,
                    Name = "Bar1",
                    X = 40, Y = 100, Width = 400, Height = 30,
                    MinValue = 0, MaxValue = 100,
                    VariableBindings =
                    {
                        new VariableBindingData
                        {
                            PropertyName = "Value", VariableId = BoundVariable,
                            Direction = BindingDirection.ReadOnly,
                        },
                    },
                });
            }
            // 每页再放 40 个静态控件：建页代价要贴近真实工程，否则"省下的建页"量不出来
            for (var c = 0; c < 40; c++)
            {
                screen.Controls.Add(new HmiControl
                {
                    Id = $"c{p:00}_{c:00}",
                    Type = c % 2 == 0 ? HmiControlType.Label : HmiControlType.Shape,
                    Name = $"C{p:00}_{c:00}",
                    X = 20 + c % 8 * 96, Y = 60 + c / 8 * 60, Width = 90, Height = 40,
                    Text = $"T{c}",
                    FontSize = c % 3 == 0 ? 18 : 22,
                    TextColor = "#E0E0E0",
                    ShapeKind = "rect",
                    BgColor = "#30465A",
                });
            }
            project.Screens.Add(screen);
        }
        return project;
    }

    private static Dictionary<string, byte[]> Whole(HmiProject project, out ChengeExportResult result)
        => new ChengeExporter().ExportToMemory(project, null, Options, null, out result);

    /// <summary>哪些页的 JSON 与 <paramref name="before"/> 不同（正确性的唯一判据）。</summary>
    private static HashSet<string> ChangedPages(IReadOnlyDictionary<string, byte[]> before,
        IReadOnlyDictionary<string, byte[]> after)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, bytes) in after)
        {
            if (!relative.StartsWith("pages/", StringComparison.Ordinal)) continue;
            if (!before.TryGetValue(relative, out var old) || !old.AsSpan().SequenceEqual(bytes))
                changed.Add(relative["pages/".Length..^".json".Length]);
        }
        return changed;
    }

    private static long Bytes(IReadOnlyDictionary<string, byte[]> files)
        => files.Sum(kv => (long)kv.Value.Length);

    [Fact]
    public void MetaOnly_MatchesWholeExport_FileByFile()
    {
        var project = BuildTwentyPages();
        var exporter = new ChengeExporter();
        var full = exporter.ExportToMemory(project, null, Options, null, out var fullResult);
        Assert.True(fullResult.Success, fullResult.Summary);

        var meta = new ChengeExporter().ExportMetaOnly(project, null, Options, null, fullResult.PageStats,
            out var metaResult);
        Assert.True(metaResult.Success, metaResult.Summary);

        Assert.DoesNotContain(meta.Keys, k => k.StartsWith("pages/", StringComparison.Ordinal));
        Assert.DoesNotContain(meta.Keys, k => k.StartsWith("assets/", StringComparison.Ordinal));
        foreach (var (relative, bytes) in meta)
        {
            Assert.True(full.TryGetValue(relative, out var expected), $"整包里没有 {relative}");
            Assert.True(expected!.AsSpan().SequenceEqual(bytes), $"{relative} 与整包不一致");
        }
        // 页面派生声明与 tag 指纹必须一致，否则增量包的 manifest 会与宿主里那份工程对不上
        Assert.Equal(fullResult.TagFingerprints, metaResult.TagFingerprints);
        Assert.Equal(fullResult.PageStats.Features, metaResult.PageStats.Features);
        Assert.Equal(fullResult.PageStats.FontSizes, metaResult.PageStats.FontSizes);
        Assert.Equal(fullResult.PageIds.OrderBy(kv => kv.Key), metaResult.PageIds.OrderBy(kv => kv.Key));
        Assert.Equal(20, metaResult.PageCount);
    }

    [Fact]
    public void MetaOnlyPlusEachPage_ReportsSameWarningsAsWholeExport()
    {
        // 悬空 ParentId：建页时才会报 page.orphan_parent —— 换成 meta-only 后这条不能消失
        var project = BuildTwentyPages();
        project.Screens[3].Controls.Add(new HmiControl
        {
            Id = "orphan", Type = HmiControlType.Label, Name = "Orphan",
            X = 10, Y = 200, Width = 100, Height = 30, Text = "orphan", ParentId = "nosuchcontainer",
        });

        var full = new ChengeExporter().ExportToMemory(project, null, Options, null, out var fullResult);
        Assert.True(fullResult.Success, fullResult.Summary);
        var wholeWarnings = Warnings(fullResult);
        Assert.Contains(wholeWarnings, w => w.StartsWith("page.orphan_parent", StringComparison.Ordinal));

        var exporter = new ChengeExporter();
        var meta = exporter.ExportMetaOnly(project, null, Options, null, fullResult.PageStats, out var metaResult);
        Assert.True(metaResult.Success);

        var union = new HashSet<string>(Warnings(metaResult), StringComparer.Ordinal);
        foreach (var screen in project.Screens)
        {
            var json = exporter.ExportPageOnly(project, null, Options, null, screen.Id, out var pageResult, out _);
            Assert.NotNull(json);
            union.UnionWith(Warnings(pageResult));
        }

        Assert.Equal(wholeWarnings.OrderBy(x => x, StringComparer.Ordinal),
            union.OrderBy(x => x, StringComparer.Ordinal));
        Assert.NotEmpty(meta);
    }

    private static HashSet<string> Warnings(ChengeExportResult result)
        => result.Issues.Items
            .Where(i => i.Severity == ChengeIssueSeverity.Warning)
            .Select(i => $"{i.Code}@{i.Location}")
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void RenameVariable_IncrementalPackIsMuchSmallerThanWholePack()
    {
        var project = BuildTwentyPages();
        var fullMs = long.MaxValue;
        Dictionary<string, byte[]> full = new(StringComparer.Ordinal);
        ChengeExportResult fullResult = null!;
        // 取 3 次最小值：单次耗时在 1~3ms 量级，机器抖动能盖过差异
        for (var round = 0; round < 3; round++)
        {
            var watch = Stopwatch.StartNew();
            full = Whole(project, out fullResult);
            watch.Stop();
            fullMs = Math.Min(fullMs, watch.ElapsedMilliseconds);
        }
        Assert.True(fullResult.Success, fullResult.Summary);

        // 改一个变量名：它的 tag 指纹变了，但没有任何页引用它
        var refs = ChengeIncrementalExport.PageTagRefsOf(full);
        project.VariableManager.Variables[0].Name = "变量0_改名";

        var incMs = long.MaxValue;
        DesignReloadPlan plan = null!;
        for (var round = 0; round < 3; round++)
        {
            var watch = Stopwatch.StartNew();
            plan = DesignIncrementalReload.Build(project, null, null, Options, "s00",
                fullResult.PageStats, fullResult.TagFingerprints, refs);
            watch.Stop();
            incMs = Math.Min(incMs, watch.ElapsedMilliseconds);
        }

        Assert.False(plan.Failed);
        Assert.False(plan.WholeProject);
        Assert.Equal(new[] { "p00" }, plan.IncludedPageIds.ToArray());

        var fullBytes = Bytes(full);
        var incBytes = Bytes(plan.Payload);
        _out.WriteLine($"整包 {full.Count} 个文件 {fullBytes} 字节 / 导出 {fullMs}ms；" +
                       $"增量 {plan.Payload.Count} 个文件 {incBytes} 字节 / 导出 {incMs}ms");
        Assert.True(incBytes * 3 < fullBytes, $"增量包应显著小于整包：{incBytes} vs {fullBytes}");
    }

    [Fact]
    public void DeleteBoundVariable_BringsEveryPageWhoseJsonChanged()
    {
        var project = BuildTwentyPages();
        var before = Whole(project, out var beforeResult);
        Assert.True(beforeResult.Success, beforeResult.Summary);
        var refs = ChengeIncrementalExport.PageTagRefsOf(before);

        // 删掉被绑定的那个变量（编辑器删除变量时会一并清掉引用它的绑定）：
        // 后面的 tag 序号整体前移，所有引用过的页都会过期
        var victim = project.VariableManager.Variables.Single(v => v.Id == BoundVariable);
        project.VariableManager.Variables.Remove(victim);
        foreach (var control in project.Screens.SelectMany(s => s.Controls))
            control.VariableBindings.Clear();

        var plan = DesignIncrementalReload.Build(project, null, null, Options, "s00",
            beforeResult.PageStats, beforeResult.TagFingerprints, refs);
        if (plan.Failed)
            throw new Xunit.Sdk.XunitException("导出失败：" + plan.Result.Summary + " / " +
                string.Join(" | ", plan.Result.Issues.Items.Select(i => i.Code + "@" + i.Location + ":" + i.Message)));
        if (plan.WholeProject) throw new Xunit.Sdk.XunitException("退化原因：" + plan.Reason);

        var after = Whole(project, out var afterResult);
        Assert.True(afterResult.Success, afterResult.Summary);
        var changed = ChangedPages(before, after);
        Assert.Contains("p07", changed);

        // 判据：内容变了的页一个都不能漏（漏了 = 宿主里留旧页，切页读到别的变量）
        foreach (var pageId in changed)
            Assert.Contains(pageId, plan.IncludedPageIds);
        Assert.Contains("p07", plan.IncludedPageIds);
        _out.WriteLine($"变了的页 {string.Join(",", changed.OrderBy(x => x))}；" +
                       $"包内页 {string.Join(",", plan.IncludedPageIds)}");
    }

    [Fact]
    public void ReorderVariables_BringsEveryPageWhoseJsonChanged()
    {
        var project = BuildTwentyPages();
        // 再造一页绑定 v0：换序后两页引用的序号都会错位
        project.Screens[15].Controls.Add(new HmiControl
        {
            Id = "bar2", Type = HmiControlType.Bar, Name = "Bar2",
            X = 40, Y = 200, Width = 300, Height = 30, MinValue = 0, MaxValue = 100,
            VariableBindings =
            {
                new VariableBindingData { PropertyName = "Value", VariableId = "v0", Direction = BindingDirection.ReadOnly },
            },
        });

        var before = Whole(project, out var beforeResult);
        var refs = ChengeIncrementalExport.PageTagRefsOf(before);

        var list = project.VariableManager.Variables;
        var first = list[0];
        list.RemoveAt(0);
        list.Insert(1, first);

        var plan = DesignIncrementalReload.Build(project, null, null, Options, "s00",
            beforeResult.PageStats, beforeResult.TagFingerprints, refs);
        Assert.False(plan.Failed);
        Assert.False(plan.WholeProject);

        var after = Whole(project, out _);
        var changed = ChangedPages(before, after);
        foreach (var pageId in changed)
            Assert.Contains(pageId, plan.IncludedPageIds);
        // p15 绑的是被换序的那个变量；p07 绑的 tag 序号没动，所以不该被带上
        Assert.Contains("p15", plan.IncludedPageIds);
        Assert.DoesNotContain("p07", plan.IncludedPageIds);
        _out.WriteLine($"换序后变了的页 {string.Join(",", changed.OrderBy(x => x))}；" +
                       $"包内页 {string.Join(",", plan.IncludedPageIds)}");
    }

    [Fact]
    public void DeletingAllAlarms_SendsEmptyAlarmDocument()
    {
        var project = BuildTwentyPages();
        var group = new AlarmGroup { Name = "温度" };
        group.Alarms.Add(new AlarmConfig
        {
            Name = "过高", Severity = AlarmSeverity.Critical, RequiresAck = true,
            Condition = new AlarmCondition
            {
                VariableId = BoundVariable, Type = AlarmConditionType.HighLimit, Threshold = 90,
            },
        });
        project.AlarmGroups.Add(group);

        var before = Whole(project, out var beforeResult);
        Assert.True(before.ContainsKey("alarms.json"));
        var refs = ChengeIncrementalExport.PageTagRefsOf(before);

        project.AlarmGroups.Clear();

        var plan = DesignIncrementalReload.Build(project, null, null, Options, "s00",
            beforeResult.PageStats, beforeResult.TagFingerprints, refs);
        Assert.False(plan.Failed);

        // 报警被删空：必须显式发空 alarms.json，否则宿主手里那份旧报警照样会响
        Assert.True(plan.Payload.TryGetValue("alarms.json", out var alarms));
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(alarms!));
        Assert.Empty(doc.RootElement.GetProperty("alarms").EnumerateArray());
        Assert.Contains("strings.json", plan.Payload.Keys);
        Assert.Contains("savecfg.json", plan.Payload.Keys);
    }

    [Fact]
    public void UnknownPageRefs_FallsBackToWholeProject()
    {
        var project = BuildTwentyPages();
        var before = Whole(project, out var beforeResult);

        // 没有任何引用记录 = 判不出来，必须保守整包
        var plan = DesignIncrementalReload.Build(project, null, null, Options, "s00",
            beforeResult.PageStats, beforeResult.TagFingerprints,
            new Dictionary<string, HashSet<int>>(StringComparer.Ordinal));

        Assert.True(plan.WholeProject);
        Assert.Equal(before.Count, plan.Payload.Count);
        Assert.NotNull(plan.CurrentPageJson);
    }
}
