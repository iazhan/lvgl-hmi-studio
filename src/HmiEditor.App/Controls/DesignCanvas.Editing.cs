using Avalonia;
using HmiEditor.App.Design;
using HmiEditor.App.Resources;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using HmiEditor.Core;
using HmiEditor.Core.Design;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using System.IO;
using System.Linq;

namespace HmiEditor.App.Controls;

/// <summary>
/// DesignCanvas 分部 —— 控件创建与编辑操作。
/// 含 AddControl/AddControlAt/BuildControl、绘制工具（折线/多边形）的提交、
/// 模型与视图坐标换算、剪贴板与变换（复制/粘贴/删除/组合）、
/// 对齐分布、对齐辅助线吸附。
/// </summary>
public partial class DesignCanvas : Control
{
    /// <summary>
    /// 从工具箱添加控件到画布中心区域。
    /// </summary>
    /// <param name="shapeKind">
    /// 图元的种类（<c>rect</c> / <c>circle</c> / <c>container</c> …）。工具箱里每个形状是一条
    /// 独立条目，靠它区分；只有 <see cref="HmiControlType.Shape"/> 会消费这个值。
    /// </param>
    public HmiControl AddControl(HmiControlType type, double centerX, double centerY,
        string? shapeKind = null)
    {
        var (defaultW, defaultH) = DefaultSize(type);
        return AddControlAt(type,
            SnapToGrid(centerX - defaultW / 2),
            SnapToGrid(centerY - defaultH / 2),
            defaultW, defaultH, null, shapeKind);
    }

    /// <summary>
    /// I10: 在指定边界（模型坐标）创建控件 —— 绘图工具提交与工具箱添加共用。
    /// 顶点列表（Polyline）可选，未传时按类型给出默认几何。
    /// </summary>
    public HmiControl AddControlAt(HmiControlType type, double x, double y,
        double width, double height, IReadOnlyList<PointData>? points = null,
        string? shapeKind = null)
    {
        var screen = Screen;
        if (screen == null)
            throw new InvalidOperationException("Screen must be set before adding controls.");

        var control = BuildControl(type, x, y, width, height, points, shapeKind);

        screen.Controls.Add(control);
        SelectControl(control);
        InvalidateVisual();

        return control;
    }

    /// <summary>I10: 控件默认尺寸（未拖拽时的单击落点尺寸）。</summary>
    private static (double W, double H) DefaultSize(HmiControlType type) => type switch
    {
        HmiControlType.Button => (120.0, 44.0),
        HmiControlType.Label => (160.0, 30.0),
        HmiControlType.Slider => (200.0, 20.0),
        HmiControlType.Switch => (60.0, 30.0),
        HmiControlType.CheckBox => (120.0, 30.0),
        HmiControlType.Image => (100.0, 80.0),
        HmiControlType.Bar => (200.0, 20.0),
        HmiControlType.Dropdown => (140.0, 36.0),
        HmiControlType.TextArea => (200.0, 80.0),
        HmiControlType.Gauge => (160.0, 160.0),
        HmiControlType.Knob => (100.0, 100.0),
        HmiControlType.Keyboard => (240.0, 300.0),
        HmiControlType.Led => (40.0, 40.0),
        HmiControlType.Spinner => (60.0, 60.0),
        HmiControlType.TabView => (300.0, 200.0),
        HmiControlType.Line => (100.0, 2.0),
        HmiControlType.Table => (300.0, 200.0),
        HmiControlType.Svg => (160.0, 120.0),
        // I7: 过程图形控件
        HmiControlType.Tank => (140.0, 200.0),
        HmiControlType.Pipe => (160.0, 8.0),
        HmiControlType.Valve => (60.0, 60.0),
        // I10: 基础绘图图形
        HmiControlType.Rectangle => (160.0, 120.0),
        HmiControlType.Ellipse => (140.0, 100.0),
        HmiControlType.Polyline => (160.0, 80.0),
        HmiControlType.Arc => (120.0, 120.0),
        // chenge-runtime 目标新增
        HmiControlType.Value => (180.0, 40.0),
        HmiControlType.AlarmList => (400.0, 180.0),
        HmiControlType.HistoryCurve => (400.0, 180.0),
        HmiControlType.RealtimeCurve => (400.0, 180.0),
        HmiControlType.AlarmBar => (400.0, 32.0),
        HmiControlType.QrCode => (120.0, 120.0),
        HmiControlType.Message => (400.0, 44.0),
        HmiControlType.EnumText => (160.0, 32.0),
        HmiControlType.Roller => (160.0, 120.0),
        HmiControlType.StateImage => (120.0, 120.0),
        HmiControlType.FrameAnim => (120.0, 120.0),
        HmiControlType.Shape => (120.0, 120.0),
        HmiControlType.Chart => (320.0, 200.0),
        HmiControlType.XyCurve => (400.0, 180.0),
        HmiControlType.HistoryTable => (400.0, 180.0),
        HmiControlType.RecipeTable => (400.0, 180.0),
        HmiControlType.AsciiDisplay => (200.0, 32.0),
        _ => (100.0, 40.0)
    };

    /// <summary>
    /// I10: 构建已配置默认样式/名称/几何的控件（不加入屏幕集合）。
    /// 供 <see cref="AddControlAt"/> 与绘图提交共用，保证两路径默认外观一致。
    /// </summary>
    private HmiControl BuildControl(HmiControlType type, double x, double y,
        double width, double height, IReadOnlyList<PointData>? points,
        string? shapeKind = null)
    {
        var screen = Screen;
        if (screen == null)
            throw new InvalidOperationException("Screen must be set before adding controls.");

        // 默认名 = **中性名**（`Button1` / `Screen1` …）—— 与界面语言无关，因为它会写进
        // 工程文件、并进生成代码的符号（登记 #61r，阶段 159）。
        // ⚠ 名字的单一来源是 ControlNameDefaults；**显示**用的本地化别名也由它给
        //   （`Display(...)`）—— 面板/列表/画布看到的是「按钮1」，落盘的是 `Button1`。
        // 唯一性按**整棵树**判：容器子控件不进平铺列表，只查 screen.Controls 会重名。
        var uniqueName = Services.ControlNameDefaults.UniqueName(
            type, ContainerHierarchy.Flatten(screen).Select(c => c.Name));

        // 控件的默认**显示文案**仍按语言（`按钮` / `Button`）—— 那是给操作员看的 Text、
        // 用户可以改；「标识符」与「显示文案」在本阶段被明确分开（见 ControlNameDefaults 的类注释）。
        // ⚠ 收敛前这里自带一份 25 支的 Lang.Tb* 手工表，**漏了 Screen** ⇒ 从工具箱拖
        //   「屏幕」建出来的控件默认名是英文 "Screen1"，而它在工具箱里叫「屏幕」。
        var localizedName = Services.ControlTypeTextKeys.Describe(type);

        var control = new HmiControl
        {
            Type = type,
            // 只有图元带种类：其余类型忽略（把 kind 写进别的控件会生成非法属性）
            ShapeKind = type == HmiControlType.Shape ? shapeKind : null,
            Name = uniqueName,
            X = x,
            Y = y,
            Width = Math.Max(MinControlSize, width),
            Height = Math.Max(MinControlSize, height),
            Text = NewControlDefaults.HasCaption(type) ? localizedName : null,
        };

        // 下拉框/滚轮的选项必须非空：运行时 build_combobox/build_roller 拿不到 options
        // 会直接 return -1 ⇒ 整页建页失败 ⇒ 画布一片空白（不是"控件不显示"那么轻）。
        if (type == HmiControlType.Dropdown && string.IsNullOrWhiteSpace(control.Options))
            control.Options = HmiEditor.App.Resources.AppLocalization.Service[
                HmiEditor.App.Resources.Lang.DefaultOptions];
        if (type == HmiControlType.Roller && string.IsNullOrWhiteSpace(control.RollerOptions))
            control.RollerOptions = HmiEditor.App.Resources.AppLocalization.Service[
                HmiEditor.App.Resources.Lang.DefaultOptions];

        // 页签同理：没有 tabs 的 tabview 会被导出器整块跳过（"页签控件没有页签名"），
        // 新建出来就不可用。给两个默认页签，用户再改名。
        if (type == HmiControlType.TabView && string.IsNullOrWhiteSpace(control.TabTitles))
            control.TabTitles = HmiEditor.App.Resources.AppLocalization.Service[
                HmiEditor.App.Resources.Lang.DefaultTabs];

        // 其余的模板初值（底色 / 圆角 / 边框 / 图形几何 / 输入框量程 / 读数字号）在
        // Core 的 NewControlDefaults —— 导出判据与画布必须走同一份，否则"拖出来什么样"
        // 与"产物里是什么"会各说一套。
        NewControlDefaults.Apply(control, points);

        // 按点图元的顶点：Apply 只给折线/直线写了初值，管道与多边形（Shape）在这里统一落点 ——
        // 少了这一步，画布上按点画出来的形状与产物里的 points 就是两回事。
        if (points is { Count: >= 2 } && control.Points == null && FigureGeometry.UsesPointList(control))
            control.Points = points.ToList();

        // 曲线/图表没有数据源时导出期直接报错，运行时以整页建页失败收场 ——
        // 画布上就是"拖进来一片空白"。新建时先挂工程里第一个数值变量，用户再换通道。
        EnsureDefaultSeries(control);

        return control;
    }

    /// <summary>
    /// 给数据型控件挂一个**默认通道/序列**。
    ///
    /// <para>这几类的 props 里"至少一条通道"是硬要求（导出器不满足就报 Error，运行时
    /// 拒绝整页），而新建控件的模型字段是空的 —— 结果就是拖进来之后画布空白、实时预览
    /// 也不出图，用户得先猜到"要先去面板填通道"才看得见控件。挂第一个数值变量是最小
    /// 可用形态：看得见曲线/图表，再按需要改。</para>
    /// </summary>
    private static void EnsureDefaultSeries(HmiControl control)
    {
        var project = DockLayoutState.CurrentProject;
        var vars = project?.VariableManager.Variables;
        if (vars == null) return;

        var first = vars.FirstOrDefault(v =>
            !string.IsNullOrEmpty(v.Id) && v.DataType is not ("String" or "Bool"));
        if (first == null) return;

        switch (control.Type)
        {
            case HmiControlType.RealtimeCurve:
                if (string.IsNullOrWhiteSpace(control.CurveVariableIds))
                    control.CurveVariableIds = first.Id;
                break;

            case HmiControlType.HistoryCurve:
                if (string.IsNullOrWhiteSpace(control.CurveVariableIds))
                    control.CurveVariableIds = first.Id;
                // 历史曲线还要求趋势组：取工程里第一个数据记录组（没有就留空，
                // 导出期会提示"没有趋势组"，但通道已经挂好，用户只差选组）
                if (string.IsNullOrWhiteSpace(control.TrendGroup))
                    control.TrendGroup = project!.DataLogConfigs.FirstOrDefault()?.Name;
                break;

            case HmiControlType.XyCurve:
                if (string.IsNullOrWhiteSpace(control.XyTagX)) control.XyTagX = first.Id;
                if (string.IsNullOrWhiteSpace(control.XyTagY)) control.XyTagY = first.Id;
                break;

            case HmiControlType.Chart:
                if (string.IsNullOrWhiteSpace(control.ChartSeries))
                    control.ChartSeries = $"{first.Id}:{first.Name}";
                break;
        }
    }

    // ── I10: 绘图工具提交 ──

    /// <summary>视口坐标 → 模型坐标（补偿平移与缩放）。</summary>
    private CanvasCoordinateContext Coordinates
        => new(EffectiveZoom, PanOffsetX, PanOffsetY);

    private Point ToModel(Point viewportPoint)
        => Coordinates.ScreenToModel(viewportPoint);

    /// <summary>视口坐标 → 模型坐标（<see cref="PointData"/> 版，几何换算用）。</summary>
    private PointData ToModelData(Point viewportPoint)
    {
        var p = ToModel(viewportPoint);
        return new PointData(p.X, p.Y);
    }

    /// <summary>画布上是否有在途的绘制手势 —— 右键菜单据此让位给"取消绘制"。</summary>
    public bool HasToolGesture => _drawSession != null || _isDrawing;

    /// <summary>
    /// 从"拖进来的工具"开始按点绘制：把落点当作第一个点。
    /// 工具箱把弯管/多段管道拖到画布时走这条 —— 按老路落一个默认框，只会得到一根
    /// 没有走向的直管（形状信息全丢了），用户看到的就是"拖了但没画出弯管"。
    /// </summary>
    public void BeginPointTool(CanvasTool tool, double modelX, double modelY)
    {
        if (!tool.IsPointTool) return;

        AbortToolSession();
        _drawSession = new FigureDrawSession(tool);
        if (_drawSession.Click(new PointData(modelX, modelY)) == FigureDrawOutcome.Commit)
        {
            var session = _drawSession;
            ResetDrawState();
            CommitPointShape(session);
            return;
        }
        InvalidateVisual();
    }

    /// <summary>
    /// ESC / 右键：结束在途绘制。按点图元"够点即提交"（N 点式就在这里成形），
    /// 不足点数直接放弃 —— 不留退化形状，也不就地落控件。
    /// </summary>
    public bool FinishToolGesture()
    {
        var handled = false;
        if (_drawSession is { } session)
        {
            handled = true;
            if (session.Finish() == FigureDrawOutcome.Commit)
                CommitPointShape(session);
            ResetDrawState();
        }
        if (_isDrawing)
        {
            _isDrawing = false;
            handled = true;
            InvalidateVisual();
        }
        return handled;
    }

    /// <summary>
    /// 切页面 / 离开画布：**放弃**在途绘制。绝不在这里提交 —— 用户没做完的形状不该
    /// 因为他切了个页面就"就地落一个控件"。
    /// </summary>
    public void AbortToolGesture() => AbortToolSession();

    /// <summary>工具切换 / 切页面 / 离开画布：**放弃**在途绘制（永不落控件）。</summary>
    private void AbortToolSession()
    {
        if (_drawSession != null)
            Console.WriteLine($"[draw] abort n={_drawSession.Points.Count} mode={_drawSession.Mode}");
        _drawSession?.Cancel();
        ResetDrawState();
    }

    private void ResetDrawState()
    {
        _drawSession = null;
        _drawHoverPoint = default;
        _drawPressMoved = false;
        _isDrawing = false;
        PublishGuides(null);          // 绘制结束/放弃：参考线一起收掉
        InvalidateVisual();
    }

    /// <summary>
    /// 拖框工具释放时提交：拖拽矩形为控件边界，单击（没拖动）按该类型默认尺寸落在点击处。
    /// 边界吸附网格并限制在屏幕范围内。
    /// </summary>
    private void CommitBoxDraw(Point endViewportPoint)
    {
        try
        {
            var tool = DrawTool;
            var screen = Screen;
            if (tool is not { } armed || screen == null) return;

            var start = ToModel(_drawStartPoint);
            var end = ToModel(endViewportPoint);
            var w = Math.Abs(end.X - start.X);
            var h = Math.Abs(end.Y - start.Y);

            if (w < MinControlSize && h < MinControlSize)
            {
                // 单击：以点击处为中心创建默认尺寸图形
                var (dw, dh) = DefaultSize(armed.Type);
                var ctrl = AddControlAt(armed.Type,
                    SnapToGrid(start.X - dw / 2),
                    SnapToGrid(start.Y - dh / 2), dw, dh, null, armed.ShapeKind);
                ControlDrawn?.Invoke(this, new ControlDrawnEventArgs(ctrl));
                return;
            }

            // 拖拽：以拖拽矩形为边界（吸附网格 + 屏幕范围裁剪）
            var x = SnapToGrid(Math.Min(start.X, end.X));
            var y = SnapToGrid(Math.Min(start.Y, end.Y));
            w = SnapToGrid(w);
            h = SnapToGrid(h);
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x + w > screen.Width) w = screen.Width - x;
            if (y + h > screen.Height) h = screen.Height - y;
            w = Math.Max(MinControlSize, w);
            h = Math.Max(MinControlSize, h);

            var drawn = AddControlAt(armed.Type, x, y, w, h, null, armed.ShapeKind);
            ControlDrawn?.Invoke(this, new ControlDrawnEventArgs(drawn));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesignCanvas] 拖框绘制提交异常: {ex}");
        }
        finally
        {
            _isDrawing = false;
            InvalidateVisual();
        }
    }

    // ── 顶点/端点拖拽 ──

    /// <summary>
    /// 几何快照：几何图元拖一个顶点会同时改 X/Y/W/H（框=点集包围盒）或起止角，
    /// 撤销必须整组回滚 —— 只回滚框会让顶点留在新位置，形状与框错位。
    /// </summary>
    private readonly record struct GeometrySnapshot(
        double X, double Y, double Width, double Height, double MinValue, double MaxValue,
        IReadOnlyList<PointData>? Points)
    {
        public bool SameAs(GeometrySnapshot other)
        {
            if (Math.Abs(X - other.X) > 0.001 || Math.Abs(Y - other.Y) > 0.001 ||
                Math.Abs(Width - other.Width) > 0.001 || Math.Abs(Height - other.Height) > 0.001 ||
                Math.Abs(MinValue - other.MinValue) > 0.001 || Math.Abs(MaxValue - other.MaxValue) > 0.001)
                return false;

            var a = Points;
            var b = other.Points;
            if (a == null || b == null) return a == b && Points == other.Points;
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (Math.Abs(a[i].X - b[i].X) > 0.001 || Math.Abs(a[i].Y - b[i].Y) > 0.001)
                    return false;
            return true;
        }
    }

    private static GeometrySnapshot SnapshotGeometry(HmiControl control) => new(
        control.X, control.Y, control.Width, control.Height,
        control.MinValue, control.MaxValue,
        control.Points?.ToList());

    private static void RestoreGeometry(HmiControl control, GeometrySnapshot snapshot)
    {
        control.X = snapshot.X;
        control.Y = snapshot.Y;
        control.Width = snapshot.Width;
        control.Height = snapshot.Height;
        control.MinValue = snapshot.MinValue;
        control.MaxValue = snapshot.MaxValue;
        control.Points = snapshot.Points?.ToList();
    }

    private void BeginAnchorDrag(HmiControl control, int index)
    {
        _dragMode = DragMode.Vertex;
        _dragControl = control;
        _vertexIndex = index;
        _vertexStart = SnapshotGeometry(control);
    }

    private void ApplyAnchorDrag(Point viewportPoint, bool ortho = false)
    {
        if (_dragControl == null || _vertexIndex < 0) return;

        var dragModel = AnchorViewportToModel(_dragControl, viewportPoint);
        var model = new PointData(dragModel.X, dragModel.Y);
        List<AlignmentGuide> guides;

        if (ortho)
        {
            // Shift：只沿水平/垂直方向走，锚点取相邻顶点 —— 拖完这一条边就是正的水平/垂直线段
            var absolute = FigureGeometry.AbsolutePoints(_dragControl);
            var anchorIndex = FigureGeometry.OrthoAnchorIndex(_vertexIndex, absolute.Count);
            if (anchorIndex >= 0)
                model = FigureGeometry.ConstrainOrtho(absolute[anchorIndex], model);
            guides = new List<AlignmentGuide>();
        }
        else
        {
            (model, guides) = SnapAnchorPoint(_dragControl, _vertexIndex, model);
        }

        if (!FigureGeometry.TryMoveAnchor(_dragControl, _vertexIndex, model.X, model.Y, 0))
            return;

        PublishGuides(guides);
        ControlPropertyChanged?.Invoke(this, _dragControl);
    }

    /// <summary>顶点拖拽 / 按点绘制的对齐吸附容差（视口像素）——与控件拖动的参考线同一量级。</summary>
    private const double AnchorGuideThreshold = 5;

    /// <summary>
    /// 按点绘制时的吸附候选：其它控件的左/中/右与上/中/下 + 本图元已取的点 + 屏幕边与中线。
    /// （顶点拖拽不算"本图元已取的点"—— 那些在 <see cref="SnapAnchorPoint"/> 里单独收。）
    /// </summary>
    private (PointData Point, List<AlignmentGuide> Guides) SnapDrawPoint(PointData modelPoint)
    {
        var screen = Screen;
        var zoom = EffectiveZoom;
        var tolerance = AnchorGuideThreshold / Math.Max(zoom, 0.01);

        var candX = new List<double>();
        var candY = new List<double>();

        if (screen != null)
        {
            var map = BuildIndex();
            foreach (var (other, ox, oy) in FlattenedAbsolute(map))
            {
                candX.Add(ox);
                candX.Add(ox + other.Width / 2);
                candX.Add(ox + other.Width);
                candY.Add(oy);
                candY.Add(oy + other.Height / 2);
                candY.Add(oy + other.Height);
            }
            candX.Add(0);
            candX.Add(screen.Width / 2.0);
            candX.Add(screen.Width);
            candY.Add(0);
            candY.Add(screen.Height / 2.0);
            candY.Add(screen.Height);
        }

        if (_drawSession is { } session)
        {
            foreach (var p in session.Points)
            {
                candX.Add(p.X);
                candY.Add(p.Y);
            }
        }

        return SnapToCandidates(candX, candY, modelPoint, tolerance);
    }

    /// <summary>把候选坐标吸进 <paramref name="modelPoint"/>，并给出命中轴的参考线（视口坐标）。</summary>
    private (PointData Point, List<AlignmentGuide> Guides) SnapToCandidates(
        List<double> candX, List<double> candY, PointData modelPoint, double tolerance)
    {
        var screen = Screen;
        var zoom = EffectiveZoom;
        var guides = new List<AlignmentGuide>();

        var x = modelPoint.X;
        var y = modelPoint.Y;
        var hitX = FigureGeometry.TryNearest(candX, x, tolerance, out var snappedX);
        var hitY = FigureGeometry.TryNearest(candY, y, tolerance, out var snappedY);

        // 没命中对齐候选的轴才吸网格（对齐优先：贴隔壁控件比贴栅格更重要）
        var step = SnapEnabled ? SnapStep : 0;
        if (hitX) x = snappedX;
        else if (step > 0) x = Math.Round(x / step) * step;
        if (hitY) y = snappedY;
        else if (step > 0) y = Math.Round(y / step) * step;

        var width = screen?.Width ?? 800;
        var height = screen?.Height ?? 480;
        if (hitX)
            guides.Add(new AlignmentGuide(x * zoom + PanOffsetX, false,
                PanOffsetY, height * zoom + PanOffsetY));
        if (hitY)
            guides.Add(new AlignmentGuide(y * zoom + PanOffsetY, true,
                PanOffsetX, width * zoom + PanOffsetX));

        return (new PointData(x, y), guides);
    }

    /// <summary>把参考线同时交给选中层的绘制与画布的预览层（两处都读 <see cref="_currentGuides"/>）。</summary>
    private void PublishGuides(List<AlignmentGuide>? guides)
    {
        _currentGuides = guides ?? new List<AlignmentGuide>();
        if (Adorner == null) return;
        Adorner.AlignmentGuides = _currentGuides;
        Adorner.InvalidateVisual();
    }

    /// <summary>
    /// 顶点吸附：**对齐候选优先，其次网格**。候选 = 同一图元的其它顶点 + 其它控件的
    /// 左/中/右与上/中/下 + 屏幕边与中线。命中时返回要画的参考线（视口坐标），
    /// 于是"拖到与隔壁控件同一条竖线上"是看得见的，而不是靠眼力。
    /// </summary>
    private (PointData Point, List<AlignmentGuide> Guides) SnapAnchorPoint(
        HmiControl control, int index, PointData modelPoint)
    {
        var screen = Screen;
        var zoom = EffectiveZoom;
        var tolerance = AnchorGuideThreshold / Math.Max(zoom, 0.01);

        var candX = new List<double>();
        var candY = new List<double>();

        // 同一图元的其它顶点：拖管道时最常用（让下一段保持水平）
        var absolute = FigureGeometry.AbsolutePoints(control);
        for (var i = 0; i < absolute.Count; i++)
        {
            if (i == index) continue;
            candX.Add(absolute[i].X);
            candY.Add(absolute[i].Y);
        }

        if (screen != null)
        {
            var map = BuildIndex();
            foreach (var (other, ox, oy) in FlattenedAbsolute(map))
            {
                if (ReferenceEquals(other, control)) continue;
                candX.Add(ox);
                candX.Add(ox + other.Width / 2);
                candX.Add(ox + other.Width);
                candY.Add(oy);
                candY.Add(oy + other.Height / 2);
                candY.Add(oy + other.Height);
            }
            candX.Add(0);
            candX.Add(screen.Width / 2.0);
            candX.Add(screen.Width);
            candY.Add(0);
            candY.Add(screen.Height / 2.0);
            candY.Add(screen.Height);
        }

        return SnapToCandidates(candX, candY, modelPoint, tolerance);
    }

    /// <summary>顶点拖拽落一条撤销（只回滚几何：框 + 点集 + 起止角）。</summary>
    private void CommitAnchorDrag(HmiControl control)
    {
        if (_vertexStart is not { } before) return;
        var after = SnapshotGeometry(control);
        if (before.SameAs(after)) return;

        UndoRedoManager.Execute(new SnapshotCommand(
            AppLocalization.Service[Lang.UndoMoveVertex],
            () => RestoreGeometry(control, before),
            () => RestoreGeometry(control, after)));
    }

    // ── I10: 按点图元的取点与提交 ──

    /// <summary>
    /// 取一个点（视口坐标）。按下没移动 = 取按下位置；按下后拖动过 =
    /// 拖动开始时已取按下位置，这里再取抬起位置，于是"一拖"就得到两个端点。
    /// <paramref name="ortho"/> = 按住 Shift：把这一笔约束到"从上一个点出发的水平/垂直方向"。
    /// </summary>
    private void TakeDrawPoint(Point viewportPoint, bool ortho = false)
    {
        if (DrawTool is not { IsPointTool: true } tool) return;

        _drawHoverPoint = viewportPoint;
        _drawSession ??= new FigureDrawSession(tool);

        // ① 吸附：对齐候选（其它控件的边与中线、本图元已取的点、屏幕边与中线）优先，其次网格。
        //    没有这一步，鼠标落在管线旁边 2px 就会画出一条"几乎水平但不是水平"的线。
        var (modelPoint, guides) = SnapDrawPoint(modelPoint: ToModelData(viewportPoint));
        // ② Shift：主轴之外的坐标直接被锚点覆盖（约束 > 吸附）
        if (ortho && _drawSession.Points.Count > 0)
            modelPoint = FigureGeometry.ConstrainOrtho(_drawSession.Points[^1], modelPoint);

        PublishGuides(guides);

        var outcome = _drawSession.Click(modelPoint);

        if (outcome == FigureDrawOutcome.Commit)
        {
            var session = _drawSession;
            ResetDrawState();
            CommitPointShape(session!);
            return;
        }
        InvalidateVisual();
    }

    /// <summary>把会话里的点落成控件：点集装框（<see cref="FigureGeometry.Fit"/>），角度图元按直径两端定半圆。</summary>
    private void CommitPointShape(FigureDrawSession session)
    {
        Console.WriteLine($"[draw] commit {session.Tool.Type}/{session.Tool.Kind} " +
                          $"mode={session.Mode} n={session.Points.Count}");
        var screen = Screen;
        if (screen == null) return;

        try
        {
            var pts = session.Points
                .Select(p => new PointData(
                    Math.Clamp(p.X, 0, screen.Width),
                    Math.Clamp(p.Y, 0, screen.Height)))
                .ToList();
            if (FigureGeometry.IsDegenerate(session.Mode, pts)) return;

            var tool = session.Tool;
            string? kind = tool.Type == HmiControlType.Shape ? tool.ShapeKind : null;
            HmiControl drawn;

            if (session.Mode == FigurePointMode.Angle)
            {
                var arc = FigureGeometry.ArcFromEndpoints(pts[0], pts[1]);
                drawn = AddControlAt(tool.Type, arc.X, arc.Y, arc.W, arc.H, null, kind);
                // 两个端点就是起止角：面板上那一对行与画布手柄共用同一个来源
                drawn.MinValue = arc.Start;
                drawn.MaxValue = arc.End;
            }
            else
            {
                // 管道要按"装得下管径"的规则装框，其它图元就是纯包围盒
                var box = tool.Type == HmiControlType.Pipe
                    ? FigureGeometry.FitPipe(pts, FigureGeometry.DefaultPipeThickness)
                    : FigureGeometry.Fit(pts);
                drawn = AddControlAt(tool.Type, box.X, box.Y, box.Width, box.Height, box.Local, kind);
            }

            ControlDrawn?.Invoke(this, new ControlDrawnEventArgs(drawn));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesignCanvas] 按点图元提交异常: {ex}");
        }
    }

    /// <summary>
    /// 删除选中的控件（含容器子控件，带撤销）—— 层级感知版在
    /// <see cref="DeleteControlsWithHistory"/>，这里保留单入口语义。
    /// </summary>
    public void DeleteSelectedControl()
    {
        if (_selectedControls.Count == 0 || Screen == null) return;
        DeleteControlsWithHistory(_selectedControls);   // 结构通知在方法内部统一触发
    }

    // ── JSON 深拷贝辅助（用于控件复制）──
    private static T JsonClone<T>(T source) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(
            System.Text.Json.JsonSerializer.Serialize(source,
                HmiEditor.Core.Serialization.JsonOptionsDefaults.Default),
            HmiEditor.Core.Serialization.JsonOptionsDefaults.Default)!;

    // ── 复制粘贴功能 ──

    /// <summary>
    /// 复制当前选中的控件到内部剪贴板（Ctrl+C）。
    /// 保存时保留控件相对位置关系，粘贴时整体偏移。
    /// </summary>
    public void CopySelectedControls()
    {
        if (_selectedControls.Count == 0) return;

        // 深拷贝选中控件到剪贴板（不直接引用，避免修改源）
        _clipboardControls = _selectedControls.Select(c => JsonClone(c)).ToList();
        _pasteCount = 0; // 重置粘贴计数
    }

    /// <summary>
    /// 粘贴剪贴板中的控件到画布（Ctrl+V）。
    /// 相对于原位置偏移 PasteOffsetStep × 粘贴次数，避免完全重叠。
    /// 粘贴后自动选中新创建的控件。
    /// </summary>
    public IReadOnlyList<HmiControl>? PasteControls()
    {
        if (_clipboardControls == null || _clipboardControls.Count == 0 || Screen == null)
            return null;

        _pasteCount++;
        var offset = PasteOffsetStep * _pasteCount;

        // 计算原选中集的包围盒，用于生成唯一名称（避免重名）
        var existingNames = ContainerHierarchy.Flatten(Screen).Select(c => c.Name).ToHashSet();
        var index = BuildIndex();
        var newControls = new List<HmiControl>();

        foreach (var src in _clipboardControls)
        {
            var clone = JsonClone(src);
            // 生成新的唯一 ID
            clone.Id = Guid.NewGuid().ToString("N")[..8];
            // 子孙的 ParentId 仍指着旧 Id，重指到克隆后的新父（否则层级断链）
            ContainerHierarchy.RepointDescendants(clone);
            if (!string.IsNullOrEmpty(src.ParentId))
            {
                // 单独复制的子控件：还原成顶层控件（相对坐标 → 绝对坐标）
                var (ox, oy) = ContainerHierarchy.OriginOf(src, index);
                clone.X = SnapToGrid(src.X + ox + offset);
                clone.Y = SnapToGrid(src.Y + oy + offset);
                clone.ParentId = null;
            }
            else
            {
                // 位置偏移（整体粘贴偏移）
                clone.X = SnapToGrid(src.X + offset);
                clone.Y = SnapToGrid(src.Y + offset);
            }
            // 清除 GroupId（粘贴出来的控件默认不继承原组合，用户可重新组合）
            clone.GroupId = null;
            // 生成唯一名称（若重名则加序号后缀）
            clone.Name = MakeUniqueName(existingNames, clone.Name);
            existingNames.Add(clone.Name);

            Screen.Controls.Add(clone);
            newControls.Add(clone);
            ControlPropertyChanged?.Invoke(this, clone);
        }

        // 选中新粘贴的控件
        _selectedControls.Clear();
        _selectedControls.AddRange(newControls);
        FireSelectionChanged();
        UpdateMultiAdorner();
        InvalidateVisual();

        // 粘贴作为一个原子编辑事务进入共享撤销栈。
        var pasted = newControls.ToList();
        UndoRedoManager.Execute(new SnapshotCommand(
            AppLocalization.Service[pasted.Count == 1 ? Lang.UndoPasteControl : Lang.UndoPasteControls],
            () =>
            {
                foreach (var control in pasted) Screen.Controls.Remove(control);
                _selectedControls.RemoveAll(pasted.Contains);
                FireSelectionChanged();
                UpdateMultiAdorner();
                InvalidateVisual();
            },
            () =>
            {
                foreach (var control in pasted)
                    if (!Screen.Controls.Contains(control)) Screen.Controls.Add(control);
                _selectedControls.Clear();
                _selectedControls.AddRange(pasted);
                FireSelectionChanged();
                UpdateMultiAdorner();
                InvalidateVisual();
            }));

        return newControls.AsReadOnly();
    }

    /// <summary>生成唯一名称（避免与现有名称冲突）。</summary>
    private static string MakeUniqueName(HashSet<string> existingNames, string baseName)
    {
        if (string.IsNullOrEmpty(baseName)) baseName = "Control";
        if (!existingNames.Contains(baseName)) return baseName;

        var index = 2;
        while (existingNames.Contains($"{baseName}_{index}") || existingNames.Contains($"{baseName}{index}"))
            index++;
        // 优先用下划线风格，保持整洁
        return $"{baseName}_{index}";
    }

    /// <summary>
    /// 多重复制（Ctrl+D）：按指定方向和间距批量复制选中控件。
    /// </summary>
    /// <param name="count">复制数量（不含原件），≥1</param>
    /// <param name="spacingX">水平间距（像素，模型坐标）</param>
    /// <param name="spacingY">垂直间距（像素，模型坐标）</param>
    /// <returns>新创建的控件列表；失败返回 null</returns>
    public IReadOnlyList<HmiControl>? DuplicateSelected(int count, double spacingX, double spacingY)
    {
        if (count < 1 || _selectedControls.Count == 0 || Screen == null)
            return null;

        var existingNames = ContainerHierarchy.Flatten(Screen).Select(c => c.Name).ToHashSet();
        var index = BuildIndex();
        var allNew = new List<HmiControl>();

        for (var i = 1; i <= count; i++)
        {
            var dx = SnapToGrid(spacingX * i);
            var dy = SnapToGrid(spacingY * i);

            foreach (var src in _selectedControls.ToList())
            {
                var clone = JsonClone(src);
                clone.Id = Guid.NewGuid().ToString("N")[..8];
                ContainerHierarchy.RepointDescendants(clone);
                if (!string.IsNullOrEmpty(src.ParentId))
                {
                    // 单独复制的子控件：还原成顶层控件（相对坐标 → 绝对坐标）
                    var (ox, oy) = ContainerHierarchy.OriginOf(src, index);
                    clone.X = SnapToGrid(src.X + ox + dx);
                    clone.Y = SnapToGrid(src.Y + oy + dy);
                    clone.ParentId = null;
                }
                else
                {
                    clone.X = SnapToGrid(src.X + dx);
                    clone.Y = SnapToGrid(src.Y + dy);
                }
                clone.GroupId = null;
                clone.Name = MakeUniqueName(existingNames, src.Name);
                existingNames.Add(clone.Name);

                Screen.Controls.Add(clone);
                allNew.Add(clone);
                ControlPropertyChanged?.Invoke(this, clone);
            }
        }

        InvalidateVisual();

        var duplicated = allNew.ToList();
        UndoRedoManager.Execute(new SnapshotCommand(
            AppLocalization.Service[duplicated.Count == 1 ? Lang.UndoDuplicateControl : Lang.UndoDuplicateControls],
            () =>
            {
                foreach (var control in duplicated) Screen.Controls.Remove(control);
                _selectedControls.RemoveAll(duplicated.Contains);
                FireSelectionChanged();
                UpdateMultiAdorner();
                InvalidateVisual();
            },
            () =>
            {
                foreach (var control in duplicated)
                    if (!Screen.Controls.Contains(control)) Screen.Controls.Add(control);
                _selectedControls.Clear();
                _selectedControls.AddRange(duplicated);
                FireSelectionChanged();
                UpdateMultiAdorner();
                InvalidateVisual();
            }));

        return allNew.AsReadOnly();
    }

    // ── 组合与拆分功能 ──

    /// <summary>
    /// 将当前选中的多个控件组合为一个整体（Ctrl+G）。
    /// 组合后点击任意成员将自动选中整个组合的所有控件。
    /// 若已选中部分组合成员，则会将组合扩展为整个选中集。
    /// </summary>
    public string? GroupSelectedControls()
    {
        if (_selectedControls.Count < 2 || Screen == null) return null;

        var groupId = "grp_" + Guid.NewGuid().ToString("N")[..8];
        foreach (var ctrl in _selectedControls)
        {
            ctrl.GroupId = groupId;
            ControlPropertyChanged?.Invoke(this, ctrl);
        }

        InvalidateVisual();
        return groupId;
    }

    /// <summary>
    /// 拆分选中的组合（Ctrl+Shift+G）。
    /// 若选中集中包含多个组合，会一次性拆分所有涉及的组合。
    /// </summary>
    public int UngroupSelectedControls()
    {
        if (_selectedControls.Count == 0 || Screen == null) return 0;

        // 收集涉及的所有 GroupId（可能不止一个）
        var groupIds = _selectedControls
            .Select(c => c.GroupId)
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct()
            .ToHashSet();

        if (groupIds.Count == 0) return 0;

        // 在整屏范围内清除这些 GroupId（确保组合彻底拆分，
        // 即使部分成员不在选中集中也一并拆分，避免残留部分组合）
        var count = 0;
        foreach (var ctrl in Screen.Controls)
        {
            if (!string.IsNullOrEmpty(ctrl.GroupId) && groupIds.Contains(ctrl.GroupId))
            {
                ctrl.GroupId = null;
                count++;
                ControlPropertyChanged?.Invoke(this, ctrl);
            }
        }

        InvalidateVisual();
        return count;
    }

    // ── 多选对齐功能 ──

    /// <summary>多选对齐方向：6 种标准对齐方式。</summary>
    public enum MultiAlignDirection
    {
        /// <summary>左对齐：所有控件左边缘 = 锚点控件的左边缘</summary>
        Left,
        /// <summary>水平居中：所有控件中心 X = 锚点控件中心 X</summary>
        CenterHorizontal,
        /// <summary>右对齐：所有控件右边缘 = 锚点控件的右边缘</summary>
        Right,
        /// <summary>顶部对齐：所有控件上边缘 = 锚点控件的上边缘</summary>
        Top,
        /// <summary>垂直居中：所有控件中心 Y = 锚点控件中心 Y</summary>
        CenterVertical,
        /// <summary>底部对齐：所有控件下边缘 = 锚点控件的下边缘</summary>
        Bottom
    }

    /// <summary>
    /// 按指定方向对齐当前选中的多个控件。
    /// 锚点规则：以选中集中的「最后一个控件」为对齐基准（即用户最后选择的控件）。
    /// 至少需要 2 个选中控件才会生效。
    /// </summary>
    public bool AlignSelectedControls(MultiAlignDirection direction)
    {
        if (_selectedControls.Count < 2) return false;

        var anchor = _selectedControls[^1];
        var anchorRect = new Rect(anchor.X, anchor.Y, anchor.Width, anchor.Height);

        foreach (var ctrl in _selectedControls.Take(_selectedControls.Count - 1))
        {
            switch (direction)
            {
                case MultiAlignDirection.Left:
                    ctrl.X = SnapToGrid(anchorRect.X);
                    break;
                case MultiAlignDirection.CenterHorizontal:
                    ctrl.X = SnapToGrid(anchorRect.X + (anchorRect.Width - ctrl.Width) / 2);
                    break;
                case MultiAlignDirection.Right:
                    ctrl.X = SnapToGrid(anchorRect.X + anchorRect.Width - ctrl.Width);
                    break;
                case MultiAlignDirection.Top:
                    ctrl.Y = SnapToGrid(anchorRect.Y);
                    break;
                case MultiAlignDirection.CenterVertical:
                    ctrl.Y = SnapToGrid(anchorRect.Y + (anchorRect.Height - ctrl.Height) / 2);
                    break;
                case MultiAlignDirection.Bottom:
                    ctrl.Y = SnapToGrid(anchorRect.Y + anchorRect.Height - ctrl.Height);
                    break;
            }
            ControlPropertyChanged?.Invoke(this, ctrl);
        }

        UpdateMultiAdorner();
        InvalidateVisual();
        return true;
    }

    // ── 统一尺寸功能 ──

    /// <summary>
    /// 将所有选中控件的宽高统一为锚点控件（第一个选中控件）的尺寸。
    /// 至少需要 2 个选中控件才会生效。
    /// </summary>
    public bool MakeSameSize()
    {
        if (_selectedControls.Count < 2) return false;

        var anchor = _selectedControls[0];
        var targetW = anchor.Width;
        var targetH = anchor.Height;

        foreach (var ctrl in _selectedControls.Skip(1))
        {
            ctrl.Width = SnapToGrid(targetW);
            ctrl.Height = SnapToGrid(targetH);
            ControlPropertyChanged?.Invoke(this, ctrl);
        }

        UpdateMultiAdorner();
        InvalidateVisual();
        return true;
    }

    // ── 等距排列功能 ──

    /// <summary>
    /// 水平等距排列：使选中的多个控件在水平方向上间距相等。
    /// 算法：保持最左和最右控件位置不变，其余控件按 X 坐标线性插值均匀分布。
    /// 至少需要 3 个控件才会生效（2 个控件间距唯一，无需调整）。
    /// </summary>
    public bool DistributeSelectedHorizontal()
    {
        if (_selectedControls.Count < 3) return false;

        // 按当前 X 坐标排序
        var sorted = _selectedControls.OrderBy(c => c.X).ToList();
        var leftmost = sorted[0];
        var rightmost = sorted[^1];

        // 总可用空间 = 最右控件左边缘 - 最左控件左边缘
        var totalGap = rightmost.X - leftmost.X;
        // 每个间距 = (总空间 - 除最右控件外所有控件宽度) / 间隔数
        // 注意：须减去包含最左在内的全部中间控件宽度，否则 gap 偏大、
        //       导致最后一组空隙被压缩（与前面的空隙不一致）。
        var gapCount = sorted.Count - 1;
        var gap = (totalGap - sorted.Take(gapCount).Sum(c => c.Width)) / gapCount;

        // 从左到右依次排布：误差补偿避免 SnapToGrid 取整误差累积到最后一组
        var currentX = leftmost.X + leftmost.Width; // 最左控件右边缘
        var carry = 0.0; // 本次取整丢掉的余量，累加到下一个控件补偿
        for (var i = 1; i < sorted.Count - 1; i++)
        {
            var ctrl = sorted[i];
            var target = currentX + gap + carry;
            var snapped = SnapToGrid(target);
            ctrl.X = snapped;
            carry = target - snapped;
            currentX = snapped + ctrl.Width;
            ControlPropertyChanged?.Invoke(this, ctrl);
        }

        UpdateMultiAdorner();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// 垂直等距排列：使选中的多个控件在垂直方向上间距相等。
    /// 算法：保持最上和最下控件位置不变，其余控件按 Y 坐标线性插值均匀分布。
    /// 至少需要 3 个控件才会生效。
    /// </summary>
    public bool DistributeSelectedVertical()
    {
        if (_selectedControls.Count < 3) return false;

        // 按当前 Y 坐标排序
        var sorted = _selectedControls.OrderBy(c => c.Y).ToList();
        var topmost = sorted[0];
        var bottommost = sorted[^1];

        var totalGap = bottommost.Y - topmost.Y;
        var gapCount = sorted.Count - 1;
        // 与水平方向同理：须减去包含最上在内的全部中间控件高度，否则 gap 偏大，
        // 导致最后一组空隙被压缩（与前面的空隙不一致）。
        var gap = (totalGap - sorted.Take(gapCount).Sum(c => c.Height)) / gapCount;

        // 从上到下依次排布：误差补偿避免 SnapToGrid 取整误差累积到最后一组
        var currentY = topmost.Y + topmost.Height; // 最上控件下边缘
        var carry = 0.0;
        for (var i = 1; i < sorted.Count - 1; i++)
        {
            var ctrl = sorted[i];
            var target = currentY + gap + carry;
            var snapped = SnapToGrid(target);
            ctrl.Y = snapped;
            carry = target - snapped;
            currentY = snapped + ctrl.Height;
            ControlPropertyChanged?.Invoke(this, ctrl);
        }

        UpdateMultiAdorner();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// 判断剪贴板是否有可粘贴内容（供菜单/按钮禁用状态判断）。
    /// </summary>
    public bool HasClipboardContent => _clipboardControls != null && _clipboardControls.Count > 0;

    /// <summary>
    /// 当前选中控件数 ≥ 2 时返回 true（组合/对齐的前置条件）。
    /// </summary>
    public bool HasMultiSelection => _selectedControls.Count >= 2;

    /// <summary>
    /// 当前选中集中至少有一个控件属于某个组合时返回 true。
    /// </summary>
    public bool SelectionContainsGroup => _selectedControls.Any(c => !string.IsNullOrEmpty(c.GroupId));

    // ── 对齐方式 ──

    /// <summary>对齐方式枚举。</summary>
    public enum AlignmentType
    {
        Left, Right, Top, Bottom,
        CenterHorizontal, CenterVertical
    }

    /// <summary>静态对齐方法（用于单元测试，不涉及 UndoRedoManager）。</summary>
    public static void AlignControlStatic(HmiControl control, Rect targetBounds, AlignmentType alignment)
    {
        switch (alignment)
        {
            case AlignmentType.Left:
                control.X = targetBounds.X;
                break;
            case AlignmentType.Right:
                control.X = targetBounds.X + targetBounds.Width - control.Width;
                break;
            case AlignmentType.Top:
                control.Y = targetBounds.Y;
                break;
            case AlignmentType.Bottom:
                control.Y = targetBounds.Y + targetBounds.Height - control.Height;
                break;
            case AlignmentType.CenterHorizontal:
                control.X = targetBounds.X + (targetBounds.Width - control.Width) / 2;
                break;
            case AlignmentType.CenterVertical:
                control.Y = targetBounds.Y + (targetBounds.Height - control.Height) / 2;
                break;
        }
    }

    // ── 对齐参考线检测 ──

    /// <summary>对齐检测阈值（像素）。</summary>
    private const double GuideThreshold = 5;

    /// <summary>
    /// 整棵树的「绝对坐标 + 控件」展平清单（参考线比对用）。
    /// 容器子控件不进平铺列表，只扫 <see cref="HmiScreen.Controls"/> 会漏掉它们。
    /// </summary>
    private List<(HmiControl Control, double X, double Y)> FlattenedAbsolute(
        Dictionary<string, HmiControl> index)
    {
        var list = new List<(HmiControl, double, double)>();
        var screen = Screen;
        if (screen == null) return list;
        void Walk(HmiControl control, double ox, double oy)
        {
            list.Add((control, control.X + ox, control.Y + oy));
            foreach (var child in control.Children) Walk(child, control.X + ox, control.Y + oy);
        }
        foreach (var root in RootControls(screen)) Walk(root, 0, 0);
        return list;
    }

    /// <summary>检测并生成对齐参考线（视口坐标）。位置一律用屏幕绝对坐标算。</summary>
    private List<AlignmentGuide> DetectAlignmentGuides(HmiControl dragged, Point currentPointerPos)
    {
        var screen = Screen;
        if (screen == null) return new List<AlignmentGuide>();

        var z = EffectiveZoom;
        var guides = new List<AlignmentGuide>();
        var index = BuildIndex();
        var draggedAbs = AbsoluteRectOf(dragged, index);

        // 拖拽控件的 6 个关键位置（视图坐标，已缩放+平移）
        var dl = draggedAbs.X * z + PanOffsetX;           // left
        var dr = (draggedAbs.X + draggedAbs.Width) * z + PanOffsetX;  // right
        var dc = (draggedAbs.X + draggedAbs.Width / 2) * z + PanOffsetX; // center X
        var dt = draggedAbs.Y * z + PanOffsetY;           // top
        var db = (draggedAbs.Y + draggedAbs.Height) * z + PanOffsetY; // bottom
        var dcy = (draggedAbs.Y + draggedAbs.Height / 2) * z + PanOffsetY; // center Y

        // 检查与其他控件的对齐（同一次拖拽选中的控件之间不互相吸）
        foreach (var (other, ox, oy) in FlattenedAbsolute(index))
        {
            if (_selectedControls.Contains(other)) continue;

            var ol = ox * z + PanOffsetX;
            var or = (ox + other.Width) * z + PanOffsetX;
            var oc = (ox + other.Width / 2) * z + PanOffsetX;
            var ot = oy * z + PanOffsetY;
            var ob = (oy + other.Height) * z + PanOffsetY;
            var ocy = (oy + other.Height / 2) * z + PanOffsetY;

            // 垂直对齐检测（水平参考线）
            TryAddGuide(guides, dt, ot, dl, dr, true);
            TryAddGuide(guides, db, ob, dl, dr, true);
            TryAddGuide(guides, dcy, ocy, dl, dr, true);

            // 水平对齐检测（垂直参考线）
            TryAddGuide(guides, dl, ol, dt, db, false);
            TryAddGuide(guides, dr, or, dt, db, false);
            TryAddGuide(guides, dc, oc, dt, db, false);
        }

        // 检查与屏幕边界的对齐
        var sLeft = 0.0 + PanOffsetX;
        var sRight = screen.Width * z + PanOffsetX;
        var sCenterX = screen.Width * z / 2 + PanOffsetX;
        var sTop = 0.0 + PanOffsetY;
        var sBottom = screen.Height * z + PanOffsetY;
        var sCenterY = screen.Height * z / 2 + PanOffsetY;

        TryAddGuide(guides, dl, sLeft, dt, db, false);
        TryAddGuide(guides, dr, sRight, dt, db, false);
        TryAddGuide(guides, dc, sCenterX, dt, db, false);
        TryAddGuide(guides, dt, sTop, dl, dr, true);
        TryAddGuide(guides, db, sBottom, dl, dr, true);
        TryAddGuide(guides, dcy, sCenterY, dl, dr, true);

        return guides;
    }

    /// <summary>尝试添加参考线：如果 draggedPos 与 targetPos 在阈值内，添加参考线并吸附拖拽控件。</summary>
    private void TryAddGuide(List<AlignmentGuide> guides, double draggedPos, double targetPos,
        double start, double end, bool isHorizontal)
    {
        if (Math.Abs(draggedPos - targetPos) > GuideThreshold) return;

        guides.Add(new AlignmentGuide(targetPos, isHorizontal, start, end));
    }

    /// <summary>
    /// 按对齐参考线吸附拖拽控件的**新绝对位置**（屏幕绝对坐标里比，父子控件一视同仁）。
    /// 只动 <paramref name="abs"/> 里主控件的条目，写回（绝对 → 相对）由调用方做。
    /// </summary>
    private void SnapToGuidesAbsolute(
        Dictionary<HmiControl, (double X, double Y)> abs, HmiControl control,
        Dictionary<string, HmiControl> index)
    {
        var screen = Screen;
        if (screen == null || !abs.TryGetValue(control, out var pos)) return;

        var z = EffectiveZoom;
        var tol = GuideThreshold / z;
        var w = control.Width;
        var h = control.Height;

        // 与其他控件的边缘/中心对齐（绝对坐标）
        foreach (var (other, ox, oy) in FlattenedAbsolute(index))
        {
            if (_selectedControls.Contains(other)) continue;

            // 左边缘对齐
            if (Math.Abs(pos.X - ox) < tol) pos.X = ox;
            // 右边缘对齐
            if (Math.Abs(pos.X + w - ox - other.Width) < tol) pos.X = ox + other.Width - w;
            // 中心 X 对齐
            if (Math.Abs(pos.X + w / 2 - ox - other.Width / 2) < tol) pos.X = ox + (other.Width - w) / 2;
            // 顶部对齐
            if (Math.Abs(pos.Y - oy) < tol) pos.Y = oy;
            // 底部对齐
            if (Math.Abs(pos.Y + h - oy - other.Height) < tol) pos.Y = oy + other.Height - h;
            // 中心 Y 对齐
            if (Math.Abs(pos.Y + h / 2 - oy - other.Height / 2) < tol) pos.Y = oy + (other.Height - h) / 2;
        }

        // 屏幕边界对齐（绝对坐标）
        if (Math.Abs(pos.X - 0) < tol) pos.X = 0;
        if (Math.Abs(pos.X + w - screen.Width) < tol) pos.X = screen.Width - w;
        if (Math.Abs(pos.X + w / 2 - screen.Width / 2) < tol) pos.X = (screen.Width - w) / 2.0;
        if (Math.Abs(pos.Y - 0) < tol) pos.Y = 0;
        if (Math.Abs(pos.Y + h - screen.Height) < tol) pos.Y = screen.Height - h;
        if (Math.Abs(pos.Y + h / 2 - screen.Height / 2) < tol) pos.Y = (screen.Height - h) / 2.0;

        abs[control] = pos;
    }
}
