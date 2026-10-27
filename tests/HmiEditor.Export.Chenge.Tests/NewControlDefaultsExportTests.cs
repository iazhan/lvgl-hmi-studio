using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 「新建控件、什么都不改」导出成什么 —— 逐类型钉住
/// <see cref="NewControlDefaults"/> 的结果。
///
/// <para><b>为什么值得一条独立判据</b>：这些值全部会**显式写进产物**，所以"面板显示什么、
/// 板子就是什么"；一旦某个模板值落在契约缺省之外、而面板又没有对应行，用户在编辑器里
/// 根本看不到它（历史表恒 10 行、输入框隐形上限 100、读数恒带 2 位小数都属于这一类）。
/// 每条用例的注释写清这个值归哪一侧（模板值 / 契约缺省），对应
/// <c>docs/chenge-rt/50</c> §7 的三方对照表。</para>
///
/// <para>判据只读产物 JSON，不需要宿主运行时：模板值一旦漂移，这里立刻红。</para>
/// </summary>
public class NewControlDefaultsExportTests : IDisposable
{
    private readonly string _dir;

    public NewControlDefaultsExportTests() => _dir = TestTempDir.NewDir("new-defaults");
    public void Dispose() => TestTempDir.DeleteDir(_dir);

    /// <summary>
    /// 输入框（数值型）：量程不能沿用滑块那对 0..100（会让"新拖的输入框输不进 500"），
    /// 限长按契约 12 写出来（0="不限"在运行时会被键盘钳到 24）。
    /// </summary>
    [Fact]
    public void TextArea_NewControl_HasContractRangeAndMaxLen()
    {
        var props = ExportFresh(HmiControlType.TextArea, BindTo("v_set"));

        Assert.Equal(12, props.GetProperty("max_len").GetInt32());
        Assert.Equal(1000000, props.GetProperty("max").GetInt32());
        Assert.Equal(0, props.GetProperty("min").GetInt32());
        // 小数位与契约 input.decimals 一致（缺省 0），一次改到五类控件
        Assert.Equal(0, props.GetProperty("decimals").GetInt32());
    }

    /// <summary>
    /// 输入框（文本型）：绑字符串变量时导出 <c>mode=text</c>，**不写量程与小数位** ——
    /// 输入 "ABC" 与 0..1000000 的数值范围无关，写了只会让人以为受限制。
    /// </summary>
    [Fact]
    public void TextArea_TextMode_OmitsRangeAndDecimals()
    {
        var props = ExportFresh(HmiControlType.TextArea, c =>
        {
            c.VariableBindings.Add(new VariableBindingData
            {
                PropertyName = "Text", VariableId = "v_str", Direction = BindingDirection.ReadWrite,
            });
        });

        Assert.Equal("text", props.GetProperty("mode").GetString());
        Assert.False(props.TryGetProperty("min", out _), "文本输入不该带量程下限");
        Assert.False(props.TryGetProperty("max", out _), "文本输入不该带量程上限");
        Assert.False(props.TryGetProperty("decimals", out _), "文本输入不该带小数位");
        Assert.Equal(12, props.GetProperty("max_len").GetInt32());
    }

    /// <summary>读数：字号 20（契约 value.font_size，l 型正文是 16）、小数位 0（契约 value.decimals）。</summary>
    [Fact]
    public void Value_NewControl_HasContractFontAndDecimals()
    {
        var props = ExportFresh(HmiControlType.Value);

        Assert.Equal(20, props.GetProperty("font_size").GetInt32());
        Assert.Equal(0, props.GetProperty("decimals").GetInt32());
    }

    /// <summary>
    /// 小数位一个模型字段供五类控件共用（value/input/gauge/chart/knob），模板值 0 必须
    /// 五处都落到契约缺省：gauge.decimals / knob.decimals / chart.y_decimals 都是 0。
    /// </summary>
    [Fact]
    public void Decimals_AreZero_WhereTheModelFieldIsShared()
    {
        Assert.Equal(0, ExportFresh(HmiControlType.Gauge).GetProperty("decimals").GetInt32());
        Assert.Equal(0, ExportFresh(HmiControlType.Knob).GetProperty("decimals").GetInt32());
        Assert.Equal(0, ExportFresh(HmiControlType.Chart, c => c.ChartSeries = "v_set:设定")
            .GetProperty("y_decimals").GetInt32());
    }

    /// <summary>
    /// 仪表：弧宽 0 = "不写、吃运行时缺省 8"（契约已把 0=自动 这个没人实现的承诺去掉），
    /// 中心圆半径显式写 6 —— 契约 gauge.center_radius=6，运行时旧值 5。
    /// </summary>
    [Fact]
    public void Gauge_NewControl_WritesCenterRadiusAndOmitsArcWidth()
    {
        var props = ExportFresh(HmiControlType.Gauge);

        Assert.False(props.TryGetProperty("arc_width", out _),
            "弧宽 0 = 不写：面板显示 0 时让运行时用它自己的 8");
        Assert.Equal(6, props.GetProperty("center_radius").GetInt32());
    }

    /// <summary>历史表：行数上限取 500（契约 history_table.max_rows），不再复用报警列表的 10。</summary>
    [Fact]
    public void HistoryTable_NewControl_ShowsContractRowCount()
    {
        var props = ExportFresh(HmiControlType.HistoryTable, c =>
        {
            c.HistTableColumns = "time:时间\nv_set:设定";
            c.HistTableGroup = "temp";
        });

        Assert.Equal(500, props.GetProperty("max_rows").GetInt32());
    }

    /// <summary>
    /// 开关：新建不给标题文字。它的 Text 会导出成 <c>text_on</c>，而运行时把非空的
    /// <c>text_on</c> 画在**控件框右边**（widget_action.c 的 build_switch）—— 新建出来就
    /// 在旁边挂一个"开关1"纯属噪声，还会压到相邻控件。
    /// </summary>
    [Fact]
    public void Switch_NewControl_HasNoCaption()
    {
        var control = Fresh(HmiControlType.Switch);
        Assert.Null(control.Text);

        var props = ExportFresh(HmiControlType.Switch);
        Assert.Equal("", props.GetProperty("text_on").GetString());
    }

    /// <summary>
    /// 开关的通断文字是两条独立的键：Text → <c>text_on</c>、SwitchTextOff → <c>text_off</c>。
    /// 此前 text_off 在导出器里被硬写成 ""，面板上也就没有那一行。
    /// </summary>
    [Fact]
    public void Switch_OnAndOffText_AreSeparateKeys()
    {
        var props = ExportFresh(HmiControlType.Switch, c =>
        {
            c.Text = "运行";
            c.SwitchTextOff = "停止";
        });

        Assert.Equal("运行", props.GetProperty("text_on").GetString());
        Assert.Equal("停止", props.GetProperty("text_off").GetString());
    }

    /// <summary>
    /// 表格：3×3（契约 table.rows/cols），没有表头文字就不开表头（契约 table.header 缺省
    /// false，与运行时的缺键行为一致）。
    /// </summary>
    [Fact]
    public void Table_NewControl_IsThreeByThreeWithoutHeader()
    {
        var props = ExportFresh(HmiControlType.Table);

        Assert.Equal(3, props.GetProperty("rows").GetInt32());
        Assert.Equal(3, props.GetProperty("cols").GetInt32());
        Assert.False(props.TryGetProperty("header", out var h) && h.GetBoolean());
    }

    /// <summary>
    /// 实时曲线窗口：契约缺省 60s（1 分钟）。30 分钟 × 1s 采样是 1800 个点，运行时只留
    /// 300 点，X 轴还按"点数 × 采样"标 —— 面板写 30 分钟、板上只画得出 5 分钟。
    /// </summary>
    [Fact]
    public void RealtimeCurve_NewControl_UsesContractWindow()
    {
        var props = ExportFresh(HmiControlType.RealtimeCurve, c => c.CurveVariableIds = "v_temp");

        Assert.Equal(60, props.GetProperty("span_s").GetInt32());
    }

    /// <summary>
    /// 标签字号 14 是**编辑器的模板值**，与契约缺省 16 有意不同：主题基准字号是 14
    /// （表格/曲线/报警列表都用它），而这个值面板上看得见、改得动。这条用例把它钉住，
    /// 免得将来"向契约看齐"时悄悄把所有新标签放大一号。
    /// </summary>
    [Fact]
    public void Label_NewControl_KeepsEditorTemplateFontSize()
    {
        Assert.Equal(14, ExportFresh(HmiControlType.Label).GetProperty("font_size").GetInt32());
    }

    // ── 端到端：新建控件必须能在**运行时**里真的建出页来 ──────────

    /// <summary>
    /// 新建表格（编辑器默认值）交给运行时，页面必须**建得出来**并画出东西。
    /// 设计画布白屏的根源就在这里：导出 JSON 看着正常，运行时拒了整页 ⇒ 宿主给
    /// 一张空画面 ⇒ 画布一片白，而实时预览走另一条路看不出来。
    /// </summary>
    [Fact]
    public void Table_NewControl_RendersInRuntime()
    {
        var (shot, log) = ShootFresh(HmiControlType.Table, null, 300, 200);

        Assert.True(File.Exists(shot), $"运行时没出截图（建页失败？）\n{log}");
        Assert.DoesNotContain("加载失败", log);
    }

    /// <summary>新建输入框绑**字符串**变量：文本键盘那一路，整页同样必须建得出来。</summary>
    [Fact]
    public void TextArea_StringBind_RendersInRuntime()
    {
        var (shot, log) = ShootFresh(HmiControlType.TextArea, BindTo("v_str"), 240, 44);

        Assert.True(File.Exists(shot), $"运行时没出截图（建页失败？）\n{log}");
        Assert.DoesNotContain("加载失败", log);
    }

    /// <summary>导出一页只含一个新建控件、跑一次宿主运行时截图，返回 (截图, 运行时日志)。</summary>
    private (string Shot, string Log) ShootFresh(HmiControlType type, Action<HmiControl>? tune,
        int width, int height)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR") ?? "";
        var binary = runtimeDir.Length == 0 ? "" : Path.Combine(runtimeDir, "chenge-runtime");
        if (!File.Exists(binary)) return ("", "skip: no CHENGE_RUNTIME_DIR");

        var project = ExportFixture.Build();
        foreach (var c in project.Screens[0].Controls.ToList())
            project.Screens[0].Controls.Remove(c);
        if (project.VariableManager.Variables.All(v => v.Id != "v_str"))
            project.VariableManager.Variables.Add(new Variable
            {
                Id = "v_str", Name = "文本", DataType = "String", Access = "rw",
            });
        var control = Fresh(type, tune);
        control.X = 40;
        control.Y = 60;
        control.Width = width;
        control.Height = height;
        project.Screens[0].Controls.Add(control);

        var dir = Path.Combine(_dir, "shoot-" + type);
        Directory.CreateDirectory(dir);
        var result = ExportFixture.Export(project, dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var shot = Path.Combine(dir, "shot.bmp");
        var psi = new ProcessStartInfo(binary)
        {
            WorkingDirectory = runtimeDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SDL_VIDEODRIVER"] = "dummy";
        psi.Environment["HMI_PROJECT_DIR"] = dir;
        psi.Environment["HMI_DATA_DIR"] = Path.Combine(dir, "data");
        psi.Environment["HMI_SHOT"] = shot;
        psi.Environment["HMI_RT_TRACE"] = "1";

        using var process = Process.Start(psi)!;
        var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20000), "运行时 20s 未退出");
        return (shot, log);
    }

    // ── 工程构造 ──────────────────────────────────────────

    /// <summary>把控件的 text 绑到一个数值变量上（输入框靠绑定类型决定 num/text）。</summary>
    private static Action<HmiControl> BindTo(string variableId) => c =>
        c.VariableBindings.Add(new VariableBindingData
        {
            PropertyName = "Text", VariableId = variableId, Direction = BindingDirection.ReadWrite,
        });

    /// <summary>按画布同一条路径造一个"什么都没改"的控件（<see cref="NewControlDefaults"/>）。</summary>
    private static HmiControl Fresh(HmiControlType type, Action<HmiControl>? tune = null)
    {
        var control = new HmiControl
        {
            Type = type,
            Name = "W1",
            X = 40,
            Y = 60,
            Width = 200,
            Height = 80,
            Text = NewControlDefaults.HasCaption(type) ? "T" : null,
        };
        NewControlDefaults.Apply(control);
        tune?.Invoke(control);
        return control;
    }

    /// <summary>导出单个新建控件，返回它的 props。</summary>
    private JsonElement ExportFresh(HmiControlType type, Action<HmiControl>? tune = null)
    {
        var project = ExportFixture.Build();
        foreach (var c in project.Screens[0].Controls.ToList())
            project.Screens[0].Controls.Remove(c);
        // 文本型输入要一个字符串变量才验得到（夹具里原本只有数值/布尔）
        if (project.VariableManager.Variables.All(v => v.Id != "v_str"))
            project.VariableManager.Variables.Add(new Variable
            {
                Id = "v_str", Name = "文本", DataType = "String", Access = "rw",
            });
        var control = Fresh(type, tune);
        project.Screens[0].Controls.Add(control);

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var path = System.IO.Path.Combine(_dir, "pages", project.Screens[0].Name + ".json");
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var widget = doc.RootElement.GetProperty("widgets").EnumerateArray().Single();
        return widget.GetProperty("props").Clone();
    }
}
