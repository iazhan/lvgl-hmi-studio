using System;
using System.Collections.Generic;
using System.Linq;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Core.Tests;

/// <summary>
/// 「几何图元」的**纯换算**判据：点集 ↔ 控件框、两点 ↔ 起止角、三点 ↔ 弯管拐点、
/// 以及绘制会话的点数状态机（{@link FigureDrawSession}）。
///
/// <para>为什么要有这一层：画布上的"点几下、点出来是什么"靠 UI 判据（
/// <c>CanvasDrawGestureTests</c>）判，但**换算本身**必须能脱离 Avalonia 单独跑 —— 这里一旦错，
/// 画布与产物会一起错，而症状只是"形状有点歪"，人眼很难发现。</para>
/// </summary>
public class FigureGeometryTests
{
    private static HmiControl Figure(HmiControlType type, string? kind = null,
        double x = 0, double y = 0, double w = 100, double h = 60, params (double X, double Y)[] pts)
    {
        var c = new HmiControl
        {
            Id = "f1", Name = "f1", Type = type, ShapeKind = kind,
            X = x, Y = y, Width = w, Height = h,
        };
        if (pts.Length > 0)
            c.Points = pts.Select(p => new PointData(p.X, p.Y)).ToList();
        return c;
    }

    // ── ① 点数约束：同一 kind 从工具箱两条入口进来必须同一种取点方式 ──

    [Theory]
    [InlineData("line", FigurePointMode.TwoPoint)]
    [InlineData("arrow", FigurePointMode.TwoPoint)]
    [InlineData("arrow_thick", FigurePointMode.TwoPoint)]
    [InlineData("arrow_thin", FigurePointMode.TwoPoint)]
    [InlineData("polyline", FigurePointMode.OpenPath)]
    [InlineData("polygon", FigurePointMode.ClosedPath)]
    [InlineData("arc", FigurePointMode.Angle)]
    [InlineData("pie", FigurePointMode.Angle)]
    [InlineData("rect", FigurePointMode.Box)]
    [InlineData("circle", FigurePointMode.Box)]
    public void ModeForKind_MatchesPointConstraint(string kind, FigurePointMode expected)
        => Assert.Equal(expected, FigureGeometry.ModeForKind(kind));

    [Fact]
    public void SameKind_TwoToolboxEntries_SamePointConstraint()
    {
        // 「线条」按钮（HmiControlType.Line）与「通用图元 → line」（Shape+kind）导出同一个 kind，
        // 取点方式必须一致 —— 否则同一形状从两条入口画出来是两种交互。
        var typed = CanvasTool.Widget(HmiControlType.Line);
        var figure = CanvasTool.Figure("line");

        Assert.Equal("line", typed.Kind);
        Assert.Equal("line", figure.Kind);
        Assert.Equal(FigurePointMode.TwoPoint, typed.Mode);
        Assert.Equal(FigurePointMode.TwoPoint, figure.Mode);

        // 管道三条工具共用 HmiControlType.Pipe，只差点数
        Assert.True(CanvasTool.Pipe(FigurePointMode.TwoPoint)
            .SameTarget(CanvasTool.Pipe(FigurePointMode.TwoPoint)));
        Assert.False(CanvasTool.Pipe(FigurePointMode.TwoPoint)
            .SameTarget(CanvasTool.Pipe(FigurePointMode.ThreePointCorner)));
        Assert.False(CanvasTool.Pipe(FigurePointMode.TwoPoint).SameTarget(figure));
    }

    // ── ② 点集 ↔ 控件框 ──

    [Fact]
    public void Fit_NormalizesPointsIntoBox()
    {
        var box = FigureGeometry.Fit(new List<PointData> { new(30, 40), new(110, 90), new(60, 20) });

        Assert.Equal(30, box.X, 3);
        Assert.Equal(20, box.Y, 3);
        Assert.Equal(80, box.Width, 3);      // 110-30
        Assert.Equal(70, box.Height, 3);     // 90-20
        Assert.Equal(0, box.Local[0].X, 3);  // (30,40) → (0,20)
        Assert.Equal(20, box.Local[0].Y, 3);
        Assert.Equal(80, box.Local[1].X, 3); // (110,90) → (80,70)
        Assert.Equal(70, box.Local[1].Y, 3);
    }

    [Fact]
    public void Fit_ZeroHeightLine_GetsMinimumHeightAndCenteredPoints()
    {
        // 纯水平的两点线：包围盒高 0，控件高度取最小值，点落在垂直中线上
        // （否则线会被塞在框的最上边，选中框与线错位）。
        var box = FigureGeometry.Fit(new List<PointData> { new(10, 50), new(200, 50) });

        Assert.True(box.Height >= FigureGeometry.MinSize);
        Assert.Equal(box.Height / 2, box.Local[0].Y, 3);
        Assert.Equal(box.Height / 2, box.Local[1].Y, 3);
    }

    [Fact]
    public void Anchors_AreModelCoordinates()
    {
        var c = Figure(HmiControlType.Polyline, null, 100, 200, 80, 40,
            (0, 0), (40, 40), (80, 0));

        var anchors = FigureGeometry.Anchors(c);

        Assert.Equal(3, anchors.Count);
        Assert.Equal(100, anchors[0].X, 3);
        Assert.Equal(200, anchors[0].Y, 3);
        Assert.Equal(140, anchors[1].X, 3);
        Assert.Equal(240, anchors[1].Y, 3);
        Assert.Equal(new[] { 0, 1, 2 }, anchors.Select(a => a.Index).ToArray());
    }

    [Fact]
    public void TryMoveAnchor_OnlyThatVertexMoves_BoxFollows()
    {
        var c = Figure(HmiControlType.Polyline, null, 100, 100, 100, 50,
            (0, 0), (50, 50), (100, 0));

        // 把中间顶点拖到 (60, 90)（绝对）→ 局部 (60,90) 落到框外，框要跟着重算
        Assert.True(FigureGeometry.TryMoveAnchor(c, 1, 160, 190, 0));

        var pts = c.Points!.Select(p => new { X = c.X + p.X, Y = c.Y + p.Y }).ToList();
        Assert.Equal(100, pts[0].X, 3);
        Assert.Equal(100, pts[0].Y, 3);
        Assert.Equal(160, pts[1].X, 3);
        Assert.Equal(190, pts[1].Y, 3);
        Assert.Equal(200, pts[2].X, 3);   // 第三个点绝对位置不变
        Assert.Equal(100, pts[2].Y, 3);
        Assert.Equal(100, c.Y, 3);        // 框上边仍贴第一个点
        Assert.Equal(90, c.Height, 3);    // 下边跟到新顶点
    }

    [Fact]
    public void TryMoveAnchor_ThreePointElbow_KeepsEndpoints()
    {
        // 弯管 = 三点：起点-拐点-终点。拖拐点只该改中间那个点。
        var c = Figure(HmiControlType.Pipe, null, 20, 20, 120, 80,
            (0, 80), (60, 80), (120, 0));

        Assert.True(FigureGeometry.TryMoveAnchor(c, 1, 140, 120, 0));

        var pts = c.Points!.Select(p => new { X = c.X + p.X, Y = c.Y + p.Y }).ToList();
        Assert.Equal(20, pts[0].X, 3);
        Assert.Equal(100, pts[0].Y, 3);
        Assert.Equal(140, pts[1].X, 3);
        Assert.Equal(120, pts[1].Y, 3);
        Assert.Equal(140, pts[2].X, 3);
        Assert.Equal(20, pts[2].Y, 3);
    }

    // ── ③ 两点 ↔ 起止角 ──

    [Fact]
    public void ArcFromEndpoints_RightEndpointIsZeroDegrees()
    {
        // 0° 在 3 点方向、顺时针：左端点是 180°、右端点是 360°(=0)
        var arc = FigureGeometry.ArcFromEndpoints(new PointData(0, 50), new PointData(100, 50));

        Assert.Equal(0, arc.X, 3);
        Assert.Equal(0, arc.Y, 3);
        Assert.Equal(100, arc.W, 3);
        Assert.Equal(100, arc.H, 3);
        Assert.Equal(180d, arc.Start, 2);
        Assert.Equal(360d, arc.End, 2);
    }

    [Fact]
    public void ArcFromEndpoints_VerticalPair_StartsAtFirstPoint()
    {
        // 第一个点是起点（上端点在 270°），扫 180° 到对侧（下端点 90° = 270+180）
        var arc = FigureGeometry.ArcFromEndpoints(new PointData(50, 0), new PointData(50, 100));

        Assert.Equal(270d, arc.Start, 2);
        Assert.Equal(450d, arc.End, 2);
    }

    [Fact]
    public void ArcAnchors_RoundTripToTheTwoClickedPoints()
    {
        var start = new PointData(60, 40);
        var end = new PointData(180, 120);
        var arc = FigureGeometry.ArcFromEndpoints(start, end);
        var c = Figure(HmiControlType.Arc, null, arc.X, arc.Y, arc.W, arc.H);
        c.MinValue = arc.Start;
        c.MaxValue = arc.End;

        var anchors = FigureGeometry.Anchors(c);

        Assert.True(anchors.Count == 2, $"圆弧应有 2 个端点手柄，实际 {anchors.Count}");
        // 起止角按整度存，大半径上 1° 误差 ≈ 1px ⇒ 端点落回点击处的容差取 1.5px
        Assert.InRange(Math.Abs(anchors[0].X - start.X), 0, 1.5);
        Assert.InRange(Math.Abs(anchors[0].Y - start.Y), 0, 1.5);
        Assert.InRange(Math.Abs(anchors[1].X - end.X), 0, 1.5);
        Assert.InRange(Math.Abs(anchors[1].Y - end.Y), 0, 1.5);
    }

    [Fact]
    public void TryMoveAnchor_ArcEndpoint_TurnsSweepButNeverCrossesStart()
    {
        // 弧：圆心 (50,50)、半径 50，180°→360°。拖终点手柄 = 改扫过的角度。
        var c = Figure(HmiControlType.Arc, null, 0, 0, 100, 100);
        c.MinValue = 180;
        c.MaxValue = 360;

        // 终点拖到 12 点方向（270°，顺时针约定 90°=下）：扫角从 180° 收到 90°
        Assert.True(FigureGeometry.TryMoveAnchor(c, 1, 50, 0, 0));
        Assert.Equal(180, c.MinValue, 1);
        Assert.Equal(270, c.MaxValue, 1);

        // 终点拖回起点之前（90°）：扫角不许翻转，钳到最小 1°
        Assert.True(FigureGeometry.TryMoveAnchor(c, 1, 50, 100, 0));
        Assert.Equal(180, c.MinValue, 1);
        Assert.Equal(181, c.MaxValue, 1);
    }

    // ── ④ 绘制会话状态机 ──

    private static FigureDrawSession Session(CanvasTool tool) => new(tool);

    [Fact]
    public void Session_TwoPoint_CommitsOnSecondClick()
    {
        var s = Session(CanvasTool.Figure("line"));

        Assert.Equal(FigureDrawOutcome.Pending, s.Click(new PointData(10, 10)));
        Assert.True(s.Points.Count == 1);
        Assert.Equal(FigureDrawOutcome.Commit, s.Click(new PointData(80, 40)));
        Assert.True(s.Points.Count == 2);
    }

    [Fact]
    public void Session_OpenPath_OnlyCommitsOnFinish()
    {
        var s = Session(CanvasTool.Figure("polyline"));

        Assert.Equal(FigureDrawOutcome.Pending, s.Click(new PointData(0, 0)));
        Assert.Equal(FigureDrawOutcome.Pending, s.Click(new PointData(40, 40)));
        Assert.Equal(FigureDrawOutcome.Pending, s.Click(new PointData(80, 0)));
        Assert.Equal(FigureDrawOutcome.Pending, s.Click(new PointData(120, 40)));
        Assert.Equal(4, s.Points.Count);
        Assert.Equal(FigureDrawOutcome.Commit, s.Finish());
    }

    [Fact]
    public void Session_OpenPath_WithSinglePoint_FinishCancels()
    {
        var s = Session(CanvasTool.Figure("polyline"));
        s.Click(new PointData(0, 0));

        Assert.Equal(FigureDrawOutcome.Cancelled, s.Finish());
    }

    [Fact]
    public void Session_ClosedPath_NeedsThreePoints()
    {
        var two = Session(CanvasTool.Figure("polygon"));
        two.Click(new PointData(0, 0));
        two.Click(new PointData(50, 0));
        Assert.Equal(FigureDrawOutcome.Cancelled, two.Finish());   // <3 点不成多边形

        var three = Session(CanvasTool.Figure("polygon"));
        three.Click(new PointData(0, 0));
        three.Click(new PointData(50, 0));
        three.Click(new PointData(25, 40));
        Assert.Equal(FigureDrawOutcome.Commit, three.Finish());
    }

    [Fact]
    public void Session_ThreePointCorner_CommitsOnThirdClick()
    {
        var s = Session(new CanvasTool(HmiControlType.Pipe, null, FigurePointMode.ThreePointCorner));

        Assert.Equal(FigureDrawOutcome.Pending, s.Click(new PointData(0, 80)));
        Assert.Equal(FigureDrawOutcome.Pending, s.Click(new PointData(60, 80)));
        Assert.Equal(FigureDrawOutcome.Commit, s.Click(new PointData(60, 0)));
    }

    [Fact]
    public void Session_Cancel_NeverCommits()
    {
        var s = Session(CanvasTool.Figure("polyline"));
        s.Click(new PointData(0, 0));
        s.Click(new PointData(50, 50));

        s.Cancel();

        Assert.Equal(FigureDrawOutcome.Cancelled, s.Finish());
        Assert.Empty(s.Points);
    }

    [Fact]
    public void Session_BoxTool_TakesNoPoints()
    {
        var s = Session(CanvasTool.Widget(HmiControlType.Button));

        Assert.Equal(FigureDrawOutcome.Cancelled, s.Click(new PointData(10, 10)));
        Assert.Equal(FigureDrawOutcome.Cancelled, s.Finish());
        Assert.Empty(s.Points);
    }

    [Fact]
    public void IsDegenerate_CoincidentPointsAreNotAShape()
    {
        var pts = new List<PointData> { new(10, 10), new(10, 10) };
        Assert.True(FigureGeometry.IsDegenerate(FigurePointMode.OpenPath, pts));
        Assert.False(FigureGeometry.IsDegenerate(FigurePointMode.OpenPath,
            new List<PointData> { new(10, 10), new(20, 10) }));
        Assert.True(FigureGeometry.IsDegenerate(FigurePointMode.ClosedPath,
            new List<PointData> { new(0, 0), new(10, 0) }));
    }

    // ── Shift 正交 / 吸附 / 管道装框 ──

    [Fact]
    public void ConstrainOrtho_KeepsTheDominantAxis()
    {
        // 横向为主：y 拉到锚点高度
        var horizontal = FigureGeometry.ConstrainOrtho(new PointData(10, 50), new PointData(90, 62));
        Assert.Equal(90, horizontal.X, 3);
        Assert.Equal(50, horizontal.Y, 3);

        // 纵向为主：x 拉到锚点
        var vertical = FigureGeometry.ConstrainOrtho(new PointData(10, 50), new PointData(18, 140));
        Assert.Equal(10, vertical.X, 3);
        Assert.Equal(140, vertical.Y, 3);

        // 两轴相等时判横向（取 >=），保证结果稳定而不是看浮点误差
        var tie = FigureGeometry.ConstrainOrtho(new PointData(0, 0), new PointData(30, 30));
        Assert.Equal(30, tie.X, 3);
        Assert.Equal(0, tie.Y, 3);
    }

    [Fact]
    public void ApplyGrid_ZeroStepIsNoSnap()
    {
        var p = new PointData(13, 27);

        Assert.Equal(13, FigureGeometry.ApplyGrid(p, 0).X, 3);
        var snapped = FigureGeometry.ApplyGrid(p, 10);
        Assert.Equal(10, snapped.X, 3);
        Assert.Equal(30, snapped.Y, 3);
    }

    [Fact]
    public void TryNearest_PicksTheClosestWithinTolerance()
    {
        var candidates = new[] { 100.0, 160.0, 240.0 };

        Assert.True(FigureGeometry.TryNearest(candidates, 163, 5, out var hit));
        Assert.Equal(160, hit, 3);

        // 容差外的最近点不吸（宁可留原值，也不要把用户的手抖放大成跳格）
        Assert.False(FigureGeometry.TryNearest(candidates, 200, 5, out var keep));
        Assert.Equal(200, keep, 3);
    }

    [Fact]
    public void OrthoAnchorIndex_UsesPreviousVertex_ElseNext()
    {
        Assert.Equal(0, FigureGeometry.OrthoAnchorIndex(1, 3));
        Assert.Equal(1, FigureGeometry.OrthoAnchorIndex(2, 3));
        // 首端点没有前一个 → 用后一个
        Assert.Equal(1, FigureGeometry.OrthoAnchorIndex(0, 2));
        Assert.Equal(-1, FigureGeometry.OrthoAnchorIndex(0, 1));
    }

    [Fact]
    public void FitPipe_LeavesHalfDiameterAroundTheBoundingBox()
    {
        // 管径 12 ⇒ 四边各留 6；框 = 包围盒 + 12
        var box = FigureGeometry.FitPipe(
            new List<PointData> { new(100, 100), new(200, 100) }, 12);

        Assert.Equal(94, box.X, 3);
        Assert.Equal(94, box.Y, 3);
        Assert.Equal(112, box.Width, 3);     // 100 跨度 + 12
        Assert.Equal(12, box.Height, 3);     // 直线：细轴就是管径
        // 点集映射回框是恒等：局部点 = 相对框左上
        Assert.Equal(6, box.Local[0].X, 3);
        Assert.Equal(6, box.Local[0].Y, 3);
        Assert.Equal(106, box.Local[1].X, 3);
    }

    [Fact]
    public void FitPipe_ThickerDiameterMeansAThickerBoxNotAWiderPath()
    {
        var thin = FigureGeometry.FitPipe(
            new List<PointData> { new(0, 0), new(100, 0), new(100, 80) }, 8);
        var thick = FigureGeometry.FitPipe(
            new List<PointData> { new(0, 0), new(100, 0), new(100, 80) }, 40);

        // 路径跨度不变，框随管径变大（改管径不该把形状拉长）
        Assert.Equal(100, thin.Width - 8, 3);
        Assert.Equal(100, thick.Width - 40, 3);
        Assert.Equal(80, thin.Height - 8, 3);
        Assert.Equal(80, thick.Height - 40, 3);
    }
}
