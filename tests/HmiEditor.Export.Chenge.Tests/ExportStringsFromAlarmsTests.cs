using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HmiEditor.Core.Models.Alarm;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 报警的 <c>text_id</c> / <c>text_clear_id</c> 必须真的进 <c>strings.json</c>。
///
/// <para><b>为什么是致命项</b>：运行时 <c>alarm_load</c> 找不到 <c>text_id</c> 对应的键时
/// <c>goto fail</c>，而 <c>runtime_boot</c> 把报警加载失败当致命 —— 现象是**整个工程起不来**，
/// 不是"报警显示 id"。导出器曾经先建 strings 再建 alarms（文本表由后者填充），
/// 于是用了文本 id 的工程导出来必然缺文件。</para>
/// </summary>
public class ExportStringsFromAlarmsTests : IDisposable
{
    private readonly string _dir;

    public ExportStringsFromAlarmsTests() => _dir = TestTempDir.NewDir("strings-alarms");

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    /// <summary>报警文本 id 要同时进 strings.json 与 manifest.features（i18n）。</summary>
    [Fact]
    public void AlarmTextId_ProducesStringsJson()
    {
        var project = ExportFixture.Build();
        var alarm = project.AlarmGroups[0].Alarms[0];
        alarm.TextId = "alarm.oven.hi";
        alarm.TextClearId = "alarm.oven.ok";

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var stringsPath = Path.Combine(_dir, "strings.json");
        Assert.True(File.Exists(stringsPath), "用了 text_id 却没生成 strings.json（运行时报警加载会失败）");

        var strings = JsonDocument.Parse(File.ReadAllText(stringsPath)).RootElement;
        var ids = strings.GetProperty("texts").EnumerateArray()
            .Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.Contains("alarm.oven.hi", ids);
        Assert.Contains("alarm.oven.ok", ids);

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "manifest.json"))).RootElement;
        Assert.Contains("i18n", manifest.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    /// <summary>
    /// 同一个导出器实例连续导出两个工程时，文本表不能串味（编辑器里"预览 → 正式导出"
    /// 用的就是同一个实例）。第二个工程没有 text_id ⇒ 不该有 strings.json。
    /// </summary>
    [Fact]
    public void ExporterReuse_DoesNotLeakTextsBetweenProjects()
    {
        var exporter = new ChengeExporter();

        var first = ExportFixture.Build();
        first.AlarmGroups[0].Alarms[0].TextId = "alarm.first";
        var dirA = Path.Combine(_dir, "a");
        exporter.Export(first, null, new ChengeExportOptions(), dirA);
        Assert.True(File.Exists(Path.Combine(dirA, "strings.json")));

        var second = ExportFixture.Build();   // 无 text_id
        var dirB = Path.Combine(_dir, "b");
        exporter.Export(second, null, new ChengeExportOptions(), dirB);
        Assert.False(File.Exists(Path.Combine(dirB, "strings.json")),
            "上一个工程的报警文本漏进了第二个工程");
    }

    /// <summary>带文本 id 的工程必须能在宿主运行时里起来（报警加载失败是整机失败）。</summary>
    [Fact]
    public void AlarmTextId_ProjectOpensInRuntime()
    {
        var project = ExportFixture.Build();
        project.AlarmGroups[0].Alarms[0].TextId = "alarm.oven.hi";

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var (ok, log) = ExportFixture.RunRuntime(_dir, Path.Combine(_dir, "strings.bmp"));
        if (log.StartsWith("skip:", StringComparison.Ordinal)) return;

        Assert.True(ok, $"用了 text_id 的工程没起来（报警加载失败？）\n{log}");
    }
}
