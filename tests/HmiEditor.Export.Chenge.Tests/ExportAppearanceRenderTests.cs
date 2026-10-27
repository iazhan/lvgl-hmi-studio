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
/// 控件外观（底色 / 描边 / 圆角 / 不透明度）的**真机**判据：导出 → 跑宿主运行时 → 量位图。
///
/// <para><b>为什么必须有这条</b>：这三个键由导出器写给**每一个**类型，但运行时的建页
/// 此前只有 label / button / 图元 / 曲线会应用，其余二十多类各自硬编码 —— 面板上改了、
/// 画布与板子都不动，而导出报告与单元测试全绿。这条用"同类控件的多个实例各给一个颜色、
/// 每块矩形里必须取到自己的颜色"把"只认第一个实例 / 按类型共用一个样式"也一起钉死。</para>
///
/// <para>没有 <c>CHENGE_RUNTIME_DIR</c>（宿主二进制）时跳过，与同目录其他渲染判据一致。</para>
/// </summary>
public class ExportAppearanceRenderTests : IDisposable
{
    private readonly string _dir;

    public ExportAppearanceRenderTests()
    {
        _dir = TestTempDir.NewDir("appearance-render");
    }

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    /// <summary>
    /// 同一页、同一类控件的三个实例，各配一个底色：三块矩形的主色必须各自是自己的颜色。
    ///
    /// <para>按类型跑一遍：label / button 是原本就吃 <c>bg_color</c> 的两类（对照组），
    /// checkbox / input / table / gauge 是此前**硬编码外观**、改了底色位图不动的类型 ——
    /// 它们才是这条判据要盯的回归面。</para>
    /// </summary>
    [Theory]
    [InlineData(HmiControlType.Label)]
    [InlineData(HmiControlType.Button)]
    [InlineData(HmiControlType.CheckBox)]
    [InlineData(HmiControlType.TextArea)]
    [InlineData(HmiControlType.Table)]
    [InlineData(HmiControlType.Gauge)]
    public void SameTypeMultipleInstances_EachKeepsItsOwnAppearance(HmiControlType type)
    {
        if (!TryLocateRuntime(out var runtimeDir, out var binary)) return;

        var project = IsolatedProject();
        var colors = new[] { "#FF0000", "#00FF00", "#0000FF" };
        var rects = new (int X, int Y, int W, int H)[colors.Length];

        for (var i = 0; i < colors.Length; i++)
        {
            rects[i] = (20 + i * 250, 120, 220, 180);
            var control = new HmiControl
            {
                Id = "c" + i, Name = "C" + i, Type = type,
                X = rects[i].X, Y = rects[i].Y, Width = rects[i].W, Height = rects[i].H,
                Text = "A", FontSize = 20, TextColor = "#FFFFFF", BgColor = colors[i],
            };
            Decorate(control);
            project.Screens[0].Controls.Add(control);
        }

        var shot = ExportAndShoot(project, _dir, runtimeDir, binary, "instances.bmp");

        for (var i = 0; i < colors.Length; i++)
        {
            var got = DominantColor(shot, rects[i].X, rects[i].Y, rects[i].W, rects[i].H);
            Assert.True(got == colors[i],
                $"{type} 实例 {i}（{colors[i]}）的矩形主色是 {got} —— " +
                "同类控件的多个实例共用了一个外观，或这一类根本不读 bg_color？");
        }
    }

    /// <summary>
    /// 面板上没动过圆角（模型默认 0）的控件，产物里也必须写 <c>radius: 0</c> 与
    /// <c>border_width: 0</c>。
    ///
    /// <para>面板显示的永远是模型值；只在 <c>&gt; 0</c> 时写键就等于"面板说 0、板子给主题
    /// 圆角"（按钮 8、滑块/进度条是圆头），用户会以为设置没生效。判据表是
    /// <see cref="AppearanceSupport.HasBoxBorder"/>。</para>
    /// </summary>
    [Fact]
    public void PanelDefaultRadius_IsWrittenAsZero()
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "s1", Name = "S1", Type = HmiControlType.Slider,
            X = 40, Y = 60, Width = 240, Height = 60,
            MinValue = 0, MaxValue = 100,
        });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var props = ReadWidgetJson(_dir, 1).GetProperty("props");
        Assert.True(props.TryGetProperty("radius", out var radius),
            "面板显示 0 的圆角没写进产物：运行时会把缺键当成主题圆角");
        Assert.Equal(0, radius.GetInt32());
        Assert.Equal(0, props.GetProperty("border_width").GetInt32());
    }

    /// <summary>
    /// 圆角从 30 改成 0：导出产物里必须出现 <c>radius: 0</c>，位图也必须变。
    ///
    /// <para>面板上的「圆角」改成 0 曾经完全不生效：导出器只在 <c>Radius &gt; 0</c> 时写键，
    /// 运行时对缺键就吃控件主题的圆角 —— 用户看到的"改了不动"。</para>
    /// </summary>
    [Fact]
    public void RadiusZero_IsExportedAndChangesTheBitmap()
    {
        if (!TryLocateRuntime(out var runtimeDir, out var binary)) return;
        var rect = (X: 100, Y: 100, W: 240, H: 160);

        var round = ExportAndShoot(WithButton(rect, 30), Path.Combine(_dir, "round"), runtimeDir, binary, "round.bmp");
        var square = ExportAndShoot(WithButton(rect, 0), Path.Combine(_dir, "square"), runtimeDir, binary, "square.bmp");

        // 产物里两个都要有 radius 键（0 也要写），否则运行时无从区分"直角"与"没配"
        var json0 = ReadWidgetJson(Path.Combine(_dir, "square"), 1);
        Assert.True(json0.GetProperty("props").TryGetProperty("radius", out var radius),
            "radius=0 没被导出：运行时会把缺键当成【主题默认圆角】");
        Assert.Equal(0, radius.GetInt32());

        var changed = CountDiff(round, square, rect.X, rect.Y, rect.W, rect.H);
        Assert.True(changed > 200, $"圆角 30→0 只改了 {changed} 个像素（期望 >200，直角会削掉四个角）");
    }

    /// <summary>不透明度：同色同字，一个 100%、一个 40%，位图必须明显不同。</summary>
    [Fact]
    public void Opacity_ReachesTheBitmap()
    {
        if (!TryLocateRuntime(out var runtimeDir, out var binary)) return;
        var rect = (X: 100, Y: 100, W: 240, H: 160);

        var opaque = ExportAndShoot(WithLabel(rect, 100), Path.Combine(_dir, "opaque"), runtimeDir, binary, "opaque.bmp");
        var faded = ExportAndShoot(WithLabel(rect, 40), Path.Combine(_dir, "faded"), runtimeDir, binary, "faded.bmp");

        var changed = CountDiff(opaque, faded, rect.X, rect.Y, rect.W, rect.H);
        Assert.True(changed > 1000, $"不透明度 100→40 只改了 {changed} 个像素（期望 >1000）");
    }

    /// <summary>
    /// 导出产物里的外观键逐类型核对：面板上给了底色/描边/圆角/不透明度，产物里就必须有。
    /// 运行时真的读没读由上面三条像素判据与本仓库 tools/appearance_probe.py 保证。
    /// </summary>
    [Theory]
    [InlineData(HmiControlType.Label, "label")]
    [InlineData(HmiControlType.Button, "button")]
    [InlineData(HmiControlType.Slider, "slider")]
    [InlineData(HmiControlType.Bar, "bar")]
    [InlineData(HmiControlType.Knob, "knob")]
    [InlineData(HmiControlType.Gauge, "gauge")]
    [InlineData(HmiControlType.Spinner, "spinner")]
    [InlineData(HmiControlType.TextArea, "input")]
    [InlineData(HmiControlType.Dropdown, "combobox")]
    [InlineData(HmiControlType.Roller, "roller")]
    [InlineData(HmiControlType.Table, "table")]
    [InlineData(HmiControlType.Message, "message")]
    [InlineData(HmiControlType.EnumText, "enum_text")]
    [InlineData(HmiControlType.AsciiDisplay, "ascii_display")]
    [InlineData(HmiControlType.TabView, "tabview")]
    public void AppearanceKeys_AreWrittenForEveryWidgetType(HmiControlType type, string target)
    {
        var project = IsolatedProject();
        var control = new HmiControl
        {
            Id = "c1", Name = "C1", Type = type,
            X = 40, Y = 60, Width = 240, Height = 160,
            BgColor = "#123456", BorderColor = "#654321", BorderWidth = 5, Radius = 9,
            Opacity = 50,
        };
        Decorate(control);
        project.Screens[0].Controls.Add(control);

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var widget = ReadWidgetJson(_dir, 1);
        Assert.Equal(target, widget.GetProperty("type").GetString());
        var props = widget.GetProperty("props");
        Assert.Equal("#123456", props.GetProperty("bg_color").GetString());
        Assert.Equal("#654321", props.GetProperty("border_color").GetString());
        Assert.Equal(5, props.GetProperty("border_width").GetInt32());
        Assert.Equal(9, props.GetProperty("radius").GetInt32());
        Assert.Equal(128, props.GetProperty("opacity").GetInt32());   // 50% → 128/255
    }

    // ── 夹具 ──────────────────────────────────────────────

    /// <summary>让控件建得起来的最小参数（缺了导出器会跳过该控件，判据就变成空跑）。</summary>
    private static void Decorate(HmiControl control)
    {
        switch (control.Type)
        {
            case HmiControlType.Button: control.Text = "B"; break;
            case HmiControlType.TextArea: control.Text = ""; break;
            case HmiControlType.Dropdown: control.Options = "A\nB"; break;
            case HmiControlType.Roller: control.RollerOptions = "A\nB"; break;
            case HmiControlType.Table:
                control.RowCount = 1;
                control.ColumnCount = 1;
                control.Cells = new List<TableCellData>
                {
                    new() { Row = 0, Col = 0, Text = "a" },
                };
                break;
            case HmiControlType.Message: control.MessageText = "hi"; break;
            case HmiControlType.EnumText: control.EnumTextValue = "on"; break;
            case HmiControlType.TabView: control.TabTitles = "P1"; break;
            case HmiControlType.Gauge:
                control.MinValue = 0;
                control.MaxValue = 100;
                break;
        }
    }

    private static HmiProject IsolatedProject()
    {
        var project = ExportFixture.Build();
        foreach (var c in project.Screens[0].Controls.ToList())
            project.Screens[0].Controls.Remove(c);
        return project;
    }

    private static HmiProject WithButton((int X, int Y, int W, int H) r, int radius)
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "b1", Name = "B1", Type = HmiControlType.Button,
            X = r.X, Y = r.Y, Width = r.W, Height = r.H,
            Text = "OK", FontSize = 20, TextColor = "#FFFFFF", BgColor = "#2F70D0",
            Radius = radius,
        });
        return project;
    }

    private static HmiProject WithLabel((int X, int Y, int W, int H) r, double opacity)
    {
        var project = IsolatedProject();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "l1", Name = "L1", Type = HmiControlType.Label,
            X = r.X, Y = r.Y, Width = r.W, Height = r.H,
            Text = "A", FontSize = 20, TextColor = "#FFFFFF", BgColor = "#FF0000",
            Opacity = opacity,
        });
        return project;
    }

    /// <summary>没有 CHENGE_RUNTIME_DIR（宿主二进制）就跳像素判据，与同目录其他渲染测试一致。</summary>
    private static bool TryLocateRuntime(out string runtimeDir, out string binary)
    {
        runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR") ?? "";
        binary = runtimeDir.Length == 0 ? "" : Path.Combine(runtimeDir, "chenge-runtime");
        return binary.Length > 0 && File.Exists(binary);
    }

    /// <summary>导出到 <paramref name="dir"/> 并跑一次宿主截图，返回 BMP 路径。</summary>
    private static string ExportAndShoot(HmiProject project, string dir, string runtimeDir,
                                         string binary, string shotName)
    {
        Directory.CreateDirectory(dir);
        var result = ExportFixture.Export(project, dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var shot = Path.Combine(dir, shotName);
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

    /// <summary>页面里第 <paramref name="seq"/> 个控件（导出 id 是 w1、w2…）。</summary>
    private static JsonElement ReadWidgetJson(string dir, int seq)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "pages", "home.json")));
        return doc.RootElement.GetProperty("widgets")[seq - 1].Clone();
    }

    // ── BMP 取样 ──────────────────────────────────────────

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

    /// <summary>矩形里出现最多的颜色（%RRGGBB）。避开中心取色：中心常落在文字笔画上。</summary>
    private static string DominantColor(string bmpPath, int x, int y, int w, int h)
    {
        var bmp = Load(bmpPath);
        var counts = new Dictionary<(byte, byte, byte), int>();
        for (var yy = y; yy < y + h; yy++)
        for (var xx = x; xx < x + w; xx++)
        {
            var c = bmp.At(xx, yy);
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        var best = counts.OrderByDescending(kv => kv.Value).First().Key;
        return $"#{best.Item1:X2}{best.Item2:X2}{best.Item3:X2}";
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
