using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HmiEditor.Core;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 旋转落点的**真机**判据：导出后跑宿主运行时，看位图里那块颜色到底画在哪。
///
/// <para><b>为什么必须有这条</b>：运行时的 <c>apply_transform</c> 原先用
/// <c>lv_obj_get_width()</c> 算旋转支点，而建页这一刻对象还没走过布局、<c>coords</c> 可能还是 0
/// ⇒ 支点算成 (0,0)、绕**左上角**转。位图与编辑器里的选中框于是错开半个尺寸，而导出报告、
/// 单元测试全都是绿的。这条用"红块包围盒的中心必须等于控件中心"把它钉死。</para>
/// </summary>
public class ExportTransformRenderTests : IDisposable
{
    private readonly string _dir;

    public ExportTransformRenderTests()
    {
        _dir = TestTempDir.NewDir("rot-render");
    }

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    [Fact]
    public void RotatedLabel_PivotsOnControlCenter()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        var binary = runtimeDir == null ? null : Path.Combine(runtimeDir, "chenge-runtime");
        if (binary == null || !File.Exists(binary))
            return; // 跳过：没有宿主二进制

        const int x = 200, y = 100, w = 200, h = 40;

        var project = ExportFixture.Build();
        var label = project.Screens[0].Controls.First(c => c.Type == HmiControlType.Label);
        label.X = x; label.Y = y; label.Width = w; label.Height = h;
        label.BgColor = "#FF0000";      // 纯红底：截图里能直接量包围盒
        label.Text = "旋转";
        label.Rotation = 90;
        label.RotationDirection = "CW";
        foreach (var other in project.Screens[0].Controls.Where(c => c != label).ToList())
            project.Screens[0].Controls.Remove(other);

        var result = ExportFixture.Export(project, _dir);
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

        using var process = Process.Start(psi)!;
        var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20000), "运行时 20s 未退出");

        var box = RedBox(shot);
        Assert.NotNull(box);
        var (bx0, by0, bx1, by1) = box!.Value;

        var cx = (bx0 + bx1) / 2.0;
        var cy = (by0 + by1) / 2.0;
        Assert.True(Math.Abs(cx - (x + w / 2.0)) <= 2,
            $"红块中心 x={cx} 应等于控件中心 {x + w / 2.0}（旋转支点错了？）\n{log}");
        Assert.True(Math.Abs(cy - (y + h / 2.0)) <= 2,
            $"红块中心 y={cy} 应等于控件中心 {y + h / 2.0}（旋转支点错了？）\n{log}");

        // 转 90° 后宽高互换
        Assert.True(Math.Abs(bx1 - bx0 + 1 - h) <= 2, $"旋转后宽应≈{h}，实际 {bx1 - bx0 + 1}\n{log}");
        Assert.True(Math.Abs(by1 - by0 + 1 - w) <= 2, $"旋转后高应≈{w}，实际 {by1 - by0 + 1}\n{log}");
    }

    /// <summary>截图里"纯红"像素的包围盒（屏幕坐标；BMP 行是自下而上的）。</summary>
    private static (int X0, int Y0, int X1, int Y1)? RedBox(string bmpPath)
    {
        var bytes = File.ReadAllBytes(bmpPath);
        var offset = BitConverter.ToInt32(bytes, 10);
        var width = BitConverter.ToInt32(bytes, 18);
        var height = Math.Abs(BitConverter.ToInt32(bytes, 22));
        var bpp = BitConverter.ToInt16(bytes, 28);
        var px = bpp / 8;
        var rowSize = (width * px + 3) & ~3;

        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var row = 0; row < height; row++)
        for (var col = 0; col < width; col++)
        {
            var p = offset + row * rowSize + col * px;
            // BMP 是 BGR
            if (bytes[p + 2] < 200 || bytes[p + 1] > 60 || bytes[p] > 60) continue;
            var sy = height - 1 - row;
            x0 = Math.Min(x0, col); x1 = Math.Max(x1, col);
            y0 = Math.Min(y0, sy); y1 = Math.Max(y1, sy);
        }

        return x1 < 0 ? null : (x0, y0, x1, y1);
    }
}
