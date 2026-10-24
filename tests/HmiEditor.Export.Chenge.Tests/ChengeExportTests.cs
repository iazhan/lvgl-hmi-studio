using System.Diagnostics;
using System.Text.Json;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

public class ChengeExportTests : IDisposable
{
    private readonly string _dir;

    public ChengeExportTests()
    {
        _dir = TestTempDir.NewDir("chenge-export");
    }

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    [Fact]
    public void Export_ProducesProjectSkeleton()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);

        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        Assert.True(File.Exists(Path.Combine(_dir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "tags.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "devices.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "ports.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "pages", "home.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "alarms.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "savecfg.json")));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "manifest.json"))).RootElement;
        Assert.Equal("0.6", manifest.GetProperty("format_version").GetString());
        Assert.Equal("f1c100s-kinseal", manifest.GetProperty("hw_profile").GetString());
        Assert.Equal("home", manifest.GetProperty("start_page").GetString());
        Assert.Equal(800, manifest.GetProperty("screen").GetProperty("width").GetInt32());
        Assert.Contains("curves", manifest.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.Contains("alarm", manifest.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public void Export_CoreWidgets_MapToContractTypes()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var widgets = page.GetProperty("widgets").EnumerateArray().ToList();
        var byType = widgets.GroupBy(w => w.GetProperty("type").GetString()!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var type in new[] { "label", "value", "button", "lamp", "switch", "slider", "bar", "alarm_list", "history_curve" })
            Assert.True(byType.ContainsKey(type), $"缺少控件类型 {type}；实际 {string.Join(',', byType.Keys)}");

        // 按钮：click → write_tag，且 tag id 按变量顺序 0 起分配（炉温=0、启动=1、设定=2）
        var button = byType["button"][0];
        var action = button.GetProperty("events").GetProperty("click").GetProperty("actions")[0];
        Assert.Equal("write_tag", action.GetProperty("do").GetString());
        Assert.Equal(1, action.GetProperty("tag").GetInt32());

        // 开关：checked 绑定写回（不需要额外动作）
        var sw = byType["switch"][0];
        Assert.Equal(1, sw.GetProperty("bind").GetProperty("checked").GetProperty("tag").GetInt32());

        // 滑块：value 绑定
        Assert.Equal(2, byType["slider"][0].GetProperty("bind").GetProperty("value").GetProperty("tag").GetInt32());

        // 历史曲线：group 必须与 savecfg 组名一致
        Assert.Equal("temp", byType["history_curve"][0].GetProperty("props").GetProperty("group").GetString());

        // 报警列表：source.alarm_group 指到报警组
        Assert.Equal("温度", byType["alarm_list"][0].GetProperty("props").GetProperty("source")
            .GetProperty("alarm_group").GetString());
    }

    /// <summary>
    /// 批次 1a：仪表 / 指示灯 / 按钮的细节属性必须真的落进 JSON（面板能改 ≠ 导得出去）。
    /// 属性名写错会先被 <see cref="Export_AllPropsAreDeclaredInSchema"/> 拦住，这里再钉住取值。
    /// </summary>
    [Fact]
    public void Export_DetailProps_ReachThePageJson()
    {
        // 二次确认单独构造：它会拦住"点一下就写变量"，所以不能进宿主测试的公共夹具
        var project = ExportFixture.Build();
        var button = project.Screens[0].Controls.First(c => c.Type == HmiControlType.Button);
        button.ButtonConfirmText = "确定启动？";
        button.ButtonConfirmYes = "确定";
        button.ButtonConfirmNo = "取消";
        button.ButtonConfirmWaitMs = 5000;

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var byType = page.GetProperty("widgets").EnumerateArray()
            .GroupBy(w => w.GetProperty("type").GetString()!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var btn = byType["button"][0].GetProperty("props");
        Assert.Equal("确定启动？", btn.GetProperty("confirm").GetProperty("text").GetString());
        Assert.Equal("确定", btn.GetProperty("confirm").GetProperty("yes").GetString());
        Assert.Equal(5000, btn.GetProperty("confirm").GetProperty("wait_ms").GetInt32());
        Assert.Equal(100, btn.GetProperty("hold_ms").GetInt32());
        Assert.Equal(1200, btn.GetProperty("long_press_ms").GetInt32());
        Assert.Equal("hide", btn.GetProperty("off_style").GetString());

        var lamp = byType["lamp"][0].GetProperty("props");
        Assert.Equal(32, lamp.GetProperty("size").GetInt32());
        var lampRanges = lamp.GetProperty("ranges").EnumerateArray().ToList();
        Assert.Equal(3, lampRanges.Count);
        Assert.Equal(50, lampRanges[0].GetProperty("lt").GetInt32());
        Assert.Equal("#38C172", lampRanges[0].GetProperty("color").GetString());
        Assert.False(lampRanges[2].TryGetProperty("lt", out _));   // 最后一段是兜底

        var gauge = byType["gauge"][0].GetProperty("props");
        Assert.Equal("pointer", gauge.GetProperty("style").GetString());
        Assert.Equal(12, gauge.GetProperty("arc_width").GetInt32());
        Assert.Equal(8, gauge.GetProperty("tick_len").GetInt32());
        Assert.Equal(5, gauge.GetProperty("needle_width").GetInt32());
        Assert.Equal(8, gauge.GetProperty("center_radius").GetInt32());
        Assert.Equal(0.7, gauge.GetProperty("needle_len").GetDouble(), 2);
        Assert.Equal(2, gauge.GetProperty("ranges").EnumerateArray().Count());
    }

    /// <summary>
    /// 批次 1b：开关/进度条/加载圈/文本/输入框/旋钮/页签的细节属性必须真的落进 JSON。
    /// 属性名写错会先被 <see cref="Export_AllPropsAreDeclaredInSchema"/> 拦住，这里钉取值。
    /// </summary>
    [Fact]
    public void Export_Batch1bDetailProps_ReachThePageJson()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var byType = page.GetProperty("widgets").EnumerateArray()
            .GroupBy(w => w.GetProperty("type").GetString()!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var label = byType["label"][0].GetProperty("props");
        Assert.Equal(2, label.GetProperty("letter_spacing").GetInt32());
        Assert.Equal(6, label.GetProperty("line_spacing").GetInt32());
        Assert.False(label.TryGetProperty("font_style", out _));   // normal 是默认值，不写

        var sw = byType["switch"][0].GetProperty("props");
        Assert.Equal("#FFCC00", sw.GetProperty("knob_color").GetString());
        Assert.Equal("#22AA55", sw.GetProperty("color_on").GetString());

        var bar = byType["bar"][0].GetProperty("props");
        Assert.Equal("v", bar.GetProperty("direction").GetString());
        Assert.True(bar.GetProperty("show_percent").GetBoolean());
        Assert.Equal(2, bar.GetProperty("ranges").EnumerateArray().Count());

        var spinner = byType["spinner"][0].GetProperty("props");
        Assert.Equal(240, spinner.GetProperty("arc_len").GetInt32());
        Assert.Equal(6, spinner.GetProperty("arc_width").GetInt32());
        Assert.Equal(800, spinner.GetProperty("speed_ms").GetInt32());

        var knob = byType["knob"][0].GetProperty("props");
        Assert.Equal(14, knob.GetProperty("arc_width").GetInt32());

        var input = byType["input"][0].GetProperty("props");
        Assert.Equal("blur", input.GetProperty("commit").GetString());
    }

    /// <summary>
    /// 批次 2：历史曲线的外观与刻度必须真的落进 JSON（运行时 build_curve 全读这些键）。
    /// 属性名写错会先被 <see cref="Export_AllPropsAreDeclaredInSchema"/> 拦住。
    /// </summary>
    [Fact]
    public void Export_CurveDetailProps_ReachThePageJson()
    {
        var project = ExportFixture.Build();
        var curve = project.Screens[0].Controls.First(c => c.Type == HmiControlType.HistoryCurve);
        curve.CurveGrid = false;
        curve.CurveLegend = false;
        curve.CurveXDiv = 6;
        curve.CurveYDiv = 5;
        curve.CurveYDecimals = 2;
        curve.CurveY2Max = 200;
        curve.CurveXFormat = "rel";
        curve.CurveAxisColor = "#112233";
        curve.CurveRangeLastMin = 45;

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var props = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "history_curve")
            .GetProperty("props");

        Assert.False(props.GetProperty("grid").GetBoolean());
        Assert.False(props.GetProperty("legend").GetBoolean());
        Assert.Equal(6, props.GetProperty("x_div").GetInt32());
        Assert.Equal(5, props.GetProperty("y_div").GetInt32());
        Assert.Equal(2, props.GetProperty("y_decimals").GetInt32());
        Assert.Equal(200, props.GetProperty("y2_max").GetInt32());
        Assert.Equal("rel", props.GetProperty("x_format").GetString());
        Assert.Equal("#112233", props.GetProperty("axis_color").GetString());
        Assert.Equal(45, props.GetProperty("range").GetProperty("last_min").GetInt32());
        Assert.Equal(1, props.GetProperty("curves").GetArrayLength());
    }

    /// <summary>
    /// 批次 2c：报警列表的显示细节必须落进 JSON，且取值落在运行时的白名单里
    /// （这些枚举是硬校验，写错会让**整页**建不出来）。
    /// </summary>
    [Fact]
    public void Export_AlarmListDetailProps_ReachThePageJson()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var props = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "alarm_list")
            .GetProperty("props");

        Assert.Equal("newest_last", props.GetProperty("order").GetString());
        Assert.Equal("dblclick", props.GetProperty("ack_on").GetString());
        Assert.Equal("rows", props.GetProperty("grid").GetString());
        Assert.Equal(30, props.GetProperty("row_height").GetInt32());
        Assert.False(props.GetProperty("blink").GetBoolean());
        Assert.Equal(1000, props.GetProperty("refresh_ms").GetInt32());
        Assert.Equal("yyyy-MM-dd", props.GetProperty("date_format").GetString());
        Assert.Equal("HH:mm", props.GetProperty("time_format").GetString());
    }

    /// <summary>
    /// 批次 2b：实时曲线与历史曲线共用外观面板，但数据源不同 —— 它写 sample_ms/history_s/span_s，
    /// **不写** group / range / refresh_ms（那三个是 trends.json 存盘组的东西）。
    /// </summary>
    [Fact]
    public void Export_RealtimeCurve_UsesSamplePropsNotGroup()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var props = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "realtime_curve")
            .GetProperty("props");

        Assert.Equal(500, props.GetProperty("sample_ms").GetInt32());
        Assert.Equal(60, props.GetProperty("history_s").GetInt32());
        Assert.Equal(300, props.GetProperty("span_s").GetInt32());
        Assert.Equal(1, props.GetProperty("curves").GetArrayLength());
        Assert.False(props.TryGetProperty("group", out _));
        Assert.False(props.TryGetProperty("refresh_ms", out _));
    }

    /// <summary>
    /// 批次 2d：报警条的 12 个 props 必须落进 JSON；order 只有两个合法值（运行时硬校验）。
    /// </summary>
    [Fact]
    public void Export_AlarmBarDetailProps_ReachThePageJson()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var props = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "alarm_bar")
            .GetProperty("props");

        Assert.Equal("温度", props.GetProperty("source").GetProperty("alarm_group").GetString());
        Assert.Equal("newest_last", props.GetProperty("order").GetString());
        Assert.Equal("系统正常", props.GetProperty("normal_text").GetString());
        Assert.False(props.GetProperty("only_unacked").GetBoolean());
        Assert.Equal(2000, props.GetProperty("scroll_ms").GetInt32());
        Assert.Equal(8, props.GetProperty("scroll_chars").GetInt32());
        Assert.True(props.GetProperty("show_time").GetBoolean());
        Assert.Equal("HH:mm:ss", props.GetProperty("time_format").GetString());
    }

    /// <summary>
    /// 管道在契约里是**控件**（widgets.pipe，6 个 props），运行时也有 W_PIPE 分派。
    /// 早先导成 shape 的 pipe_h/v 只保留了 wall，direction/color/channel_color/flow_ms 全丢。
    /// </summary>
    [Fact]
    public void Export_Pipe_UsesWidgetPropsNotShape()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var pipe = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "pipe");
        var props = pipe.GetProperty("props");

        Assert.Equal("v", props.GetProperty("direction").GetString());
        Assert.Equal(0.3, props.GetProperty("wall").GetDouble(), 3);
        Assert.Equal(500, props.GetProperty("flow_ms").GetInt32());
        Assert.Equal("#223344", props.GetProperty("channel_color").GetString());
        Assert.Equal("#4EA1FF", props.GetProperty("color").GetString());
        Assert.False(props.TryGetProperty("kind", out _));   // 不再是 shape
    }

    [Fact]
    public void Export_AllPropsAreDeclaredInSchema()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var schema = ChengeSchema.Load();
        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        foreach (var widget in page.GetProperty("widgets").EnumerateArray())
        {
            var type = widget.GetProperty("type").GetString()!;
            var kind = widget.GetProperty("props").TryGetProperty("kind", out var k) ? k.GetString() : null;
            var allowed = schema.AllowedProps(type, kind);
            foreach (var prop in widget.GetProperty("props").EnumerateObject())
                Assert.True(allowed.Contains(prop.Name), $"{type} 写了契约外属性 {prop.Name}");
        }
    }

    [Fact]
    public void Export_ButtonWithoutTag_ReportsError()
    {
        var project = ExportFixture.Build();
        project.Screens[0].Controls.First(c => c.Type == Core.Models.HmiControlType.Button).Actions[0].Target = "不存在的变量";

        var result = ExportFixture.Export(project, _dir);

        Assert.False(result.Success);
        Assert.Contains(result.Issues.Items, i => i.Code == "action.tag_missing");
        Assert.False(File.Exists(Path.Combine(_dir, "manifest.json")));
    }

    [Fact]
    public void Export_TwoActionsOnOneEvent_IsRejected()
    {
        var project = ExportFixture.Build();
        var button = project.Screens[0].Controls.First(c => c.Type == Core.Models.HmiControlType.Button);
        button.Actions.Add(new Core.Models.HmiAction { ActionType = "ToggleBit", Target = "v_start", TriggerEvent = "OnClick" });

        var result = ExportFixture.Export(project, _dir);

        Assert.False(result.Success);
        Assert.Contains(result.Issues.Items, i => i.Code == "event.multi_action");
    }

    /// <summary>
    /// 内存导出必须与目录导出**逐字节一致**：预览读内存、正式导出读目录，
    /// 两者若有差异，就会出现"预览好看、导出到板上不对"。
    /// </summary>
    [Fact]
    public void MemoryExport_MatchesDirectoryExport()
    {
        var project = ExportFixture.Build();
        var exporter = new ChengeExporter();

        var mem = exporter.ExportToMemory(project, null, new ChengeExportOptions(), null, out var memResult);
        Assert.True(memResult.Success, string.Join("\n", memResult.Issues.Items));

        var dir = Path.Combine(_dir, "disk");
        var diskResult = exporter.Export(project, null, new ChengeExportOptions(), dir);
        Assert.True(diskResult.Success, string.Join("\n", diskResult.Issues.Items));

        var diskFiles = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(diskFiles, mem.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        foreach (var rel in diskFiles)
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, rel)), mem[rel]);
    }

    /// <summary>
    /// 真跑宿主运行时：需要 CHENGE_RUNTIME_DIR（其下有 make HOST=1 的 chenge-runtime）。
    /// 没有就跳过——CI 上没编运行时不该是红。
    /// </summary>
    [Fact]
    public void ExportedProject_LoadsInHostRuntime()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        var binary = runtimeDir == null ? null : Path.Combine(runtimeDir, "chenge-runtime");
        if (binary == null || !File.Exists(binary))
            return; // 跳过：没有宿主二进制

        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var shot = Path.Combine(_dir, "shot.bmp");
        var psi = new ProcessStartInfo(binary)
        {
            WorkingDirectory = runtimeDir!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SDL_VIDEODRIVER"] = "dummy";
        psi.Environment["HMI_PROJECT_DIR"] = _dir;
        psi.Environment["HMI_DATA_DIR"] = Path.Combine(_dir, "data");
        psi.Environment["HMI_SHOT"] = shot;
        psi.Environment["HMI_RT_TRACE"] = "1";
        // 按钮中心：启动。delay 给足 1500ms —— 整组测试并行跑时机器更忙，
        // 800ms 偶尔会在建页完成前注入，点击落空（本测试只在整组跑时偶发失败）。
        psi.Environment["HMI_HOST_CLICK"] = "110,108,1500";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20000), "运行时 20s 未退出");

        var log = stdout + stderr;
        Assert.True(log.Contains("page: home 打开"), log);
        Assert.True(log.Contains("启动=1"), log);
        Assert.True(File.Exists(shot), "没有产出截图：" + log);
    }
}
