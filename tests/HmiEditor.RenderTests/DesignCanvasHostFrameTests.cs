using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HmiEditor.App.Controls;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.RenderTests;

/// <summary>
/// 「画布底图 = 宿主位图」的像素级判据（docs/chenge-rt/49 §4 S1）。
///
/// <para>为什么必须在这里而不是 UiTests：那边是 headless 绘制（no-op），
/// <c>RenderTargetBitmap.CopyPixels</c> 读回的是未初始化内存，断言等于对噪声断言。</para>
///
/// <para>判据只证一件事：<c>UseHostFrame=true</c> 时画布画的是位图本身，
/// 关掉时回到 C# 自绘（位图颜色不出现）。不涉及缩放与叠加层 —— 那是 S2 的事。</para>
/// </summary>
public class DesignCanvasHostFrameTests
{
    private const int W = 800, H = 480;

    // 位图填这个颜色（BGRA 排列在下面拼），选一个不会与画布默认背景撞色的值
    private const byte FrameB = 0x10, FrameG = 0x88, FrameR = 0xE0;

    private static WriteableBitmap SolidFrame()
    {
        var bmp = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var bytes = new byte[4 * 4 * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = FrameB;
            bytes[i + 1] = FrameG;
            bytes[i + 2] = FrameR;
            bytes[i + 3] = 0xFF;
        }
        using (var fb = bmp.Lock())
            Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
        return bmp;
    }

    private static DesignCanvas BuildCanvas()
    {
        var canvas = new DesignCanvas
        {
            Screen = new HmiScreen { Name = "home", Width = W, Height = H },
            ZoomLevel = 1.0,
        };
        canvas.Measure(new Size(W, H));
        canvas.Arrange(new Rect(0, 0, W, H));
        return canvas;
    }

    private static byte[] Render(DesignCanvas canvas)
    {
        using var target = new RenderTargetBitmap(new PixelSize(W, H), new Vector(96, 96));
        target.Render(canvas);
        return PixelReader.ReadAll(target);
    }

    [Fact]
    public void UseHostFrame_DrawsTheHostBitmap()
    {
        RenderTestHost.Run(() =>
        {
            var canvas = BuildCanvas();
            canvas.HostFrame = SolidFrame();
            canvas.UseHostFrame = true;

            var pixels = Render(canvas);
            var (b, g, r, a) = PixelReader.At(pixels, W, W / 2, H / 2);

            Assert.Equal(0xFF, a);
            Assert.Equal(FrameR, r);
            Assert.Equal(FrameG, g);
            Assert.Equal(FrameB, b);
        });
    }

    /// <summary>
    /// 页面上有**表格**时，真机位图必须照样画出来（不是被 C# 自绘的占位层盖住、
    /// 也不是这一帧直接没画）。设计画布"拖一个表格就白屏、按钮却正常"就发生在这里：
    /// 宿主交给画布的位图是对的，画布自己那一帧画崩了。
    /// </summary>
    [Fact]
    public void UseHostFrame_StillShowsWithTableOnPage()
    {
        RenderTestHost.Run(() =>
        {
            var canvas = BuildCanvas();
            canvas.Screen!.Controls.Add(new HmiControl
            {
                Id = "t1", Name = "Table1", Type = HmiControlType.Table,
                X = 40, Y = 60, Width = 300, Height = 200,
            });
            canvas.HostFrame = SolidFrame();
            canvas.UseHostFrame = true;

            var pixels = Render(canvas);
            // 取表格之外的空白处：位图必须还在
            var (b, g, r, a) = PixelReader.At(pixels, W, 10, 10);

            Assert.Equal(0xFF, a);
            Assert.Equal(FrameR, r);
            Assert.Equal(FrameG, g);
            Assert.Equal(FrameB, b);
        });
    }

    [Fact]
    public void UseHostFrameOff_FallsBackToLocalRendering()
    {
        RenderTestHost.Run(() =>
        {
            var canvas = BuildCanvas();
            canvas.HostFrame = SolidFrame();
            canvas.UseHostFrame = false;

            var pixels = Render(canvas);
            var (b, g, r, _) = PixelReader.At(pixels, W, W / 2, H / 2);

            // 回到 C# 自绘：空白页就是画布底色，不会出现位图那个颜色
            Assert.False(r == FrameR && g == FrameG && b == FrameB,
                $"关掉 UseHostFrame 后仍在画宿主位图：R={r} G={g} B={b}");
        });
    }
}
