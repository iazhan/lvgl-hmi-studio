using System.Text.Json;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 数据类控件"没有数据"时的兜底：曲线 / 图表 / X-Y 用 <c>{"expr":"0"}</c> 占位。
///
/// <para><b>为什么必须兜底</b>：运行时要 <c>curves</c> 至少 1 条、<c>series</c> 至少 1 项，
/// 缺了**拒绝整页**。表现是设计画布变空白（宿主起不来），用户只是想先画个框却毫无提示。
/// 给一条 0 线比白屏好排查，也比替用户挑一个真实变量安全（那会把工程悄悄绑到别的量上）。</para>
/// </summary>
public class PlaceholderChannelExportTests : IDisposable
{
    private readonly string _dir;

    public PlaceholderChannelExportTests() => _dir = TestTempDir.NewDir("placeholder-ch");

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    private JsonElement Widget(string type) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json")))
            .RootElement.GetProperty("widgets")
            .EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == type);

    [Fact]
    public void RealtimeCurve_WithoutChannels_ExportsZeroPlaceholderAndWarns()
    {
        var project = ExportFixture.Build();
        var curve = project.Screens[0].Controls.First(c => c.Type == HmiControlType.RealtimeCurve);
        curve.CurveVariableIds = "";

        var result = ExportFixture.Export(project, _dir);

        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        var curves = Widget("realtime_curve").GetProperty("props").GetProperty("curves");
        Assert.Equal(1, curves.GetArrayLength());
        Assert.Equal("0", curves[0].GetProperty("expr").GetString());
        Assert.Contains(result.Issues.Items, i => i.Code == "realtime_curve.curves");
    }

    [Fact]
    public void Chart_WithoutSeries_ExportsZeroPlaceholderAndWarns()
    {
        var project = ExportFixture.Build();
        var chart = project.Screens[0].Controls.FirstOrDefault(c => c.Type == HmiControlType.Chart);
        if (chart == null)
        {
            chart = new HmiControl
            {
                Id = "ch1", Name = "Chart1", Type = HmiControlType.Chart,
                X = 20, Y = 200, Width = 300, Height = 160,
                MinValue = 0, MaxValue = 100,
            };
            project.Screens[0].Controls.Add(chart);
        }
        chart.ChartSeries = "";

        var result = ExportFixture.Export(project, _dir);

        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        var series = Widget("chart").GetProperty("props").GetProperty("series");
        Assert.Equal(1, series.GetArrayLength());
        Assert.Equal("0", series[0].GetProperty("value").GetProperty("expr").GetString());
        Assert.Contains(result.Issues.Items, i => i.Code == "chart.series");
    }

    [Fact]
    public void XyCurve_WithoutTags_ExportsZeroPlaceholderAndWarns()
    {
        var project = ExportFixture.Build();
        var xy = project.Screens[0].Controls.FirstOrDefault(c => c.Type == HmiControlType.XyCurve);
        if (xy == null)
        {
            xy = new HmiControl
            {
                Id = "xy1", Name = "Xy1", Type = HmiControlType.XyCurve,
                X = 20, Y = 200, Width = 300, Height = 160,
                MinValue = 0, MaxValue = 100,
            };
            project.Screens[0].Controls.Add(xy);
        }
        xy.XyTagX = null;
        xy.XyTagY = null;

        var result = ExportFixture.Export(project, _dir);

        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        var props = Widget("xy_curve").GetProperty("props");
        Assert.Equal("0", props.GetProperty("x").GetProperty("expr").GetString());
        Assert.Equal("0", props.GetProperty("y").GetProperty("expr").GetString());
        Assert.Contains(result.Issues.Items, i => i.Code == "xy_curve.tags");
    }

    /// <summary>
    /// 面板上的「可见」勾掉之后，产物里必须是 <c>visible:false</c>（契约 common.visible）。
    /// 模型/导出器一直支持这个键，缺的是面板入口 —— 这条判据钉住"勾了真的写出去"。
    /// </summary>
    [Fact]
    public void HiddenControl_ExportsVisibleFalse()
    {
        var project = ExportFixture.Build();
        var label = project.Screens[0].Controls.First(c => c.Type == HmiControlType.Label);
        label.IsVisible = false;

        var result = ExportFixture.Export(project, _dir);

        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        var hidden = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json")))
            .RootElement.GetProperty("widgets")
            .EnumerateArray()
            .Where(w => w.GetProperty("props").TryGetProperty("visible", out var v) && !v.GetBoolean())
            .ToList();
        Assert.Single(hidden);
    }
}
