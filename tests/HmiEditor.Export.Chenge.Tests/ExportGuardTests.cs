using System.Diagnostics;
using System.Text.Json;
using HmiEditor.Core;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 导出期的"取值域防线"：运行时的建页对一批数值是**硬校验**（越界直接 return -1 ⇒
/// 整页建不起来），而这些工程在导出报告里本来是干净的 —— 越界值写出去、上板才发现。
/// 这一组测试钉住"导出器自己先把值收进运行时能接受的区间 / 该报错的报错"。
/// </summary>
public class ExportGuardTests : IDisposable
{
    private readonly string _dir;

    public ExportGuardTests()
    {
        _dir = TestTempDir.NewDir("chenge-export-guard");
    }

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    private JsonElement PageWidgets() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json")))
            .RootElement.GetProperty("widgets");

    private JsonElement FirstOfType(string type) =>
        PageWidgets().EnumerateArray().First(w => w.GetProperty("type").GetString() == type);

    /// <summary>
    /// 曲线的四个取值域：span_s ≤ 3600（编辑器填分钟，上限 60）、div ≤ 10、minor ≤ 5、
    /// direction 只有 lr。越界一律钳住 + 告警，**不能**让它们以 Error 挡住整份导出，
    /// 更不能原样写出去（运行时那边是拒绝整页）。
    /// </summary>
    [Fact]
    public void Export_CurveOutOfRuntimeRange_IsClampedWithWarning()
    {
        var project = ExportFixture.Build();
        var curve = project.Screens[0].Controls.First(c => c.Type == HmiControlType.RealtimeCurve);
        curve.SpanMin = 90;              // → 5400 秒，运行时上限 3600
        curve.CurveDirection = "rl";     // 运行时只实现 lr
        curve.CurveXDiv = 12;            // 运行时上限 10
        curve.CurveYMinor = 7;           // 运行时上限 5

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var props = FirstOfType("realtime_curve").GetProperty("props");
        Assert.Equal(3600, props.GetProperty("span_s").GetInt32());
        Assert.Equal("lr", props.GetProperty("direction").GetString());
        Assert.Equal(10, props.GetProperty("x_div").GetInt32());
        Assert.Equal(5, props.GetProperty("y_minor").GetInt32());

        Assert.Contains(result.Issues.Items, i => i.Code == "curve.span");
        Assert.Contains(result.Issues.Items, i => i.Code == "curve.direction");
    }

    /// <summary>
    /// 量程下限 ≥ 上限：运行时 bar/slider/knob/gauge/chart/曲线一律拒绝整页。
    /// 导出期必须是 Error（不是悄悄钳一下 —— 钳完用户看到的量程就不是他填的了）。
    /// </summary>
    [Fact]
    public void Export_InvertedRange_ReportsError()
    {
        var project = ExportFixture.Build();
        var bar = project.Screens[0].Controls.First(c => c.Type == HmiControlType.Bar);
        bar.MinValue = 100;
        bar.MaxValue = 0;

        var result = ExportFixture.Export(project, _dir);

        Assert.False(result.Success);
        var issue = Assert.Single(result.Issues.Items, i => i.Code == "bar.range");
        Assert.Equal(ChengeIssueSeverity.Error, issue.Severity);
    }

    /// <summary>
    /// 阀门：契约 figures 里有 valve_ball / valve_butterfly / valve_gate，
    /// 导出器早先以"契约没有 valve_*"为由整类拒绝（错），现在按 ValveType 选 kind。
    /// </summary>
    [Fact]
    public void Export_Valve_MapsToValveFigureKind()
    {
        var project = ExportFixture.Build();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "v_gate", Type = HmiControlType.Valve, Name = "Valve1",
            X = 620, Y = 300, Width = 60, Height = 60,
            ValveType = "Gate", BgColor = "#EF4444",
        });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        // 控件 id 会被导出器重编（control.Id → widget id），所以按 kind 找而不是按 id
        var valve = PageWidgets().EnumerateArray().Single(w =>
            w.GetProperty("props").TryGetProperty("kind", out var k) &&
            k.GetString() == "valve_gate");
        Assert.Equal("shape", valve.GetProperty("type").GetString());
        var props = valve.GetProperty("props");
        Assert.Equal("#EF4444", props.GetProperty("fill_color").GetString());
    }

    /// <summary>
    /// 表格与曲线的字号在运行时是 10..32 的硬校验（面板允许到 72），
    /// 导出器必须把这两类的 font_size 收到 32，否则整页拒绝。
    /// </summary>
    [Fact]
    public void Export_TableAndCurveFontSize_IsClampedToRuntimeMax()
    {
        var project = ExportFixture.Build();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "t1", Type = HmiControlType.Table, Name = "Table1",
            X = 20, Y = 200, Width = 300, Height = 120,
            RowCount = 3, ColumnCount = 3, FontSize = 48,
        });
        var curve = project.Screens[0].Controls.First(c => c.Type == HmiControlType.RealtimeCurve);
        curve.FontSize = 48;

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        Assert.Equal(32, FirstOfType("table").GetProperty("props").GetProperty("font_size").GetInt32());
        Assert.Equal(32, FirstOfType("realtime_curve").GetProperty("props").GetProperty("font_size").GetInt32());
    }

    /// <summary>
    /// 输入框的 `Text` 绑定必须真的导出成 `bind.text`。
    ///
    /// <para><b>为什么值得一条判据</b>：契约里 <c>input.bindable</c> 原先只有 <c>value</c>，
    /// 导出器的 <c>CanBind("input","text")</c> 因此为假 —— 输入框的绑定被静默跳过，
    /// 运行时的 <c>bind_tag</c> 保持 -1，点击回调根本没挂上，现象就是"点输入框不弹键盘"
    /// 而导出报告只有一条没人看的 Warn。</para>
    /// </summary>
    [Fact]
    public void Export_TextAreaTextBinding_IsEmittedAsBindText()
    {
        var project = ExportFixture.Build();
        var input = project.Screens[0].Controls.FirstOrDefault(c => c.Type == HmiControlType.TextArea);
        if (input == null)
        {
            input = new HmiControl
            {
                Id = "in1", Name = "Input1", Type = HmiControlType.TextArea,
                X = 100, Y = 300, Width = 200, Height = 40,
            };
            project.Screens[0].Controls.Add(input);
        }
        // 绑到工程里的第一个变量（夹具里一定有）
        var tagId = project.VariableManager.Variables[0].Id;
        input.VariableBindings.Clear();
        input.VariableBindings.Add(new VariableBindingData { PropertyName = "Text", VariableId = tagId });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        Assert.DoesNotContain(result.Issues.Items, i => i.Code is "bind.not_bindable" or "bind.property");

        var widget = PageWidgets().EnumerateArray()
            .Single(w => w.GetProperty("type").GetString() == "input");
        Assert.True(widget.TryGetProperty("bind", out var bind), "输入框的绑定没有导出（运行时因此不弹键盘）");
        Assert.True(bind.TryGetProperty("text", out var textBind), "应该导出成 bind.text");
        // tag 号由工程里的变量顺序决定，这里只断言"确实落到了某个变量上"
        Assert.True(textBind.GetProperty("tag").GetInt32() >= 0);
    }

    /// <summary>
    /// 进度类控件的 `Value` 绑定必须导出成 `bind.value`。
    ///
    /// <para><b>为什么值得一条判据</b>：<c>MapBindProperty</c> 一直认识 <c>Value → value</c>，
    /// 但属性面板的「目标属性」只有 值/文本（映射成 <c>text</c>）、颜色、可见性 ——
    /// bar/gauge/knob/slider/弧的数值绑定在界面里根本造不出来。面板现在补了「数值」，
    /// 这条判据钉住它落到 <c>bind.value</c> 而不是被 <c>bind.not_bindable</c> 丢掉。</para>
    /// </summary>
    [Fact]
    public void Export_BarValueBinding_IsEmittedAsBindValue()
    {
        var project = ExportFixture.Build();
        var tagId = project.VariableManager.Variables[0].Id;
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "bar1", Name = "Bar1", Type = HmiControlType.Bar,
            X = 40, Y = 320, Width = 200, Height = 30,
            MinValue = 0, MaxValue = 100,
            VariableBindings =
            {
                new VariableBindingData { PropertyName = "Value", VariableId = tagId },
            },
        });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        Assert.DoesNotContain(result.Issues.Items, i => i.Code is "bind.not_bindable" or "bind.property");

        var bind = FirstOfType("bar").GetProperty("bind");
        Assert.True(bind.TryGetProperty("value", out var valueBind), "条的数值绑定没有导出成 bind.value");
        Assert.True(valueBind.GetProperty("tag").GetInt32() >= 0);
    }

    /// <summary>
    /// 绑到**字符串变量**的文本要走直搬（`{tag}`），不能带 `kind:fmt` + `%.2f`：
    /// 运行时按数值渲染字符串变量会得到 0.00，输入框/标签里看不到真正的文本。
    /// </summary>
    [Fact]
    public void Export_StringTagTextBinding_IsDirectNotFmt()
    {
        var project = ExportFixture.Build();
        project.VariableManager.Variables.Add(new Variable
        {
            Id = "v_name", Name = "名称", DataType = "String", Access = "rw",
        });

        var label = project.Screens[0].Controls.First(c => c.Type == HmiControlType.Label);
        label.VariableBindings.Clear();
        label.VariableBindings.Add(new VariableBindingData { PropertyName = "Text", VariableId = "v_name" });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var widget = PageWidgets().EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "label");
        var textBind = widget.GetProperty("bind").GetProperty("text");
        Assert.False(textBind.TryGetProperty("kind", out _),
            "字符串变量的 text 绑定不该带 kind:fmt（运行时会把字符串按 %.2f 渲染成 0.00）");
    }

    /// <summary>
    /// 旋转 / 镜像：契约有这三个键、运行时 apply_transform 也实现了，导出器此前只告警
    /// 丢弃 —— 用户在面板上转了控件，画布（宿主位图）和板子都不动。这里钉住"真的写出去"，
    /// 以及"越档的组合在导出期就报 Error"（运行时越档是拒绝整页）。
    /// </summary>
    [Fact]
    public void Export_RotationAndMirror_AreWrittenForSupportedTypes()
    {
        var project = ExportFixture.Build();
        var label = project.Screens[0].Controls.First(c => c.Type == HmiControlType.Label);
        label.Rotation = 90;
        label.RotationDirection = "CW";

        var shape = new HmiControl
        {
            Id = "s_rot", Type = HmiControlType.Shape, Name = "Shape1",
            ShapeKind = "star5", X = 620, Y = 120, Width = 80, Height = 80,
            Rotation = 30, MirrorX = true,
        };
        project.Screens[0].Controls.Add(shape);

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var labelProps = PageWidgets().EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "label").GetProperty("props");
        Assert.Equal(90, labelProps.GetProperty("rotation").GetInt32());

        var shapeProps = PageWidgets().EnumerateArray().Single(w =>
            w.GetProperty("props").TryGetProperty("rotation", out var r) && r.GetInt32() == 30)
            .GetProperty("props");
        Assert.Equal("star5", shapeProps.GetProperty("kind").GetString());
        Assert.Equal(30, shapeProps.GetProperty("rotation").GetInt32());
        Assert.True(shapeProps.GetProperty("mirror_x").GetBoolean());
    }

    /// <summary>
    /// 越档的变换必须在导出期报错（运行时是整页拒绝）：
    /// 文本类不能镜像、不能转非 90° 倍数；rect/circle 没有顶点可翻，不能镜像。
    /// </summary>
    [Theory]
    [InlineData("label-mirror")]
    [InlineData("label-45deg")]
    [InlineData("rect-mirror")]
    public void Export_UnsupportedTransform_ReportsError(string scenario)
    {
        var project = ExportFixture.Build();
        var label = project.Screens[0].Controls.First(c => c.Type == HmiControlType.Label);

        switch (scenario)
        {
            case "label-mirror":
                label.MirrorX = true;
                break;
            case "label-45deg":
                label.Rotation = 45;
                break;
            case "rect-mirror":
                project.Screens[0].Controls.Add(new HmiControl
                {
                    Id = "r1", Type = HmiControlType.Rectangle, Name = "Rect1",
                    X = 620, Y = 200, Width = 80, Height = 60, MirrorY = true,
                });
                break;
        }

        var result = ExportFixture.Export(project, _dir);

        Assert.False(result.Success);
        var issue = Assert.Single(result.Issues.Items, i => i.Code == "widget.transform");
        Assert.Equal(ChengeIssueSeverity.Error, issue.Severity);
    }

    /// <summary>
    /// 帧动画：运行时要求 2..32 帧，只有 1 帧会整页拒绝，所以导出期就要报错
    /// （早先只挡 0 帧）。这里用"导不出来的帧"构造 0 可用帧，命中同一条校验。
    /// </summary>
    [Fact]
    public void Export_FrameAnimWithoutEnoughFrames_ReportsError()
    {
        var project = ExportFixture.Build();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "fa1", Type = HmiControlType.FrameAnim, Name = "FrameAnim1",
            X = 620, Y = 380, Width = 80, Height = 80,
            FrameImages = "missing-asset",
        });

        var result = ExportFixture.Export(project, _dir);

        Assert.False(result.Success);
        var issue = Assert.Single(result.Issues.Items, i => i.Code == "frame_anim.frames");
        Assert.Contains("2 帧", issue.Message);
    }

    /// <summary>
    /// 端到端：把曲线填成"旧导出器会原样写出去、运行时必然拒绝整页"的那组值，
    /// 导出后真跑宿主运行时，页面必须能打开。需要 CHENGE_RUNTIME_DIR（其下有 make HOST=1
    /// 的 chenge-runtime），没有就跳过 —— 与 ChengeExportTests 里那条同一个门槛。
    /// </summary>
    [Fact]
    public void ExportedProject_WithOutOfRangeCurveSettings_StillLoadsInHostRuntime()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        var binary = runtimeDir == null ? null : Path.Combine(runtimeDir, "chenge-runtime");
        if (binary == null || !File.Exists(binary))
            return;

        var project = ExportFixture.Build();
        var curve = project.Screens[0].Controls.First(c => c.Type == HmiControlType.RealtimeCurve);
        curve.SpanMin = 90;
        curve.CurveDirection = "rl";
        curve.CurveXDiv = 12;
        curve.CurveYMinor = 7;
        // 顺带把阀门也放进这一页：它是这次新接通的图元，要证明运行时真能建出来
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "v_e2e", Type = HmiControlType.Valve, Name = "Valve1",
            X = 620, Y = 300, Width = 60, Height = 60, ValveType = "Butterfly",
        });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var psi = new ProcessStartInfo(binary)
        {
            WorkingDirectory = runtimeDir!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SDL_VIDEODRIVER"] = "dummy";
        psi.Environment["HMI_PROJECT_DIR"] = _dir;
        psi.Environment["HMI_DATA_DIR"] = Path.Combine(_dir, "data");
        psi.Environment["HMI_SHOT"] = Path.Combine(_dir, "shot.bmp");
        psi.Environment["HMI_RT_TRACE"] = "1";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20000), "运行时 20s 未退出");

        var log = stdout + stderr;
        Assert.True(log.Contains("page: home 打开"), log);
        // 整页被拒的典型日志：建页失败 / 属性越界
        Assert.DoesNotContain("建页失败", log);
        Assert.DoesNotContain("越界", log);
    }
}
