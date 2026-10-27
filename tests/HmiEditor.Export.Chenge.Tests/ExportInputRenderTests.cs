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
/// 输入框（TextArea → <c>input</c>）的**高度要跟模型一致**。
///
/// <para><b>为什么必须有这条</b>：<c>build_input</c> 原来先 <c>place()</c> 再
/// <c>lv_textarea_set_one_line(o, true)</c>，而 LVGL 的 <c>set_one_line</c> 内部会把对象高度设成
/// <c>LV_SIZE_CONTENT</c>（见 <c>lv_textarea.c</c> 的 set_one_line）—— 于是编辑器里拖高度，
/// 位图纹丝不动、只有选中框在变。</para>
/// </summary>
public class ExportInputRenderTests : IDisposable
{
    private readonly string _dir;

    public ExportInputRenderTests()
    {
        _dir = TestTempDir.NewDir("input-render");
    }

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    [Fact]
    public void InputHeight_FollowsModelHeight()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        var binary = runtimeDir == null ? null : Path.Combine(runtimeDir, "chenge-runtime");
        if (binary == null || !File.Exists(binary))
            return; // 跳过：没有宿主二进制

        var heights = new[] { 30, 80 };

        var project = ExportFixture.Build();
        foreach (var c in project.Screens[0].Controls.ToList())
            project.Screens[0].Controls.Remove(c);

        for (var i = 0; i < heights.Length; i++)
            project.Screens[0].Controls.Add(new HmiControl
            {
                Id = "i" + i, Name = "I" + i, Type = HmiControlType.TextArea,
                X = 100, Y = 100 + i * 120, Width = 240, Height = heights[i], FontSize = 14,
                // 输入框的出厂外观带 1px 边框（见 AppearanceDefaults）；判据靠"比底色亮"
                // 找框的行，没有边框就只剩下文字那几行可数
                BorderWidth = 1,
            });

        var result = ExportFixture.Export(project, _dir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var shot = Path.Combine(_dir, "shot.bmp");
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

        for (var i = 0; i < heights.Length; i++)
        {
            var top = 100 + i * 120;
            var rows = BoxRows(shot, top - 10, top + heights[i] + 10, 100, 340);
            Assert.True(rows.Count > 0, $"h={heights[i]}: 位图里找不到输入框\n{log}");

            var painted = rows.Last() - rows.First() + 1;
            Assert.True(Math.Abs(painted - heights[i]) <= 2,
                $"h={heights[i]} 的输入框在位图里只有 {painted} 行（{rows.First()}..{rows.Last()}）—— "
                + "高度没生效（set_one_line 会把高度重置成 LV_SIZE_CONTENT）\n" + log);
        }
    }

    /// <summary>横向区间内"比页面底色亮"的行（输入框底色/边框因此可辨）。</summary>
    private static List<int> BoxRows(string bmpPath, int top, int bottom, int left, int right)
    {
        var bytes = File.ReadAllBytes(bmpPath);
        var offset = BitConverter.ToInt32(bytes, 10);
        var width = BitConverter.ToInt32(bytes, 18);
        var height = Math.Abs(BitConverter.ToInt32(bytes, 22));
        var px = BitConverter.ToInt16(bytes, 28) / 8;
        var rowSize = (width * px + 3) & ~3;

        var rows = new List<int>();
        for (var sy = Math.Max(0, top); sy < bottom && sy < height; sy++)
        {
            var fileRow = height - 1 - sy;
            for (var x = left; x < right && x < width; x++)
            {
                var i = offset + fileRow * rowSize + x * px;
                if (bytes[i] + bytes[i + 1] + bytes[i + 2] > 130) { rows.Add(sy); break; }
            }
        }
        return rows;
    }
}
