using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HmiEditor.Core;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 垂直对齐的**真机**判据：把 top / middle / bottom 三个 label 放进同一页，用宿主截图
/// 量每块文字实际占的行，再与控件框比。
///
/// <para><b>为什么必须有这条</b>：运行时的 <c>apply_text_valign</c> 曾按**单行**行高算
/// pad_top。文本两行时"居中"实际落在偏下、"底部"把第二行顶出框外（面板上表现为
/// "选底部直接看不到字"）。单行文本一切正常，所以单元测试和肉眼抽查都容易漏。</para>
/// </summary>
public class ExportTextVAlignRenderTests : IDisposable
{
    private readonly string _dir;

    public ExportTextVAlignRenderTests()
    {
        _dir = TestTempDir.NewDir("valign-render");
    }

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    [Fact]
    public void LabelVAlign_PlacesTextBlockByItsRealHeight()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        var binary = runtimeDir == null ? null : Path.Combine(runtimeDir, "chenge-runtime");
        if (binary == null || !File.Exists(binary))
            return; // 跳过：没有宿主二进制

        const int boxH = 60;
        var aligns = new[] { TextVAlignMode.Top, TextVAlignMode.Middle, TextVAlignMode.Bottom };

        var project = ExportFixture.Build();
        foreach (var c in project.Screens[0].Controls.ToList())
            project.Screens[0].Controls.Remove(c);

        for (var i = 0; i < aligns.Length; i++)
            project.Screens[0].Controls.Add(new HmiControl
            {
                Id = "l" + i, Name = "L" + i, Type = HmiControlType.Label,
                X = 100, Y = 100 + i * 100, Width = 300, Height = boxH,
                // 两行：单行看不出这个缺陷
                Text = "AG\nCD", FontSize = 20, TextColor = "#FFFFFF", TextVAlign = aligns[i],
            });

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

        for (var i = 0; i < aligns.Length; i++)
        {
            var boxTop = 100 + i * 100;
            var rows = WhiteRows(shot, boxTop, boxTop + boxH, 90, 420);
            Assert.True(rows.Count > 0, $"{aligns[i]}: 框内一个字都没画出来\n{log}");

            var textTop = rows.First();
            var textBottom = rows.Last();
            var textCenter = (textTop + textBottom) / 2.0;
            var boxCenter = boxTop + boxH / 2.0;

            switch (aligns[i])
            {
                case TextVAlignMode.Top:
                    Assert.True(textTop - boxTop <= 8,
                        $"top: 文字首行 {textTop} 应贴着框顶 {boxTop}\n{log}");
                    break;
                case TextVAlignMode.Middle:
                    Assert.True(Math.Abs(textCenter - boxCenter) <= 4,
                        $"middle: 文字中心 {textCenter} 应≈框中心 {boxCenter}（多行时按单行行高算 pad 就会偏）\n{log}");
                    break;
                case TextVAlignMode.Bottom:
                    Assert.True(boxTop + boxH - textBottom <= 10,
                        $"bottom: 文字末行 {textBottom} 应贴框底 {boxTop + boxH}（越界 = 被裁掉）\n{log}");
                    break;
            }
        }
    }

    /// <summary>
    /// 带边框的标签：垂直对齐要按**内容区**算，不是整框。
    ///
    /// <para>55 高、边框 10 的标签内容区只剩 35px。按整框算 pad 的话："居中"看起来贴底、
    /// "底部"把文字顶出可视区 —— 工程里就是这组参数（275×55 + border_width 10）。</para>
    /// </summary>
    [Fact]
    public void LabelVAlign_RespectsBorderInsets()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        var binary = runtimeDir == null ? null : Path.Combine(runtimeDir, "chenge-runtime");
        if (binary == null || !File.Exists(binary))
            return;

        const int boxH = 55, border = 10;
        var aligns = new[] { TextVAlignMode.Top, TextVAlignMode.Middle, TextVAlignMode.Bottom };

        var project = ExportFixture.Build();
        foreach (var c in project.Screens[0].Controls.ToList())
            project.Screens[0].Controls.Remove(c);

        for (var i = 0; i < aligns.Length; i++)
            project.Screens[0].Controls.Add(new HmiControl
            {
                Id = "bd" + i, Name = "BD" + i, Type = HmiControlType.Label,
                X = 40, Y = 80 + i * 75, Width = 275, Height = boxH,
                Text = "111", FontSize = 14,
                TextColor = "#FFFFFF", TextAlign = TextAlignMode.Center, TextVAlign = aligns[i],
                BgColor = "#EF4444", BorderColor = "#22C55E", BorderWidth = border,
                LabelLetterSpacing = 10,
            });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var shot = Path.Combine(_dir, "border-shot.bmp");
        var psi = new ProcessStartInfo(binary)
        {
            WorkingDirectory = runtimeDir!, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.Environment["SDL_VIDEODRIVER"] = "dummy";
        psi.Environment["HMI_PROJECT_DIR"] = _dir;
        psi.Environment["HMI_DATA_DIR"] = Path.Combine(_dir, "data");
        psi.Environment["HMI_SHOT"] = shot;
        psi.Environment["HMI_RT_TRACE"] = "1";

        using var process = Process.Start(psi)!;
        var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20000), "运行时 20s 未退出");

        for (var i = 0; i < aligns.Length; i++)
        {
            var boxTop = 80 + i * 75;
            var rows = WhiteRows(shot, boxTop, boxTop + boxH, 40, 315);
            Assert.True(rows.Count > 0, $"{aligns[i]}: 框内没有文字（被边框顶出去了？）\n{log}");

            var innerTop = boxTop + border;
            var innerBottom = boxTop + boxH - border;

            switch (aligns[i])
            {
                case TextVAlignMode.Top:
                    Assert.True(rows.First() - innerTop <= 8,
                        $"top: 文字首行 {rows.First()} 应贴内容区顶 {innerTop}\n{log}");
                    break;
                case TextVAlignMode.Middle:
                    var center = (rows.First() + rows.Last()) / 2.0;
                    var innerCenter = (innerTop + innerBottom) / 2.0;
                    Assert.True(Math.Abs(center - innerCenter) <= 4,
                        $"middle: 文字中心 {center} 应≈内容区中心 {innerCenter}（按整框算 pad 就会偏到底部）\n{log}");
                    break;
                case TextVAlignMode.Bottom:
                    Assert.True(innerBottom - rows.Last() <= 8,
                        $"bottom: 文字末行 {rows.Last()} 应贴内容区底 {innerBottom}（越界 = 被裁掉）\n{log}");
                    break;
            }
        }
    }

    /// <summary>指定横向区间内、纵向区间里出现过"接近纯白"像素的屏幕行号。</summary>
    private static List<int> WhiteRows(string bmpPath, int top, int bottom, int left, int right)
    {
        var bytes = File.ReadAllBytes(bmpPath);
        var offset = BitConverter.ToInt32(bytes, 10);
        var width = BitConverter.ToInt32(bytes, 18);
        var height = Math.Abs(BitConverter.ToInt32(bytes, 22));
        var px = BitConverter.ToInt16(bytes, 28) / 8;
        var rowSize = (width * px + 3) & ~3;

        var rows = new List<int>();
        for (var sy = top; sy < bottom && sy < height; sy++)
        {
            var fileRow = height - 1 - sy;   // BMP 自下而上
            for (var x = left; x < right && x < width; x++)
            {
                var i = offset + fileRow * rowSize + x * px;
                if (bytes[i] > 200 && bytes[i + 1] > 200 && bytes[i + 2] > 200)
                {
                    rows.Add(sy);
                    break;
                }
            }
        }
        return rows;
    }
}
