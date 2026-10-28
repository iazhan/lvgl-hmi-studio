namespace HmiEditor.Core.Models;

/// <summary>
/// 几何图元的**点约束**：形状由几个点、怎么闭合、还是由拖框定尺寸。
///
/// <para>编辑器只按这张表分四类处理，不按控件类型各写一份 —— <c>line</c> 与
/// <c>Shape(kind=line)</c>、<c>Arc</c> 与 <c>Shape(kind=arc)</c> 导出的是同一个 kind，
/// 编辑行为必须一致，靠的就是"先归一到 mode，再走同一套"。</para>
/// </summary>
public enum FigurePointMode
{
    /// <summary>拖框定尺寸的普通控件（按钮、表格、矩形、椭圆、扇形以外的多边形族…）。</summary>
    Box,

    /// <summary>两点：直线、箭头三兄弟。第二次落点即提交。</summary>
    TwoPoint,

    /// <summary>N 点不闭合：折线、多段管道。</summary>
    OpenPath,

    /// <summary>N 点闭合（≥3）：多边形。</summary>
    ClosedPath,

    /// <summary>角度：圆弧、扇形。两个端点决定起止角。</summary>
    Angle,

    /// <summary>三点：弯管的起点-拐点-终点。</summary>
    ThreePointCorner,
}

/// <summary>图元上的一个可拖拽锚点（**模型坐标**，不是控件局部坐标）。</summary>
public readonly record struct GeometryAnchor(int Index, double X, double Y);

/// <summary>点集落到控件框上的结果（<see cref="Local"/> 为控件局部坐标）。</summary>
public readonly record struct FigureBox(double X, double Y, double Width, double Height,
    IReadOnlyList<PointData> Local);

/// <summary>
/// 几何图元的换算与编辑 —— 纯函数，不依赖 Avalonia，画布与判据共用同一份。
/// </summary>
public static class FigureGeometry
{
    /// <summary>契约给 <c>points</c> 的上限（<c>spec/widget-schema.json</c>）。</summary>
    public const int MaxPoints = 64;

    /// <summary>控件最小边长（与画布 <c>MinControlSize</c> 同值，两处都按"控件有框"算）。</summary>
    public const double MinSize = 10;

    /// <summary>管道默认管径（像素）。与契约 <c>pipe.width</c> 的 default 同值。</summary>
    public const double DefaultPipeThickness = 12;

    // ── 分类 ──

    /// <summary>按图元 kind 判定点约束。<c>pipe</c> 是被 <see cref="ModeFor(HmiControl)"/> 单独处理的。</summary>
    public static FigurePointMode ModeForKind(string? kind) => kind switch
    {
        "line" or "arrow" or "arrow_thin" or "arrow_thick" => FigurePointMode.TwoPoint,
        "polyline" => FigurePointMode.OpenPath,
        "polygon" => FigurePointMode.ClosedPath,
        "arc" or "pie" => FigurePointMode.Angle,
        _ => FigurePointMode.Box,
    };

    /// <summary>控件的编辑模式：由已存在的 kind（和管道已有的点数）反推，与"当初用什么工具画的"无关。</summary>
    public static FigurePointMode ModeFor(HmiControlType type, string? shapeKind, int pointCount = 0) => type switch
    {
        HmiControlType.Shape => ModeForKind(shapeKind ?? "rect"),
        HmiControlType.Line => FigurePointMode.TwoPoint,
        HmiControlType.Polyline => FigurePointMode.OpenPath,
        HmiControlType.Arc => FigurePointMode.Angle,
        // 管道的走向靠点数区分（运行时按 points 走整条路径）：2=直管、3=弯管、≥4=多段
        HmiControlType.Pipe => pointCount switch
        {
            3 => FigurePointMode.ThreePointCorner,
            >= 4 => FigurePointMode.OpenPath,
            _ => FigurePointMode.TwoPoint,
        },
        _ => FigurePointMode.Box,
    };

    public static FigurePointMode ModeFor(HmiControl control)
        => ModeFor(control.Type, control.ShapeKind, control.Points?.Count ?? 0);

    /// <summary>是否是"按点画"的图元（拖框工具之外的形状）。</summary>
    public static bool IsPointFigure(HmiControl control) => ModeFor(control) != FigurePointMode.Box;

    /// <summary>提交一个形状至少要几个点；不足即视为退化，不落控件。</summary>
    public static int MinPoints(FigurePointMode mode) => mode switch
    {
        FigurePointMode.TwoPoint => 2,
        FigurePointMode.OpenPath => 2,
        FigurePointMode.ClosedPath => 3,
        FigurePointMode.Angle => 2,
        FigurePointMode.ThreePointCorner => 3,
        _ => 0,
    };

    /// <summary>
    /// 形状是否真的按 <c>points</c> 存点（而不是由控件框 + 比例参数生成）。
    /// 箭头三兄弟与 polygon 族都从控件框推顶点，点只用来定位框。
    /// </summary>
    public static bool UsesPointList(HmiControl control) => ModeFor(control) switch
    {
        FigurePointMode.OpenPath or FigurePointMode.ClosedPath or FigurePointMode.ThreePointCorner => true,
        // 管道连直管也走 points：两点可以是对角方向，只写 direction=h/v 会把斜管画成水平管
        FigurePointMode.TwoPoint => control.Type == HmiControlType.Pipe
                                     || KindOf(control) is "line" or "polyline",
        _ => false,
    };

    /// <summary>控件导出/等价比较用的 kind（<c>Pipe</c> 是控件类型，不在 figures 白名单里）。</summary>
    public static string? KindOf(HmiControl control) => control.Type switch
    {
        HmiControlType.Shape => control.ShapeKind ?? "rect",
        HmiControlType.Line => "line",
        HmiControlType.Polyline => "polyline",
        HmiControlType.Arc => "arc",
        HmiControlType.Pipe => "pipe",
        HmiControlType.Rectangle => "rect",
        HmiControlType.Ellipse => "ellipse",
        HmiControlType.Tank => "tank",
        _ => null,
    };

    // ── 锚点：把"可拖的点"统一成模型坐标 ──

    /// <summary>
    /// 当前控件上的可拖锚点（模型坐标）。按模式给出：
    /// 点集图元 → 每个顶点；拖框型两点图元（箭头）→ 框的两个对角；
    /// 角度图元 → 起止角在椭圆上的端点。
    /// </summary>
    public static IReadOnlyList<GeometryAnchor> Anchors(HmiControl control)
    {
        var mode = ModeFor(control);
        switch (mode)
        {
            case FigurePointMode.Angle:
            {
                var (a, b) = AngleAnchors(control);
                return new[] { new GeometryAnchor(0, a.X, a.Y), new GeometryAnchor(1, b.X, b.Y) };
            }
            case FigurePointMode.Box:
                return Array.Empty<GeometryAnchor>();
            default:
            {
                if (UsesPointList(control))
                {
                    var local = LocalPoints(control);
                    var list = new List<GeometryAnchor>(local.Count);
                    for (var i = 0; i < local.Count; i++)
                        list.Add(new GeometryAnchor(i, control.X + local[i].X, control.Y + local[i].Y));
                    return list;
                }
                // 箭头/多边形族：形状由控件框生成，可拖的就是框的两个对角
                return new[]
                {
                    new GeometryAnchor(0, control.X, control.Y),
                    new GeometryAnchor(1, control.X + control.Width, control.Y + control.Height),
                };
            }
        }
    }

    /// <summary>
    /// 控件实际生效的局部顶点：未配置时回退对角线（与导出器 <c>PointsOf</c> 同一套兜底），
    /// 保证"看上去的点"与"拖动的点"是同一批。
    /// </summary>
    public static IReadOnlyList<PointData> LocalPoints(HmiControl control)
    {
        if (control.Points is { Count: >= 2 })
            return control.Points;
        return new[] { new PointData(0, 0), new PointData(control.Width, control.Height) };
    }

    // ── 角度 ↔ 两个端点 ──

    /// <summary>
    /// 圆弧/扇形的起止角端点（模型坐标）。角度约定与契约一致：0° 在 3 点方向、顺时针增大
    /// （屏幕 y 向下，所以 <c>sin</c> 直接加）。
    /// </summary>
    public static (PointData Start, PointData End) AngleAnchors(HmiControl control)
    {
        var cx = control.X + control.Width / 2;
        var cy = control.Y + control.Height / 2;
        var rx = control.Width / 2;
        var ry = control.Height / 2;
        return (OnEllipse(cx, cy, rx, ry, control.MinValue), OnEllipse(cx, cy, rx, ry, control.MaxValue));
    }

    private static PointData OnEllipse(double cx, double cy, double rx, double ry, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new PointData(cx + rx * Math.Cos(rad), cy + ry * Math.Sin(rad));
    }

    /// <summary>把椭圆上的一点换成角度（度，0..360）。椭圆的"角"不是极角，要按半径归一后再取 atan2。</summary>
    public static double AngleOf(double cx, double cy, double rx, double ry, double x, double y)
    {
        var nx = rx > 0 ? (x - cx) / rx : 0;
        var ny = ry > 0 ? (y - cy) / ry : 0;
        var deg = Math.Atan2(ny, nx) * 180.0 / Math.PI;
        return deg < 0 ? deg + 360.0 : deg;
    }

    /// <summary>
    /// 两次点击 → 圆弧/扇形：两点当作**直径两端**，得到一个半圆（圆心=中点）。
    ///
    /// <para>为什么是直径而不是"椭圆包围盒的两个角"：两个点无法唯一确定一个椭圆，
    /// 但能唯一确定一个过这两点的圆。取直径模型，提交后两个拖拽手柄就落在用户点的
    /// 位置上（不跳）；想改形状再拖手柄改起止角，或用缩放手柄改椭圆比例。</para>
    /// </summary>
    public static (double X, double Y, double W, double H, int Start, int End) ArcFromEndpoints(
        PointData a, PointData b, double minSize = MinSize)
    {
        var cx = (a.X + b.X) / 2;
        var cy = (a.Y + b.Y) / 2;
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var r = Math.Max(minSize / 2, Math.Sqrt(dx * dx + dy * dy) / 2);

        var start = (int)Math.Round(AngleOf(cx, cy, r, r, a.X, a.Y));
        return (cx - r, cy - r, 2 * r, 2 * r, start, start + 180);
    }

    // ── 编辑：拖一个锚点 ──

    /// <summary>
    /// 把第 <paramref name="index"/> 个锚点拖到 <paramref name="modelX"/>/<paramref name="modelY"/>。
    /// 只改几何（X/Y/W/H 与局部点、或起止角），不碰别的属性；返回值表示几何是否真的变了。
    ///
    /// <para>点集图元拖出控件框时会**重算框**（框 = 点集包围盒），因此"框跟着点走"而不是
    /// 点被裁掉 —— 运行时按 points 画，裁掉就变成"看得见但画不出"。</para>
    /// </summary>
    public static bool TryMoveAnchor(HmiControl control, int index, double modelX, double modelY,
        double snap = 0)
    {
        if (index < 0) return false;

        if (snap > 0)
        {
            modelX = Math.Round(modelX / snap) * snap;
            modelY = Math.Round(modelY / snap) * snap;
        }

        switch (ModeFor(control))
        {
            case FigurePointMode.Angle:
                return MoveAngleAnchor(control, index, modelX, modelY);

            case FigurePointMode.Box:
                return false;

            default:
                if (UsesPointList(control))
                    return MovePointAnchor(control, index, modelX, modelY);
                return MoveBoxAnchor(control, index, modelX, modelY);
        }
    }

    /// <summary>点集图元：换掉一个模型坐标顶点，再按包围盒重算控件框。</summary>
    private static bool MovePointAnchor(HmiControl control, int index, double modelX, double modelY)
    {
        var local = LocalPoints(control).ToList();
        if (index >= local.Count) return false;

        var absolute = local.Select(p => new PointData(control.X + p.X, control.Y + p.Y)).ToList();
        absolute[index] = new PointData(modelX, modelY);

        // 关掉吸附时 snap=0，点要能落在小数位置；导出会取整，这里保持原值以免连续拖动累积漂移。
        // 管道要沿用 FitPipe：否则拖一个顶点就把框缩成裸包围盒，管子的外沿立刻被裁掉。
        var box = control.Type == HmiControlType.Pipe
            ? FitPipe(absolute, control.PipeWidth)
            : Fit(absolute, MinSize);
        control.X = box.X;
        control.Y = box.Y;
        control.Width = box.Width;
        control.Height = box.Height;
        control.Points = box.Local.ToList();
        return true;
    }

    /// <summary>拖框型两点图元（箭头）：两个对角点定框。</summary>
    private static bool MoveBoxAnchor(HmiControl control, int index, double modelX, double modelY)
    {
        var otherX = index == 0 ? control.X + control.Width : control.X;
        var otherY = index == 0 ? control.Y + control.Height : control.Y;

        var x = Math.Min(modelX, otherX);
        var y = Math.Min(modelY, otherY);
        var w = Math.Max(MinSize, Math.Abs(modelX - otherX));
        var h = Math.Max(MinSize, Math.Abs(modelY - otherY));

        control.X = x;
        control.Y = y;
        control.Width = w;
        control.Height = h;
        return true;
    }

    /// <summary>角度图元：端点换角度，起止角互相夹住（最小 1°，最大一整圈）。</summary>
    private static bool MoveAngleAnchor(HmiControl control, int index, double modelX, double modelY)
    {
        var cx = control.X + control.Width / 2;
        var cy = control.Y + control.Height / 2;
        var angle = AngleOf(cx, cy, control.Width / 2, control.Height / 2, modelX, modelY);

        if (index == 0)
        {
            var end = control.MaxValue;
            control.MinValue = Math.Clamp(angle, end - 360, end - 1);
        }
        else
        {
            var start = control.MinValue;
            control.MaxValue = Math.Clamp(angle, start + 1, start + 360);
        }
        return true;
    }

    // ── 点集 ↔ 控件框 ──

    /// <summary>把模型坐标点集装进一个控件框：框 = 包围盒（不足 <paramref name="minSize"/> 时居中撑开）。</summary>
    public static FigureBox Fit(IReadOnlyList<PointData> modelPoints, double minSize = MinSize)
    {
        if (modelPoints.Count == 0)
            return new FigureBox(0, 0, minSize, minSize, new[] { new PointData(0, 0), new PointData(minSize, minSize) });

        var minX = modelPoints.Min(p => p.X);
        var minY = modelPoints.Min(p => p.Y);
        var maxX = modelPoints.Max(p => p.X);
        var maxY = modelPoints.Max(p => p.Y);

        var w = maxX - minX;
        var h = maxY - minY;

        // 水平/垂直的折线某一维为 0：控件框不能是 0 宽高（运行时建对象会失败），
        // 所以把点居中放进最小边长里，点的相对位置保持不变。
        if (w < minSize)
        {
            minX -= (minSize - w) / 2;
            w = minSize;
        }
        if (h < minSize)
        {
            minY -= (minSize - h) / 2;
            h = minSize;
        }

        var local = modelPoints.Select(p => new PointData(p.X - minX, p.Y - minY)).ToList();
        return new FigureBox(minX, minY, w, h, local);
    }

    /// <summary>
    /// 管道（<see cref="HmiControlType.Pipe"/>）的点集装框：**包围盒四边各留半个管径**。
    ///
    /// <para>为什么不像别的图元那样直接取包围盒：管道是"以折线为中心线、往外扩半个管径"
    /// 描出来的，框不给管径留位置，外沿就会被控件框裁掉（弯管的腿只剩一半）。
    /// 反过来说，框也**不能**用来反推管径 —— 弯管的框短边往往就是半条腿长，
    /// 按框推出来的管子会宽得像一块板（这正是实测踩到的现象）。所以管径是独立参数
    /// （<see cref="HmiControl.PipeWidth"/> → 契约 <c>pipe.width</c>），这里只按它留边。</para>
    ///
    /// <para>留边量 = 管径/2，与运行时 <c>pipe_fit_path</c> 的内缩量相等 ⇒ 点集映射是**恒等**，
    /// 顶点手柄正好落在看得见的管线中心线上；用户拉大框时点集等比缩放，形状不走形。</para>
    /// </summary>
    public static FigureBox FitPipe(IReadOnlyList<PointData> modelPoints,
        double thickness = DefaultPipeThickness)
    {
        if (modelPoints.Count == 0) return Fit(modelPoints);

        var t = Math.Clamp(thickness, 4, 200);
        var pad = t / 2;
        var minX = modelPoints.Min(p => p.X);
        var minY = modelPoints.Min(p => p.Y);
        var maxX = modelPoints.Max(p => p.X);
        var maxY = modelPoints.Max(p => p.Y);

        // 直线（某个轴跨度为 0）也要留出一整个管径，否则细轴宽度是 0
        var w = Math.Max(MinSize, maxX - minX + 2 * pad);
        var h = Math.Max(MinSize, maxY - minY + 2 * pad);
        var x = minX - pad;
        var y = minY - pad;

        var local = modelPoints
            .Select(p => new PointData(p.X - x, p.Y - y))
            .ToList();
        return new FigureBox(x, y, w, h, local);
    }

    // ── 正交约束与吸附（Shift 画直、顶点对齐）──

    /// <summary>
    /// Shift 正交约束：把点拉到"从 <paramref name="anchor"/> 出发的水平或垂直方向"上
    /// （取位移较大的那个轴当主方向）。画折线/管道时 anchor = 上一个点，
    /// 拖顶点时 anchor = 相邻顶点 —— 这样"按住 Shift 画出来的那一段"一定是水平或垂直的。
    /// </summary>
    public static PointData ConstrainOrtho(PointData anchor, PointData point)
    {
        var dx = point.X - anchor.X;
        var dy = point.Y - anchor.Y;
        return Math.Abs(dx) >= Math.Abs(dy)
            ? new PointData(point.X, anchor.Y)
            : new PointData(anchor.X, point.Y);
    }

    /// <summary>按网格步长取整（step ≤ 0 表示不吸附）。</summary>
    public static PointData ApplyGrid(PointData point, double step)
        => step <= 0
            ? point
            : new PointData(Math.Round(point.X / step) * step, Math.Round(point.Y / step) * step);

    /// <summary>
    /// 在候选坐标里找离 <paramref name="value"/> 最近的一个（容差内）。命中返回 true。
    /// 候选来自"同一图元的其它顶点 / 其它控件的边与中线 / 屏幕边与中线"，
    /// 这就是顶点拖拽的对齐吸附（参考线用的也是命中值）。
    /// </summary>
    public static bool TryNearest(IReadOnlyList<double> candidates, double value,
        double tolerance, out double hit)
    {
        hit = value;
        var best = double.MaxValue;
        var found = false;
        foreach (var candidate in candidates)
        {
            var delta = Math.Abs(candidate - value);
            if (delta > tolerance || delta >= best) continue;
            best = delta;
            hit = candidate;
            found = true;
        }
        return found;
    }

    /// <summary>控件顶点集的**绝对**（屏幕模型）坐标：控件框左上 + 局部点。</summary>
    public static IReadOnlyList<PointData> AbsolutePoints(HmiControl control)
    {
        var pts = control.Points;
        if (pts == null || pts.Count == 0) return Array.Empty<PointData>();
        var result = new List<PointData>(pts.Count);
        foreach (var p in pts)
            result.Add(new PointData(control.X + p.X, control.Y + p.Y));
        return result;
    }

    /// <summary>
    /// 拖顶点时的正交约束锚点：优先**前一个**顶点（画线时上一个点就是"起点"），
    /// 端点没有前一个就用后一个。返回 -1 表示没有可用锚点。
    /// </summary>
    public static int OrthoAnchorIndex(int index, int count)
    {
        if (count < 2) return -1;
        if (index > 0) return index - 1;
        return index + 1 < count ? index + 1 : -1;
    }

    /// <summary>点集是否退化（不足最小点数、或所有点重合）—— 退化形状不落控件。</summary>
    public static bool IsDegenerate(FigurePointMode mode, IReadOnlyList<PointData> points)
    {
        if (points.Count < MinPoints(mode)) return true;
        if (mode == FigurePointMode.Angle) return false;   // 角度由两个端点决定，允许重合

        var first = points[0];
        return points.All(p => Math.Abs(p.X - first.X) < 0.5 && Math.Abs(p.Y - first.Y) < 0.5);
    }
}
