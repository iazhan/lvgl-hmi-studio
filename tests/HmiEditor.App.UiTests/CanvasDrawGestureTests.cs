using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using HmiEditor.App.Controls;
using HmiEditor.App.Views;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 画布左键的**手势判定表**：全部用 headless 真实指针序列驱动（MouseDown/Move/Up + Pump），
/// 不靠截图 —— "拖到哪、点了几个点"只有真走一遍输入管线才验得出来。
///
/// <para>判据覆盖的语义（用户视角的一句话版本）：</para>
/// <list type="number">
///   <item>选了工具 → 左键只画：拖框定尺寸 / 单击放默认尺寸 / 按点图元逐点画；</item>
///   <item>没选工具 → 空白起拖拉选框、点控件拖动、点顶点只拖这个点；</item>
///   <item>取消工具三条路（ESC、右键、再点同一工具项）都不留半成品；</item>
///   <item>切工具 / 切页面 / 离开画布都**不允许**就地落控件。</item>
/// </list>
/// </summary>
public class CanvasDrawGestureTests
{
    private static HmiScreen NewScreen() => new()
    {
        Name = "gesture", Width = 800, Height = 480, BgColor = "#101820",
    };

    /// <summary>装配画布 + 窗口（点击坐标 = 800×480 屏幕模型坐标，Zoom=1）。</summary>
    private static (DesignCanvas Canvas, Window Window) Host(HmiScreen screen)
    {
        DockLayoutState.SetDrawTool(null);       // 静态工具状态可能被别的判据留下
        var view = new DesignCanvasView();
        view.SetScreen(screen);
        view.Canvas.ZoomLevel = 1.0;
        var window = new Window { Content = view, Width = 900, Height = 650 };
        window.Show();
        UiTestHost.Pump(2);
        // 必须在挂到可视树之后关吸附：View 挂载时按编辑器偏好重设过一次
        view.Canvas.SnapEnabled = false;
        return (view.Canvas, window);
    }

    private static void Close(Window window)
    {
        DockLayoutState.SetDrawTool(null);
        window.Close();
        UiTestHost.Pump(1);
    }

    private static void Tap(DesignCanvas canvas, double x, double y)
    {
        var root = TopLevel.GetTopLevel(canvas) ?? throw new InvalidOperationException("画布不在可视树里");
        var p = canvas.TranslatePoint(new Point(x, y), root) ?? new Point(x, y);
        root.MouseMove(p);
        UiTestHost.Pump(1);
        root.MouseDown(p, MouseButton.Left);
        UiTestHost.Pump(1);
        root.MouseUp(p, MouseButton.Left);
        UiTestHost.Pump(1);
    }

    private static void Drag(DesignCanvas canvas, (double X, double Y) from, (double X, double Y) to,
        RawInputModifiers mods = RawInputModifiers.None)
    {
        var root = TopLevel.GetTopLevel(canvas) ?? throw new InvalidOperationException("画布不在可视树里");
        foreach (var (x, y, phase) in new[]
                 {
                     (from.X, from.Y, 0),
                     ((from.X + to.X) / 2, (from.Y + to.Y) / 2, 1),
                     (to.X, to.Y, 1),
                     (to.X, to.Y, 2),
                 })
        {
            var p = canvas.TranslatePoint(new Point(x, y), root) ?? new Point(x, y);
            switch (phase)
            {
                case 0: root.MouseDown(p, MouseButton.Left, mods | RawInputModifiers.LeftMouseButton); break;
                case 1: root.MouseMove(p, mods); break;
                default: root.MouseUp(p, MouseButton.Left, mods); break;
            }
            UiTestHost.Pump(1);
        }
    }

    /// <summary>带修饰键的单击（Shift = 正交 / 对齐约束）。</summary>
    private static void TapWith(DesignCanvas canvas, double x, double y, RawInputModifiers mods)
    {
        var root = TopLevel.GetTopLevel(canvas) ?? throw new InvalidOperationException("画布不在可视树里");
        var p = canvas.TranslatePoint(new Point(x, y), root) ?? new Point(x, y);
        root.MouseMove(p, mods);
        UiTestHost.Pump(1);
        root.MouseDown(p, MouseButton.Left, mods | RawInputModifiers.LeftMouseButton);
        UiTestHost.Pump(1);
        root.MouseUp(p, MouseButton.Left, mods);
        UiTestHost.Pump(1);
    }

    private static void RightClick(DesignCanvas canvas, double x, double y)
    {
        var root = TopLevel.GetTopLevel(canvas) ?? throw new InvalidOperationException("画布不在可视树里");
        var p = canvas.TranslatePoint(new Point(x, y), root) ?? new Point(x, y);
        root.MouseDown(p, MouseButton.Right);
        UiTestHost.Pump(1);
        root.MouseUp(p, MouseButton.Right);
        UiTestHost.Pump(1);
    }

    private static HmiControl? Last(DesignCanvas canvas)
        => canvas.Screen?.Controls.Count > 0 ? canvas.Screen.Controls[^1] : null;

    // ── ① 选了工具 → 左键只画 ──

    [Fact]
    public void ArmedBoxTool_DragCreatesControlWithDraggedBox()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Widget(HmiControlType.Button));
                Drag(canvas, (100, 100), (240, 160));

                var c = Assert.Single(screen.Controls);
                Assert.Equal(HmiControlType.Button, c.Type);
                Assert.Equal(100, c.X, 0);
                Assert.Equal(100, c.Y, 0);
                Assert.Equal(140, c.Width, 0);
                Assert.Equal(60, c.Height, 0);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void ArmedBoxTool_ClickPlacesDefaultSizeAtClickPoint()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Widget(HmiControlType.Button));
                Tap(canvas, 300, 200);

                var c = Assert.Single(screen.Controls);
                Assert.Equal(HmiControlType.Button, c.Type);
                // 默认尺寸以点击处为中心：120×44 ⇒ x = 300-60 = 240
                Assert.Equal(240, c.X, 0);
                Assert.Equal(178, c.Y, 0);
                Assert.Equal(120, c.Width, 0);
                Assert.Equal(44, c.Height, 0);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void ArmedLineTool_TwoClicksMakeARealSegment()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Widget(HmiControlType.Line));
                Tap(canvas, 100, 100);
                Assert.Empty(screen.Controls);        // 第一点只起预览，不落控件

                Tap(canvas, 300, 160);

                var c = Assert.Single(screen.Controls);
                Assert.Equal(HmiControlType.Line, c.Type);
                Assert.NotNull(c.Points);
                Assert.True(c.Points!.Count == 2, $"直线应存 2 个端点，实际 {c.Points.Count}");
                // 两点式：框 = 两点包围盒，点落在框的两个角上
                Assert.Equal(100, c.X, 0);
                Assert.Equal(100, c.Y, 0);
                Assert.Equal(200, c.Width, 0);
                Assert.Equal(60, c.Height, 0);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void ArmedPolylineTool_AppendsVertices_RightClickCommits()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Figure("polyline"));
                Tap(canvas, 100, 100);
                Tap(canvas, 200, 200);
                Tap(canvas, 300, 100);
                Assert.Empty(screen.Controls);

                RightClick(canvas, 300, 100);          // 右键 = 结束并提交

                var c = Assert.Single(screen.Controls);
                Assert.Equal(HmiControlType.Shape, c.Type);
                Assert.Equal("polyline", c.ShapeKind);
                Assert.True(c.Points!.Count == 3, $"折线应存 3 个顶点，实际 {c.Points!.Count}");
                Assert.Null(DockLayoutState.ActiveDrawTool);   // 右键同时取消工具
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void ArmedPolylineTool_SinglePointThenEscapeLeavesNothing()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Figure("polyline"));
                Tap(canvas, 120, 120);

                // ESC 走的就是这条入口（MainWindow 的 CancelGesture 分支）
                Assert.True(canvas.FinishToolGesture());
                canvas.AbortToolGesture();

                Assert.Empty(screen.Controls);          // 不足 2 点 → 放弃，不留退化形状
                Assert.False(canvas.HasToolGesture);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void ArmedArcTool_TwoClicksMakeSemicircleWhoseHandlesHitTheClicks()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Figure("arc"));
                Tap(canvas, 100, 200);
                Tap(canvas, 300, 200);

                var c = Assert.Single(screen.Controls);
                // 两点当直径：圆心 (200,200)、半径 100 ⇒ 框 (100,100) 200×200
                Assert.Equal(100, c.X, 0);
                Assert.Equal(100, c.Y, 0);
                Assert.Equal(200, c.Width, 0);
                Assert.Equal(200, c.Height, 0);
                Assert.Equal(180, c.MinValue, 0);      // 起点 = 第一个点的角度
                Assert.Equal(360, c.MaxValue, 0);

                // 两个拖拽手柄要落在用户点的位置上（不跳）
                var anchors = FigureGeometry.Anchors(c);
                Assert.InRange(Math.Abs(anchors[0].X - 100), 0, 1.5);
                Assert.InRange(Math.Abs(anchors[0].Y - 200), 0, 1.5);
                Assert.InRange(Math.Abs(anchors[1].X - 300), 0, 1.5);
                Assert.InRange(Math.Abs(anchors[1].Y - 200), 0, 1.5);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void ArmedElbowPipe_ThreeClicks_StoreThreePointPath()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Pipe(FigurePointMode.ThreePointCorner));
                Tap(canvas, 100, 300);      // 起点
                Tap(canvas, 100, 200);      // 拐点
                Tap(canvas, 260, 200);      // 终点

                var c = Assert.Single(screen.Controls);
                Assert.Equal(HmiControlType.Pipe, c.Type);
                Assert.True(c.Points!.Count == 3, $"弯管应存 3 点，实际 {c.Points!.Count}");
                // 管道框 = 路径包围盒 + 四边各留半个管径（默认管径 12 ⇒ 6）：
                // 包围盒 160×100，框 172×112 ⇒ 运行时内缩半个管径也是 6，点集映射是恒等
                Assert.Equal(94, c.X, 0);          // 100 - 6
                Assert.Equal(194, c.Y, 0);         // 200 - 6
                Assert.Equal(172, c.Width, 0);     // 160 + 12
                Assert.Equal(112, c.Height, 0);    // 100 + 12
            }
            finally { Close(window); }
        });
    }

    // ── ② 没选工具 → 选择/拖动/拖顶点 ──

    private static void AddExistingBox(HmiScreen screen, string id = "btn",
        double x = 100, double y = 100, double w = 140, double h = 60)
        => screen.Controls.Add(new HmiControl
        {
            Id = id, Name = id, Type = HmiControlType.Button, Text = id,
            X = x, Y = y, Width = w, Height = h,
        });

    [Fact]
    public void NoTool_BlankDrag_IsMarqueeSelect_NotCreate()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            AddExistingBox(screen);
            var (canvas, window) = Host(screen);
            try
            {
                Drag(canvas, (20, 20), (400, 300));      // 空白起拖 → 框选

                Assert.Single(screen.Controls);          // 没有新控件
                Assert.Equal(new[] { "btn" }, canvas.SelectedControls.Select(c => c.Id).ToArray());
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void NoTool_BlankClick_ClearsSelection()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            AddExistingBox(screen);
            var (canvas, window) = Host(screen);
            try
            {
                Tap(canvas, 170, 130);                   // 点控件 → 选中
                Assert.Single(canvas.SelectedControls);

                Tap(canvas, 600, 400);                   // 点空白 → 清选中
                Assert.Empty(canvas.SelectedControls);
                Assert.Single(screen.Controls);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void NoTool_DragOnControl_MovesWholeControl_VerticesUntouched()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            AddExistingBox(screen);
            var (canvas, window) = Host(screen);
            try
            {
                Drag(canvas, (170, 130), (270, 130));    // 在控件中部起拖 → 整体平移

                var c = Assert.Single(screen.Controls);
                Assert.Equal(200, c.X, 0);
                Assert.Equal(100, c.Y, 0);
            }
            finally { Close(window); }
        });
    }

    /// <summary>一条三点折线：(100,100) → (200,200) → (300,100)，框 200×100。</summary>
    private static HmiControl AddPolyline(HmiScreen screen)
    {
        var c = new HmiControl
        {
            Id = "poly", Name = "poly", Type = HmiControlType.Polyline,
            X = 100, Y = 100, Width = 200, Height = 100,
            Points = new System.Collections.Generic.List<PointData>
            {
                new(0, 0), new(100, 100), new(200, 0),
            },
        };
        screen.Controls.Add(c);
        return c;
    }

    [Fact]
    public void NoTool_DragOnVertex_MovesOnlyThatVertex()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var poly = AddPolyline(screen);
            var (canvas, window) = Host(screen);
            try
            {
                Tap(canvas, 200, 200);                   // 先在折线上点一下（选中）
                Assert.Single(canvas.SelectedControls);

                // 从中间顶点 (200,200) 拖到 (240,260)：只该动这一个点
                Drag(canvas, (200, 200), (240, 260));

                var absolute = poly.Points!
                    .Select(p => (X: poly.X + p.X, Y: poly.Y + p.Y))
                    .ToArray();
                Assert.Equal(100, absolute[0].X, 0);
                Assert.Equal(100, absolute[0].Y, 0);
                Assert.Equal(240, absolute[1].X, 0);
                Assert.Equal(260, absolute[1].Y, 0);
                Assert.Equal(300, absolute[2].X, 0);     // 端点没被整体拖动带走
                Assert.Equal(100, absolute[2].Y, 0);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void NoTool_VertexDrag_BeatsWholeControlMove()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var poly = AddPolyline(screen);
            var (canvas, window) = Host(screen);
            try
            {
                // 直接在**未选中**的端点 (100,100) 上起拖：端点优先于整体拖动
                Drag(canvas, (100, 100), (60, 60));

                var absolute = poly.Points!
                    .Select(p => (X: poly.X + p.X, Y: poly.Y + p.Y))
                    .ToArray();
                Assert.Equal(60, absolute[0].X, 0);
                Assert.Equal(60, absolute[0].Y, 0);
                Assert.Equal(200, absolute[1].X, 0);     // 其余顶点原地不动
                Assert.Equal(200, absolute[1].Y, 0);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void VertexDrag_IsUndoable()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var poly = AddPolyline(screen);
            var (canvas, window) = Host(screen);
            try
            {
                Tap(canvas, 200, 200);
                Drag(canvas, (200, 200), (260, 300));
                Assert.Equal(300, poly.Y + poly.Points![1].Y, 0);

                canvas.UndoRedoManager.Undo();
                UiTestHost.Pump(1);

                var absolute = poly.Points!.Select(p => (X: poly.X + p.X, Y: poly.Y + p.Y)).ToArray();
                Assert.Equal(200, absolute[1].X, 0);
                Assert.Equal(200, absolute[1].Y, 0);
                Assert.Equal(100, poly.X, 0);           // 框也回滚
                Assert.Equal(200, poly.Width, 0);
            }
            finally { Close(window); }
        });
    }

    // ── ③ 取消工具 / 切工具不落控件 ──

    [Fact]
    public void SameToolTwice_Disarms()
    {
        var tool = CanvasTool.Figure("line");
        try
        {
            DockLayoutState.SetDrawTool(tool);
            Assert.Equal(tool.Kind, DockLayoutState.ActiveDrawTool!.Value.Kind);

            Assert.Null(DockLayoutState.ToggleDrawTool(tool));      // 再点同一项 → 取消
            Assert.Null(DockLayoutState.ActiveDrawTool);
        }
        finally { DockLayoutState.SetDrawTool(null); }
    }

    [Fact]
    public void SwitchingTool_AbandonsUnfinishedShape()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Figure("polyline"));
                Tap(canvas, 100, 100);
                Tap(canvas, 200, 200);

                DockLayoutState.SetDrawTool(CanvasTool.Widget(HmiControlType.Button));
                UiTestHost.Pump(1);

                Assert.Empty(screen.Controls);          // 切工具不落半成品
                Assert.False(canvas.HasToolGesture);
            }
            finally { Close(window); }
        });
    }

    // ── ④ Shift 正交 + 顶点对齐吸附 ──

    [Fact]
    public void ShiftWhileDrawing_MakesTheSegmentAxisAligned()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Figure("line"));
                Tap(canvas, 100, 200);
                // 按住 Shift 点到 (300,215)：x 位移更大 ⇒ 第二点被拉到 y=200
                TapWith(canvas, 300, 215, RawInputModifiers.Shift);

                var c = Assert.Single(screen.Controls);
                Assert.Equal(10, c.Height, 0);                 // 水平直线：细轴被抬到最小边长 10
                Assert.Equal(200, c.Width, 0);                 // x 跨度仍是 200
                var pts = c.Points!;
                Assert.Equal(5, pts[0].Y, 3);                  // 两个局部 y 相等 ⇒ 水平
                Assert.Equal(5, pts[1].Y, 3);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void ShiftWhileDraggingVertex_KeepsTheSegmentAxisAligned()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var poly = AddPolyline(screen);
            var (canvas, window) = Host(screen);
            try
            {
                Tap(canvas, 200, 200);                     // 选中折线
                // 从中间顶点 (200,200) Shift 拖到 (240,300)：锚点是**前一个顶点** (100,100)，
                // dx=140 / dy=200 ⇒ 纵向为主 ⇒ x 被拉到 100 —— 这一段变成正竖直线段
                Drag(canvas, (200, 200), (240, 300), RawInputModifiers.Shift);

                var absolute = poly.Points!.Select(p => (X: poly.X + p.X, Y: poly.Y + p.Y)).ToArray();
                Assert.Equal(100, absolute[1].X, 3);
                Assert.Equal(300, absolute[1].Y, 3);
                Assert.Equal(100, absolute[0].X, 3);       // 锚点（前一个顶点）不动
                Assert.Equal(100, absolute[0].Y, 3);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void VertexDrag_SnapsToNeighbourControlEdge()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            AddExistingBox(screen, "box", 300, 60, 120, 40);   // 左边缘 x=300
            var poly = AddPolyline(screen);                     // 顶点在 (100..300, 100..200)
            var (canvas, window) = Host(screen);
            try
            {
                Tap(canvas, 200, 200);
                // 拖到 303 —— 离 300 只有 3px ⇒ 吸附到隔壁控件的左边缘那条竖线上
                Drag(canvas, (200, 200), (303, 260));

                var absolute = poly.Points!.Select(p => (int)(poly.X + p.X)).ToArray();
                Assert.Equal(300, absolute[1]);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void Drawing_SnapsToNeighbourControlEdge()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            AddExistingBox(screen, "box", 300, 60, 120, 40);   // 左边缘 x=300
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Figure("line"));
                Tap(canvas, 100, 200);
                // 第二点落在 297 —— 离 300 只有 3px：应吸到隔壁控件的左边缘那条竖线上
                Tap(canvas, 297, 245);

                var c = screen.Controls.Single(x => x.Type == HmiControlType.Shape);
                Assert.Equal(100, c.X, 0);
                Assert.Equal(300, c.X + c.Width, 0);           // 右端被吸到 300（不是 297）
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void PointFigure_HasNoResizeHandles_SoVerticesWin()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var poly = AddPolyline(screen);
            var (canvas, window) = Host(screen);
            try
            {
                Tap(canvas, 200, 200);                          // 选中折线
                Assert.Single(canvas.SelectedControls);

                // 包围盒右下角 (300,200) 不是顶点：按点图元没有缩放手柄 ⇒ 这里拖下去不会**拉伸**控件
                // （拖动整体是允许的，所以只判尺寸没变）
                var w = poly.Width;
                var h = poly.Height;
                Drag(canvas, (300, 200), (380, 260));

                Assert.Equal(w, poly.Width, 3);
                Assert.Equal(h, poly.Height, 3);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void Pie_AngleHandleChangesTheSweep()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                // 两点画扇形：两点当直径 ⇒ 半圆（圆心 (200,200)、半径 100、180°→360°）
                DockLayoutState.SetDrawTool(CanvasTool.Figure("pie"));
                Tap(canvas, 100, 200);
                Tap(canvas, 300, 200);

                var pie = Assert.Single(screen.Controls);
                Assert.Equal("pie", pie.ShapeKind);
                Assert.Equal(180, pie.MinValue, 0);
                Assert.Equal(360, pie.MaxValue, 0);

                // 画完工具还开着（可连画）—— 先退出工具，否则按下会被当成"再画一个"的第一个点
                DockLayoutState.SetDrawTool(null);
                UiTestHost.Pump(1);

                // 拖起点手柄到 12 点方向（270°）：扫角从 180° 收到 90°
                Drag(canvas, (100, 200), (200, 100));

                Assert.Equal(270, pie.MinValue, 1);
                Assert.Equal(360, pie.MaxValue, 1);
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void DroppingAPointFigure_StartsDrawing_InsteadOfPlacingABox()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                // 工具箱把「弯管」拖到画布：先选中工具、落点当第一个点，接着再点两个就成形
                var tool = CanvasTool.Pipe(FigurePointMode.ThreePointCorner);
                DockLayoutState.SetDrawTool(tool);
                canvas.BeginPointTool(tool, 100, 100);
                Assert.Empty(screen.Controls);

                Tap(canvas, 100, 220);
                Tap(canvas, 280, 220);

                var c = Assert.Single(screen.Controls);
                Assert.Equal(HmiControlType.Pipe, c.Type);
                Assert.True(c.Points!.Count == 3, $"落地+两点应得到 3 点弯管，实际 {c.Points.Count}");
            }
            finally { Close(window); }
        });
    }

    [Fact]
    public void LeavingCanvas_AbandonsUnfinishedShape()
    {
        UiTestHost.Run(() =>
        {
            var screen = NewScreen();
            var (canvas, window) = Host(screen);
            try
            {
                DockLayoutState.SetDrawTool(CanvasTool.Figure("polyline"));
                Tap(canvas, 100, 100);

                window.Content = null;                  // 切窗口 / 离开画布
                UiTestHost.Pump(2);

                Assert.Empty(screen.Controls);
                Assert.False(canvas.HasToolGesture);
            }
            finally { Close(window); }
        });
    }
}
