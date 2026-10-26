using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using HmiEditor.App.Controls;
using HmiEditor.App.Views;
using HmiEditor.Core;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 画布的「容器放子控件」交互：命中测试、拖进/拖出容器、撤销、删除，全部用
/// headless 指针序列驱动（<c>MouseDown/Move/Up</c> + Pump），不靠截图 ——
/// 拖拽方向与手感只有真走一遍输入管线才验得出来。
/// </summary>
public class DesignCanvasContainerTests
{
    private static HmiControl Container(string id, double x, double y, double w, double h)
        => new HmiControl
        {
            Id = id, Name = id, Type = HmiControlType.Shape, ShapeKind = "container",
            X = x, Y = y, Width = w, Height = h,
            BgColor = "#1F2937", BorderColor = "#4A5A6A", BorderWidth = 1,
        };

    private static HmiControl Label(string id, double x, double y)
        => new HmiControl
        {
            Id = id, Name = id, Type = HmiControlType.Label, Text = id,
            X = x, Y = y, Width = 100, Height = 30,
        };

    /// <summary>
    /// 夹具：屏幕 800×480；容器 (100,100) 200×150（根）；
    /// 子标签在容器内（绝对 (130,140) → 相对 (30,40)）；根标签在 (400,120)。
    /// <para>⚠ <see cref="HmiScreen.BgColor"/> 必须给：headless 的命中测试走的是
    /// **渲染场景**（按像素），画布不画背景的地方（含标签字形之间）点不中，
    /// 事件会落到 SnapGrid 或窗口装饰层。给了底色整屏才可命中。</para>
    /// </summary>
    private static (HmiScreen Screen, HmiControl Cont, HmiControl Kid, HmiControl Root) BuildScreen()
    {
        var screen = new HmiScreen { Name = "home", Width = 800, Height = 480, BgColor = "#101820" };
        var cont = Container("cont", 100, 100, 200, 150);
        var kid = Label("kid", 130, 140);
        var root = Label("root", 400, 120);
        screen.Controls.Add(cont);
        screen.Controls.Add(root);
        ContainerHierarchy.AttachAt(cont, kid, ContainerHierarchy.Index(screen));
        return (screen, cont, kid, root);
    }

    /// <summary>装配画布 + 窗口（点击必须落在 800×480 屏幕内，见 <see cref="BuildScreen"/>）。</summary>
    private static (DesignCanvas Canvas, Window Window) Host(HmiScreen screen)
    {
        var view = new DesignCanvasView();
        view.SetScreen(screen);
        view.Canvas.ZoomLevel = 1.0;
        var window = new Window { Content = view, Width = 900, Height = 650 };
        window.Show();
        UiTestHost.Pump(2);
        // 吸附会把落点中心吸到栅格（98 → 100，差 2px），本节判的是坐标换算与层级。
        // 必须在挂到可视树**之后**设：DesignCanvasView 挂载时按编辑器偏好重设过一次。
        view.Canvas.SnapEnabled = false;
        return (view.Canvas, window);
    }

    /// <summary>在画布局部坐标上按一下（移动 → 按下 → 抬起，逐步 Pump）。</summary>
    private static void Tap(DesignCanvas canvas, double x, double y)
    {
        var root = TopLevel.GetTopLevel(canvas)
            ?? throw new InvalidOperationException("画布不在可视树里");
        var p = canvas.TranslatePoint(new Point(x, y), root) ?? new Point(x, y);
        root.MouseMove(p);
        UiTestHost.Pump(2);
        root.MouseDown(p, MouseButton.Left);
        UiTestHost.Pump(2);
        root.MouseUp(p, MouseButton.Left);
        UiTestHost.Pump(2);
    }

    /// <summary>从 from 拖到 to（按下 → 中点 → 终点 → 抬起，逐步 Pump）。</summary>
    private static void Drag(DesignCanvas canvas, (double X, double Y) from, (double X, double Y) to)
    {
        var root = TopLevel.GetTopLevel(canvas)
            ?? throw new InvalidOperationException("画布不在可视树里");
        var path = new[]
        {
            (from.X, from.Y, 0),                                  // 按下
            ((from.X + to.X) / 2, (from.Y + to.Y) / 2, 1),        // 移动
            (to.X, to.Y, 1),                                      // 终点
            (to.X, to.Y, 2),                                      // 抬起
        };
        foreach (var (x, y, phase) in path)
        {
            var p = canvas.TranslatePoint(new Point(x, y), root) ?? new Point(x, y);
            switch (phase)
            {
                case 0: root.MouseDown(p, MouseButton.Left); break;
                case 1: root.MouseMove(p); break;
                default: root.MouseUp(p, MouseButton.Left); break;
            }
            UiTestHost.Pump(2);
        }
    }

    [Fact]
    public void PointerSequence_DragRootIntoContainer_DragsChildOut_AndUndoRestores()
    {
        UiTestHost.Run(() =>
        {
            var (screen, cont, kid, root) = BuildScreen();
            var (canvas, window) = Host(screen);

            // ── ① 点子控件：选中的是子控件本身（它在容器上层，先问孩子）──
            Tap(canvas, 180, 155);   // 子标签绝对中心 (130+50, 140+15)
            Assert.Equal(new[] { kid.Id }, canvas.SelectedControls.Select(c => c.Id).ToArray());

            // ── ② 拖根标签进容器：落点中心在容器内 → 挂成子控件，坐标改相对 ──
            Tap(canvas, 450, 135);   // 根标签中心：选中
            Assert.Equal(new[] { root.Id }, canvas.SelectedControls.Select(c => c.Id).ToArray());
            Drag(canvas, (450, 135), (250, 215));   // 拖到容器里（新绝对中心 (250,215)）

            Assert.Equal(cont.Id, root.ParentId);
            Assert.Equal(2, cont.Children.Count);   // 原有的 kid + 拖进来的 root
            Assert.Contains(root, cont.Children);
            Assert.Equal(100, root.X);       // 绝对 (200,200) − 容器 (100,100)
            Assert.Equal(100, root.Y);
            Assert.DoesNotContain(root, screen.Controls);   // 子控件不进平铺列表

            // ── ③ 拖子控件出容器：整体出界 → 移出，坐标还原为绝对 ──
            Tap(canvas, 180, 155);           // 重新选中子标签
            Drag(canvas, (180, 155), (500, 300));   // 拖到容器外

            Assert.Null(kid.ParentId);
            Assert.DoesNotContain(kid, cont.Children);
            Assert.Contains(root, cont.Children);   // root 还留在容器里
            Assert.Contains(kid, screen.Controls);
            Assert.True(kid.X >= 400, $"拖出后 X 应是绝对坐标，实际 {kid.X}");
            Assert.True(kid.Y >= 250, $"拖出后 Y 应是绝对坐标，实际 {kid.Y}");

            // ── ④ 撤销：子控件回到容器里，相对坐标归位 ──
            canvas.UndoRedoManager.Undo();
            Assert.Equal(cont.Id, kid.ParentId);
            Assert.Equal(2, cont.Children.Count);   // kid 回来，与 root 同住
            Assert.Contains(kid, cont.Children);
            Assert.Equal(30, kid.X);
            Assert.Equal(40, kid.Y);
            Assert.DoesNotContain(kid, screen.Controls);

            // ── ⑤ 再撤销一次：根标签退回顶层（② 的逆操作）──
            canvas.UndoRedoManager.Undo();
            Assert.Null(root.ParentId);
            Assert.Equal(new[] { kid }, cont.Children.ToArray());   // 只剩原有的 kid
            Assert.Contains(root, screen.Controls);

            window.Close();
        });
    }

    [Fact]
    public void AddControlTo_AttachesChildWithRelativeCoordinates()
    {
        UiTestHost.Run(() =>
        {
            var (screen, cont, kid, root) = BuildScreen();
            var (canvas, window) = Host(screen);

            // 以容器内 (150,120)（屏幕绝对）为中心放一个按钮
            var added = canvas.AddControlTo(HmiControlType.Button, 150, 120, cont);

            Assert.Equal(cont.Id, added.ParentId);
            Assert.DoesNotContain(added, screen.Controls);
            Assert.Contains(added, cont.Children);
            // 落下点的绝对中心仍是 (150,120)：容器原点 + 相对坐标 + 半个控件
            Assert.Equal(150, cont.X + added.X + added.Width / 2);
            Assert.Equal(120, cont.Y + added.Y + added.Height / 2);

            window.Close();
        });
    }

    [Fact]
    public void DeleteControlsWithHistory_RemovesChild_AndUndoRestoresIt()
    {
        UiTestHost.Run(() =>
        {
            var (screen, cont, kid, root) = BuildScreen();
            var (canvas, window) = Host(screen);

            canvas.SelectAllControls();
            Assert.True(canvas.DeleteControlsWithHistory(new[] { kid }));

            Assert.Empty(cont.Children);
            Assert.DoesNotContain(kid, screen.Controls);

            canvas.UndoRedoManager.Undo();
            Assert.Single(cont.Children);
            Assert.Equal(kid, cont.Children[0]);
            Assert.Equal(30, cont.Children[0].X);
            Assert.Equal(40, cont.Children[0].Y);

            window.Close();
        });
    }

    [Fact]
    public void Render_ContainerTree_DoesNotThrow()
    {
        UiTestHost.Run(() =>
        {
            var (screen, cont, kid, root) = BuildScreen();
            var canvas = new DesignCanvas { Screen = screen, ZoomLevel = 1.0 };

            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize(800, 480));
            using var dc = rtb.CreateDrawingContext();
            canvas.Render(dc);   // 递归渲染容器子树，不应抛
        });
    }
}
