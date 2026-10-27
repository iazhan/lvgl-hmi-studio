using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HmiEditor.Core;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 「面板上有这一行」到「板端位图真的变」的端到端判据（docs/chenge-rt/50 的批次 3 剩余条与批次 4）。
///
/// <para><b>为什么不能只看导出 JSON</b>：这一类窟窿有两种，只看一侧都抓不住 ——
/// ① 导出器没写键（面板填了、产物里没有）；② 写了键、运行时没读（产物里有、位图不动）。
/// 所以每条属性都成对验：产物里必须有这个键，跑一趟宿主运行时后位图必须变。
/// 每条的"变"都是同一个控件、同一次导出流程、只改这一个面板字段，两点差异全部来自它。</para>
///
/// <para>缺 <c>CHENGE_RUNTIME_DIR</c>（宿主二进制）时像素判据跳过，与同目录其他渲染判据一致；
/// JSON 判据不需要运行时，永远执行。</para>
/// </summary>
public class ExportPanelKeysRenderTests : IDisposable
{
    private readonly string _dir;

    public ExportPanelKeysRenderTests() => _dir = TestTempDir.NewDir("panel-keys");
    public void Dispose() => TestTempDir.DeleteDir(_dir);

    // ── ① 产物里必须有这个键 ────────────────────────────────

    /// <summary>
    /// value 的「前缀 / 单位 / 后缀」是三个键。面板三个输入框都在，产物里却只有 unit，
    /// 前后缀整段丢掉；更糟的是以前把「后缀」当单位兜底（`60 %` 而不是 `60%`）。
    /// </summary>
    [Fact]
    public void Value_PrefixUnitSuffix_AreThreeSeparateKeys()
    {
        var project = IsolatedProject();
        var value = new HmiControl
        {
            Id = "v1", Name = "V1", Type = HmiControlType.Value,
            X = 40, Y = 60, Width = 240, Height = 40,
            FontSize = 20, TextColor = "#FFFFFF",
            ValuePrefix = "P", ValueSuffix = "S", Unit = "℃", ValueDecimalPlaces = 1,
        };
        value.VariableBindings.Add(new VariableBindingData { PropertyName = "Text", VariableId = "v_set" });
        project.Screens[0].Controls.Add(value);

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        var widget = ReadWidget(_dir, 1);
        var props = widget.GetProperty("props");
        Assert.Equal("P", props.GetProperty("prefix").GetString());
        Assert.Equal("S", props.GetProperty("suffix").GetString());
        Assert.Equal("℃", props.GetProperty("unit").GetString());
        // 前后缀改由 props 上的三个键表达之后，数值本身仍要靠绑定（bind 是 widget 级，
        // 不在 props 里）；占位符写法也不该再出现，否则前后缀会拼两遍
        Assert.True(widget.TryGetProperty("bind", out var bind) && bind.TryGetProperty("text", out _),
            "value 只剩前后缀键、没有文字绑定：数值不会显示");
        Assert.False(props.TryGetProperty("text", out _),
            "props.text 里的占位符会和 props.prefix/suffix 叠着拼，前后缀出现两遍");
    }

    /// <summary>表头的文字（面板「表头」那行）必须进 cells 的第 0 行，不能只写布尔 header。</summary>
    [Fact]
    public void Table_HeaderTexts_BecomeFirstRowCells()
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "t1", Name = "T1", Type = HmiControlType.Table,
            X = 40, Y = 60, Width = 300, Height = 120,
            RowCount = 2, ColumnCount = 2, HeaderTexts = "温度,压力",
            Cells = new List<TableCellData> { new() { Row = 1, Col = 0, Text = "60" } },
        });

        var cells = ExportOne(project).GetProperty("cells").EnumerateArray().ToList();
        Assert.Contains(cells, c => c.GetProperty("r").GetInt32() == 0 && c.GetProperty("c").GetInt32() == 0
                                    && c.GetProperty("text").GetString() == "温度");
        Assert.Contains(cells, c => c.GetProperty("r").GetInt32() == 0 && c.GetProperty("c").GetInt32() == 1
                                    && c.GetProperty("text").GetString() == "压力");
    }

    /// <summary>指示灯亮度：面板有这一行（0..255），导出器必须写，否则运行时恒按 255 画。</summary>
    [Fact]
    public void Led_Brightness_IsExported()
    {
        var props = ExportOne(WithLed(40));
        Assert.Equal(40, props.GetProperty("brightness").GetInt32());
    }

    /// <summary>标签长文模式：面板五档（换行/省略号/滚动/首尾滚动/裁掉）要变成契约键。</summary>
    [Theory]
    [InlineData("Dots", "dots")]
    [InlineData("Clip", "clip")]
    [InlineData("Scroll", "scroll")]
    [InlineData("ScrollCircular", "scroll_circular")]
    public void Label_LongMode_IsExported(string panel, string expected)
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "l1", Name = "L1", Type = HmiControlType.Label,
            X = 40, Y = 60, Width = 200, Height = 80,
            Text = new string('0', 120), FontSize = 20, LabelLongMode = panel,
        });
        Assert.Equal(expected, ExportOne(project).GetProperty("long_mode").GetString());
    }

    /// <summary>管道流动方向（面板「正向 / 反向」）：反向必须落成 flow_dir=reverse。</summary>
    [Fact]
    public void Pipe_ReverseFlow_IsExported()
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "p1", Name = "P1", Type = HmiControlType.Pipe,
            X = 40, Y = 60, Width = 300, Height = 80,
            PipeDirection = "Horizontal", PipeFlowDirection = "Reverse", PipeFlowSpeed = 400,
        });
        Assert.Equal("reverse", ExportOne(project).GetProperty("flow_dir").GetString());
    }

    /// <summary>下拉框初始选中项：以前只告警不写（schema 没这个键），面板上改了等于没改。</summary>
    [Fact]
    public void Dropdown_SelectedIndex_IsExportedWithoutWarning()
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "d1", Name = "D1", Type = HmiControlType.Dropdown,
            X = 40, Y = 60, Width = 200, Height = 40,
            Options = "A\nB\nC", SelectedIndex = 2, FontSize = 20,
        });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        Assert.DoesNotContain(result.Issues.Items, i => i.Code == "combobox.selected");

        var props = ReadWidget(_dir, 1).GetProperty("props");
        Assert.Equal(2, props.GetProperty("selected").GetInt32());
    }

    // ── ② 位图必须变（端到端） ──────────────────────────────

    /// <summary>前后缀进产物之后，位图也必须出现它们：P0S → 0。</summary>
    [Fact]
    public void ValuePrefixSuffix_ReachTheBitmap()
    {
        if (!TryLocateRuntime(out var rt, out var bin)) return;
        var rect = (X: 40, Y: 60, W: 240, H: 40);

        var withAffix = ExportAndShoot(WithValue(rect, "P", "S"), Path.Combine(_dir, "affix"), rt, bin);
        var plain = ExportAndShoot(WithValue(rect, null, null), Path.Combine(_dir, "plain"), rt, bin);

        var changed = CountDiff(withAffix, plain, rect.X, rect.Y, rect.W, rect.H);
        Assert.True(changed > 40, $"前后缀 P/S 只改了 {changed} 个像素（期望 >40：两个字形）");
    }

    /// <summary>换一套表头文字，表头行的像素必须跟着变。</summary>
    [Fact]
    public void TableHeaderTexts_ReachTheBitmap()
    {
        if (!TryLocateRuntime(out var rt, out var bin)) return;
        var rect = (X: 40, Y: 60, W: 300, H: 120);

        var a = ExportAndShoot(WithTable(rect, "AAAA,BBBB"), Path.Combine(_dir, "hdr_a"), rt, bin);
        var b = ExportAndShoot(WithTable(rect, "CCCC,DDDD"), Path.Combine(_dir, "hdr_b"), rt, bin);

        var changed = CountDiff(a, b, rect.X, rect.Y, rect.W, rect.H);
        Assert.True(changed > 150, $"表头文字改了但位图只差 {changed} 个像素（期望 >150）");
    }

    /// <summary>亮度 40 与 255 的灯珠必须能看出差别。</summary>
    [Fact]
    public void LampBrightness_ReachesTheBitmap()
    {
        if (!TryLocateRuntime(out var rt, out var bin)) return;
        var rect = (X: 100, Y: 100, W: 80, H: 80);

        var dim = ExportAndShoot(WithLed(40, rect), Path.Combine(_dir, "dim"), rt, bin);
        var full = ExportAndShoot(WithLed(255, rect), Path.Combine(_dir, "full"), rt, bin);

        var changed = CountDiff(dim, full, rect.X, rect.Y, rect.W, rect.H);
        Assert.True(changed > 200, $"亮度 40→255 只改了 {changed} 个像素（期望 >200）");
    }

    /// <summary>关掉中心读数：那几行字必须从位图上消失。</summary>
    [Fact]
    public void GaugeShowValueOff_RemovesTheReadout()
    {
        if (!TryLocateRuntime(out var rt, out var bin)) return;
        var rect = (X: 100, Y: 60, W: 260, H: 260);

        var on = ExportAndShoot(WithGauge(rect, true), Path.Combine(_dir, "gv_on"), rt, bin);
        var off = ExportAndShoot(WithGauge(rect, false), Path.Combine(_dir, "gv_off"), rt, bin);

        var changed = CountDiff(on, off, rect.X, rect.Y, rect.W, rect.H);
        Assert.True(changed > 50, $"show_value=false 只改了 {changed} 个像素（期望 >50：中心读数）");
    }

    /// <summary>下拉框的字号与文字色（面板两行）必须画出来。</summary>
    [Fact]
    public void DropdownFontAndColor_ReachTheBitmap()
    {
        if (!TryLocateRuntime(out var rt, out var bin)) return;
        var rect = (X: 40, Y: 60, W: 240, H: 60);

        var smallRed = ExportAndShoot(WithDropdown(rect, 12, "#FF0000"), Path.Combine(_dir, "dd_a"), rt, bin);
        var bigBlue = ExportAndShoot(WithDropdown(rect, 28, "#0000FF"), Path.Combine(_dir, "dd_b"), rt, bin);

        var changed = CountDiff(smallRed, bigBlue, rect.X, rect.Y, rect.W, rect.H);
        Assert.True(changed > 100, $"字号/文字色改了但位图只差 {changed} 个像素（期望 >100）");
    }

    /// <summary>输入框水平对齐：文字块的横向位置必须跟着面板变。</summary>
    [Fact]
    public void InputAlign_ReachesTheBitmap()
    {
        if (!TryLocateRuntime(out var rt, out var bin)) return;
        var rect = (X: 40, Y: 60, W: 240, H: 60);

        var left = ExportAndShoot(WithInput(rect, TextAlignMode.Left), Path.Combine(_dir, "in_l"), rt, bin);
        var right = ExportAndShoot(WithInput(rect, TextAlignMode.Right), Path.Combine(_dir, "in_r"), rt, bin);

        var changed = CountDiff(left, right, rect.X, rect.Y, rect.W, rect.H);
        Assert.True(changed > 100, $"输入框对齐 left→right 只改了 {changed} 个像素（期望 >100）");
    }

    // ── 工程构造 ──────────────────────────────────────────

    private static HmiProject IsolatedProject()
    {
        var project = ExportFixture.Build();
        foreach (var c in project.Screens[0].Controls.ToList())
            project.Screens[0].Controls.Remove(c);
        return project;
    }

    private static HmiProject WithValue((int X, int Y, int W, int H) r, string? prefix, string? suffix)
    {
        var project = IsolatedProject();
        var v = new HmiControl
        {
            Id = "v1", Name = "V1", Type = HmiControlType.Value,
            X = r.X, Y = r.Y, Width = r.W, Height = r.H,
            FontSize = 20, TextColor = "#FFFFFF", ValueDecimalPlaces = 0,
            ValuePrefix = prefix, ValueSuffix = suffix,
        };
        v.VariableBindings.Add(new VariableBindingData { PropertyName = "Text", VariableId = "v_set" });
        project.Screens[0].Controls.Add(v);
        return project;
    }

    private static HmiProject WithTable((int X, int Y, int W, int H) r, string header)
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "t1", Name = "T1", Type = HmiControlType.Table,
            X = r.X, Y = r.Y, Width = r.W, Height = r.H,
            RowCount = 2, ColumnCount = 2, HeaderTexts = header, FontSize = 20,
            Cells = new List<TableCellData> { new() { Row = 1, Col = 0, Text = "60" } },
        });
        return project;
    }

    private static HmiProject WithLed(int brightness, (int X, int Y, int W, int H)? rect = null)
    {
        var r = rect ?? (X: 100, Y: 100, W: 80, H: 80);
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "led1", Name = "LED1", Type = HmiControlType.Led,
            X = r.X, Y = r.Y, Width = r.W, Height = r.H,
            IndicatorColor = "#38C172", LampSize = 40, Brightness = brightness,
        });
        return project;
    }

    private static HmiProject WithGauge((int X, int Y, int W, int H) r, bool showValue)
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "g1", Name = "G1", Type = HmiControlType.Gauge,
            X = r.X, Y = r.Y, Width = r.W, Height = r.H,
            MinValue = 0, MaxValue = 100, GaugeShowValue = showValue,
            GaugeArcWidth = 8, GaugeTickLen = 6,
        });
        return project;
    }

    private static HmiProject WithDropdown((int X, int Y, int W, int H) r, int fontSize, string color)
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "d1", Name = "D1", Type = HmiControlType.Dropdown,
            X = r.X, Y = r.Y, Width = r.W, Height = r.H,
            Options = "AAAA\nBBBB", FontSize = fontSize, TextColor = color,
        });
        return project;
    }

    private static HmiProject WithInput((int X, int Y, int W, int H) r, TextAlignMode align)
    {
        var project = IsolatedProject();
        var input = new HmiControl
        {
            Id = "i1", Name = "I1", Type = HmiControlType.TextArea,
            X = r.X, Y = r.Y, Width = r.W, Height = r.H,
            FontSize = 20, TextAlign = align, MaxLength = 12,
        };
        input.VariableBindings.Add(new VariableBindingData { PropertyName = "Text", VariableId = "v_set" });
        project.Screens[0].Controls.Add(input);
        return project;
    }

    /// <summary>导出单个控件并返回它的 props。</summary>
    private JsonElement ExportOne(HmiProject project)
    {
        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        return ReadWidget(_dir, 1).GetProperty("props");
    }

    private static JsonElement ReadWidget(string dir, int seq)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "pages", "home.json")));
        return doc.RootElement.GetProperty("widgets")[seq - 1].Clone();
    }

    private static bool TryLocateRuntime(out string runtimeDir, out string binary)
    {
        runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR") ?? "";
        binary = runtimeDir.Length == 0 ? "" : Path.Combine(runtimeDir, "chenge-runtime");
        return binary.Length > 0 && File.Exists(binary);
    }

    private static string ExportAndShoot(HmiProject project, string dir, string runtimeDir, string binary)
    {
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
        Assert.True(File.Exists(shot), $"没有截图（工程建页失败？）\n{log}");
        return shot;
    }

    private readonly record struct Bitmap(byte[] Data, int Offset, int Width, int Height,
        int Px, int RowSize)
    {
        public (byte R, byte G, byte B) At(int x, int y)
        {
            var row = Height - 1 - y;          // BMP 自下而上
            var i = Offset + row * RowSize + x * Px;
            return (Data[i + 2], Data[i + 1], Data[i]);
        }
    }

    private static Bitmap Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new Bitmap(bytes, BitConverter.ToInt32(bytes, 10), BitConverter.ToInt32(bytes, 18),
            Math.Abs(BitConverter.ToInt32(bytes, 22)), BitConverter.ToInt16(bytes, 28) / 8,
            (BitConverter.ToInt32(bytes, 18) * (BitConverter.ToInt16(bytes, 28) / 8) + 3) & ~3);
    }

    private static int CountDiff(string aPath, string bPath, int x, int y, int w, int h)
    {
        var a = Load(aPath);
        var b = Load(bPath);
        var n = 0;
        for (var yy = y; yy < y + h; yy++)
        for (var xx = x; xx < x + w; xx++)
            if (a.At(xx, yy) != b.At(xx, yy)) n++;
        return n;
    }
}
