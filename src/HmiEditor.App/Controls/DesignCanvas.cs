using Avalonia;
using HmiEditor.App.Design;
using HmiEditor.App.Resources;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using HmiEditor.Core.Design;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using System.IO;
using System.Linq;

namespace HmiEditor.App.Controls;

/// <summary>
/// 设计画布核心控件 —— 控件层的交互逻辑。
/// 使用三层叠加模型：SnapGrid(底层) + DesignCanvas(控件层) + SelectionAdorner(顶层装饰)。
/// 负责：控件渲染、选中、拖拽移动、缩放手柄操作。
/// <para>
/// 2026-09-14 按域拆为 partial 分部，本文件承载依赖属性与字段、渲染主循环与控件分发、
/// 颜色解析，以及文件末尾的事件参数类；其余分部：
/// <c>DesignCanvas.Renderers.cs</c>（基本/图形控件渲染器与绘制 helper）、
/// <c>DesignCanvas.IndustrialRenderers.cs</c>（SVG 与工业设备图形）、
/// <c>DesignCanvas.Input.cs</c>（指针交互、命中测试、选择、拖放）、
/// <c>DesignCanvas.Editing.cs</c>（控件创建、绘制工具、剪贴板、对齐、辅助线）。
/// </para>
/// </summary>
public partial class DesignCanvas : Control
{
    // ── 依赖属性 ──

    public static readonly StyledProperty<HmiScreen?> ScreenProperty =
        AvaloniaProperty.Register<DesignCanvas, HmiScreen?>(nameof(Screen));

    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<DesignCanvas, double>(nameof(ZoomLevel), 1.0);

    /// <summary>
    /// 画布平移偏移量 X（视图坐标像素）。
    /// 作为 StyledProperty 可被 Avalonia 动画系统驱动，实现缩放/平移的平滑过渡。
    /// </summary>
    /// <summary>
    /// **屏幕 DPI 缩放**（阶段 222，登记 `#98` 的后半）：设备密度 ≠ 1 时，画布按
    /// <c>ZoomLevel × DpiScale</c>（见 <see cref="EffectiveZoom"/>）渲染并换算坐标 ——
    /// 渲染与命中共用同一个有效缩放，所以**拖拽不会错位**。默认 1.0（既有工程与判据不受影响）。
    /// </summary>
    public static readonly StyledProperty<double> DpiScaleProperty =
        AvaloniaProperty.Register<DesignCanvas, double>(nameof(DpiScale), 1.0);

    /// <summary>
    /// **屏幕安全区内边距**（模型坐标像素，左/上/右/下）—— 阶段 221 给 `#98` 的"接线"：
    /// 全零表示不画参考线；非零时画布按它画一圈**虚线参考线**（只影响显示，不动几何）。
    /// </summary>
    public static readonly StyledProperty<Thickness> SafePaddingProperty =
        AvaloniaProperty.Register<DesignCanvas, Thickness>(nameof(SafePadding));

    /// <summary>
    /// 底图来源：false = 现有 C# 自绘（默认，所见非所得的那些近似外观）；true = 画
    /// <see cref="HostFrame"/> 里的真机位图。切到真机位图后控件不再自绘 —— 两层同时画会重影。
    /// </summary>
    public static readonly StyledProperty<bool> UseHostFrameProperty =
        AvaloniaProperty.Register<DesignCanvas, bool>(nameof(UseHostFrame), defaultValue: true);

    /// <summary>
    /// 宿主渲染出的一帧（整屏、Bgra8888）。由预览客户端在后台线程换引用，
    /// UI 线程只在下次 Render 时读一次。位置的唯一依据仍是编辑模型，不从位图回读。
    /// </summary>
    public static readonly StyledProperty<IImage?> HostFrameProperty =
        AvaloniaProperty.Register<DesignCanvas, IImage?>(nameof(HostFrame));

    public static readonly StyledProperty<double> PanOffsetXProperty =
        AvaloniaProperty.Register<DesignCanvas, double>(nameof(PanOffsetX), 0.0);

    /// <summary>
    /// 画布平移偏移量 Y（视图坐标像素）。
    /// 作为 StyledProperty 可被 Avalonia 动画系统驱动，实现缩放/平移的平滑过渡。
    /// </summary>
    public static readonly StyledProperty<double> PanOffsetYProperty =
        AvaloniaProperty.Register<DesignCanvas, double>(nameof(PanOffsetY), 0.0);

    public static readonly StyledProperty<SelectionAdorner?> AdornerProperty =
        AvaloniaProperty.Register<DesignCanvas, SelectionAdorner?>(nameof(Adorner));

    // ── R2-T2: 预览模式（设计时数据预览）──
    public static readonly StyledProperty<bool> IsPreviewModeProperty =
        AvaloniaProperty.Register<DesignCanvas, bool>(nameof(IsPreviewMode));

    // ── I9: 分辨率预览 —— "WxH" 目标分辨率；null 表示关闭自适应预览 ──
    public static readonly StyledProperty<string?> PreviewResolutionProperty =
        AvaloniaProperty.Register<DesignCanvas, string?>(nameof(PreviewResolution));

    /// <summary>
    /// 项目主题（HmiTheme）—— 注入设计器渲染层后，兜底色/文本色等主题语义色
    /// 优先取主题值，未设置时回退 DesignPalette 默认值。null 表示使用默认调色板。
    /// </summary>
    public static readonly new StyledProperty<HmiTheme?> ThemeProperty =
        AvaloniaProperty.Register<DesignCanvas, HmiTheme?>(nameof(Theme));

    internal const double MinControlSize = 10;

    /// <summary>
    /// 吸附步长的出厂默认值（像素）。
    /// <para>等于本批次接线之前 <c>SnapThreshold</c> 的硬编码值 —— 默认行为逐像素不变；
    /// 用户值由 <c>DesignCanvasView</c> 从应用级设置（<c>SettingsService.Interaction.SnapStep</c>）注入。</para>
    /// </summary>
    internal const double DefaultSnapStep = 5;

    private double _snapStep = DefaultSnapStep;
    private bool _snapEnabled = true;

    /// <summary>
    /// 拖拽/缩放时的吸附步长（像素）。设为非正数时回退 <see cref="DefaultSnapStep"/>。
    /// </summary>
    public double SnapStep
    {
        get => _snapStep;
        set => _snapStep = value > 0 ? value : DefaultSnapStep;
    }

    /// <summary>是否启用吸附。关闭后 <c>SnapToGrid</c> 原样返回，拖拽不再对齐。</summary>
    public bool SnapEnabled
    {
        get => _snapEnabled;
        set => _snapEnabled = value;
    }

    /// <summary>拖拽数据格式标识 —— 用于 Toolbox → DesignCanvas 的控件拖放。</summary>
    public const string DragFormat = "HmiControlType";

    /// <summary>强类型拖拽格式（进程内）—— Avalonia 12.1 使用 DataFormat 替代字符串格式。</summary>
    public static readonly DataFormat<string> DragDataFormat =
        DataFormat.CreateInProcessFormat<string>(DragFormat);

    /// <summary>模板拖拽数据格式标识 —— 用于 FaceplateLibrary → DesignCanvas 的模板拖放。</summary>
    public const string TemplateDragFormat = "HmiTemplateId";

    /// <summary>强类型模板拖拽格式（进程内）—— 传递模板 ID 字符串。</summary>
    public static readonly DataFormat<string> TemplateDragDataFormat =
        DataFormat.CreateInProcessFormat<string>(TemplateDragFormat);

    /// <summary>资源拖拽数据格式标识 —— 用于 ResourceLibrary → DesignCanvas 的资源拖放。</summary>
    public const string ResourceDragFormat = "HmiResourceId";

    /// <summary>强类型资源拖拽格式（进程内）—— 传递资源 ID 字符串。</summary>
    public static readonly DataFormat<string> ResourceDragDataFormat =
        DataFormat.CreateInProcessFormat<string>(ResourceDragFormat);

    public HmiScreen? Screen
    {
        get => GetValue(ScreenProperty);
        set => SetValue(ScreenProperty, value);
    }

    /// <summary>是否用宿主位图当底图（见 <see cref="UseHostFrameProperty"/>）。</summary>
    public bool UseHostFrame
    {
        get => GetValue(UseHostFrameProperty);
        set => SetValue(UseHostFrameProperty, value);
    }

    /// <summary>宿主渲染的整屏帧（见 <see cref="HostFrameProperty"/>）。</summary>
    public IImage? HostFrame
    {
        get => GetValue(HostFrameProperty);
        set => SetValue(HostFrameProperty, value);
    }

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    /// <summary>屏幕 DPI 缩放（见 <see cref="DpiScaleProperty"/>）。</summary>
    public double DpiScale
    {
        get => GetValue(DpiScaleProperty);
        set => SetValue(DpiScaleProperty, value);
    }

    /// <summary>
    /// **有效缩放** ＝ 用户缩放 × DPI 缩放（DPI 值非正时按 1 处理，避免除零）。
    /// 画布的**渲染与坐标换算都用它** —— 两边必须成对，否则拖拽会错位。
    /// </summary>
    public double EffectiveZoom
    {
        get
        {
            var dpi = DpiScale;
            return ZoomLevel * (dpi > 0 ? dpi : 1.0);
        }
    }

    /// <summary>视口坐标 → 模型坐标（判据/外部用；与内部命中测试走同一个 <c>Coordinates</c>）。</summary>
    public Point ViewportToModel(Point viewportPoint) => Coordinates.ScreenToModel(viewportPoint);

    /// <summary>模型坐标 → 视口坐标。</summary>
    public Point ModelToViewport(Point modelPoint) => Coordinates.ModelToScreen(modelPoint);

    /// <summary>屏幕安全区内边距（见 <see cref="SafePaddingProperty"/>）。</summary>
    public Thickness SafePadding
    {
        get => GetValue(SafePaddingProperty);
        set => SetValue(SafePaddingProperty, value);
    }

    /// <summary>画布平移偏移量 X（视图坐标像素）。</summary>
    public double PanOffsetX
    {
        get => GetValue(PanOffsetXProperty);
        set => SetValue(PanOffsetXProperty, value);
    }

    /// <summary>画布平移偏移量 Y（视图坐标像素）。</summary>
    public double PanOffsetY
    {
        get => GetValue(PanOffsetYProperty);
        set => SetValue(PanOffsetYProperty, value);
    }

    /// <summary>I9: 分辨率预览（如 "1024x600"），null 关闭。</summary>
    public string? PreviewResolution
    {
        get => GetValue(PreviewResolutionProperty);
        set => SetValue(PreviewResolutionProperty, value);
    }

    public SelectionAdorner? Adorner
    {
        get => GetValue(AdornerProperty);
        set => SetValue(AdornerProperty, value);
    }

    /// <summary>项目主题（见 <see cref="ThemeProperty"/>），null = 使用默认调色板。</summary>
    public new HmiTheme? Theme
    {
        get => GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    /// <summary>R2-T2: 预览模式 —— 预览模式下禁用编辑，仅显示模拟数据。</summary>
    public bool IsPreviewMode
    {
        get => GetValue(IsPreviewModeProperty);
        set => SetValue(IsPreviewModeProperty, value);
    }

    /// <summary>
    /// R2-T2: 预览值提供器 —— 变量ID → 模拟值 的查找委托。
    /// 非空时，预览模式下绑定变量的控件渲染此委托返回的模拟值。
    /// 该委托只读，绝不写回模型（适应度函数 R2-FF：预览模式隔离 0 写操作）。
    /// </summary>
    public Func<string, double?>? PreviewValueProvider { get; set; }

    /// <summary>
    /// R2-T2: 预览变量类型提供器 —— 变量ID → 数据类型（"Bool"/"Int16"/"Float32"/"String" 等）的查找委托。
    /// 供预览值按变量类型格式化（如 Bool 显示 ON/OFF、整数不显示小数）。
    /// </summary>
    public Func<string, string?>? PreviewValueTypeProvider { get; set; }

    /// <summary>
    /// 预览变量单位提供器 —— 变量ID → 单位（如 "℃"/"%"）的查找委托。
    /// 控件未配置 ValueSuffix 时作为后缀兜底，使设计器预览与生成代码显示一致。
    /// </summary>
    public Func<string, string?>? PreviewValueUnitProvider { get; set; }

    // ── 内部状态 ──

    private enum DragMode { None, Move, Resize }
    private DragMode _dragMode = DragMode.None;

    private readonly List<HmiControl> _selectedControls = new();

    /// <summary>当前选中的控件集合（只读视图，外部可主动查询，不再仅能被动收 SelectionChanged 事件）。</summary>
    public IReadOnlyList<HmiControl> SelectedControls => _selectedControls;

    /// <summary>当前选中的第一个控件（兼容旧接口）。</summary>
    private HmiControl? _selectedControl => _selectedControls.Count > 0 ? _selectedControls[0] : null;

    private HmiControl? _dragControl;
    private Point _dragStartPoint;
    private Rect _dragStartBounds;
    private int _activeHandleIndex = -1;

    // ── 多选整体拖拽：记录拖拽起始时所有选中控件的原始位置 ──
    /// <summary>相对坐标快照（撤销恢复直接写回模型）。</summary>
    private Dictionary<HmiControl, Rect>? _dragStartAllBounds;

    /// <summary>屏幕绝对坐标快照（拖拽换算用：容器子控件与顶层控件同一套）。</summary>
    private Dictionary<HmiControl, Rect>? _dragStartAbsolute;

    private bool _historyReplaying;

    // 工具箱拖放预览：仅保存视图态，不写入工程模型。
    private readonly CanvasDragPreviewState _dragPreview = new();

    // 图片位图缓存：AssetItem.Id → 已加载位图（加载失败缓存 null 避免重复尝试）
    private readonly Dictionary<string, Bitmap?> _imageCache = new();

    // 平移偏移量（已迁移至 StyledProperty PanOffsetX/PanOffsetY）
    private bool _isPanning;
    private Point _panStartPoint;

    // 对齐参考线（拖拽过程中有效）
    private List<AlignmentGuide> _currentGuides = new();

    // 框选相关
    private bool _isRubberBanding;
    private Point _rubberBandStart;
    private Rect? _rubberBandRect;

    // ── 长按框选：空白区域按住左键达到阈值后从按下位置开始框选 ──
    /// <summary>长按触发框选的延迟（毫秒）。</summary>
    private static readonly TimeSpan LongPressDelay = TimeSpan.FromMilliseconds(400);
    /// <summary>长按判定前允许的指针移动容差（像素，视图坐标），防止轻微抖动误触发。</summary>
    private const double LongPressMoveTolerance = 6;
    private DispatcherTimer? _longPressTimer;
    private Point _longPressStart;
    private bool _longPressPending;

    // ── 复制粘贴剪贴板（进程内内存存储，支持多控件）──
    private List<HmiControl>? _clipboardControls;
    /// <summary>粘贴偏移量（每次粘贴递增，避免完全重叠）。</summary>
    private const double PasteOffsetStep = 20.0;
    private int _pasteCount;

    /// <summary>选中控件变化事件（通知 PropertyPanel、OutlinePanel）。传递所有选中的控件。</summary>
    public event EventHandler<IReadOnlyList<HmiControl>>? SelectionChanged;

    /// <summary>控件属性变化事件（通知重绘）。</summary>
    public event EventHandler<HmiControl>? ControlPropertyChanged;

    /// <summary>
    /// 通知控件属性已变化（外部修改如预览软键盘输入后调用），
    /// 触发 <see cref="ControlPropertyChanged"/> 并重绘。
    /// </summary>
    public void NotifyControlChanged(HmiControl control)
    {
        ControlPropertyChanged?.Invoke(this, control);
        InvalidateVisual();
    }

    /// <summary>
    /// 控件拖放事件 —— 从工具箱拖拽控件到画布时触发，
    /// 携带控件类型和放置坐标（模型坐标，已补偿缩放和平移）。
    /// </summary>
    public event EventHandler<ControlDroppedEventArgs>? ControlDropped;

    /// <summary>
    /// 模板拖放事件 —— 从模板库拖拽模板到画布时触发，
    /// 携带模板 ID 和放置坐标（模型坐标，已补偿缩放和平移）。
    /// </summary>
    public event EventHandler<TemplateDroppedEventArgs>? TemplateDropped;

    /// <summary>
    /// 资源拖放事件 —— 从图库拖拽资源到画布时触发，
    /// 携带资源 ID 和放置坐标（模型坐标，已补偿缩放和平移）。
    /// </summary>
    public event EventHandler<ResourceDroppedEventArgs>? ResourceDropped;

    /// <summary>
    /// 预览模式控件点击事件 —— 预览交互时触发，宿主负责执行控件动作。
    /// </summary>
    public event EventHandler<HmiControl>? PreviewControlClicked;

    /// <summary>
    /// I10: 绘图提交事件 —— 画布上用绘图工具拖拽/点击绘制完成时触发。
    /// 携带绘制的几何（模型坐标 + 顶点），宿主负责包装为撤销命令并刷新面板。
    /// </summary>
    public event EventHandler<ControlDrawnEventArgs>? ControlDrawn;

    /// <summary>
    /// I10: 当前激活的绘图工具（控件类型）。null = 普通选择/编辑模式。
    /// 非 null 时，画布上的按下/拖拽/释放用于"画图"而非选中/移动控件：
    ///   拖拽 → 以拖拽矩形为边界创建对应图形；单击 → 在点击处创建默认尺寸图形。
    /// </summary>
    public HmiControlType? DrawTool
    {
        get => GetValue(DrawToolProperty);
        set => SetValue(DrawToolProperty, value);
    }

    public static readonly StyledProperty<HmiControlType?> DrawToolProperty =
        AvaloniaProperty.Register<DesignCanvas, HmiControlType?>(nameof(DrawTool));

    /// <summary>工具切换/退出时，先提交未完成的点击式绘制（折线/直线顶点序列）。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DrawToolProperty)
            FlushClickDraw();
    }

    // ── I10: 绘图过程状态（DrawTool 激活时有效）──
    private Point _drawStartPoint;
    private Point _drawCurrentPoint;
    private bool _isDrawing;

    // I10: 点击式绘制（Line / Polyline）—— 每次左键单击追加一个顶点，
    // 移动鼠标显示"当前点 → 鼠标"的橡皮筋预览；Line 第二击提交，
    // Polyline 持续追加直到退出工具（ESC / 切换工具）时提交。
    private readonly List<Point> _clickPoints = new();
    private Point _clickHoverPoint;

    /// <summary>
    /// 撤销/重做管理器。
    /// </summary>
    public UndoRedoManager UndoRedoManager { get; set; } = null!;

    static DesignCanvas()
    {
        AffectsRender<DesignCanvas>(ScreenProperty, ZoomLevelProperty, DpiScaleProperty, SafePaddingProperty, PanOffsetXProperty,
            PanOffsetYProperty, ThemeProperty, IsPreviewModeProperty, PreviewResolutionProperty,
            DrawToolProperty, UseHostFrameProperty, HostFrameProperty);
        ClipToBoundsProperty.OverrideDefaultValue<DesignCanvas>(true);

        // 注册 DragDrop 类处理器 —— 画布作为拖放目标
        DragDrop.DragOverEvent.AddClassHandler<DesignCanvas>((c, e) => c.HandleDragOver(e));
        DragDrop.DropEvent.AddClassHandler<DesignCanvas>((c, e) => c.HandleDrop(e));
    }

    public DesignCanvas() : this(null)
    {
    }

    public DesignCanvas(UndoRedoManager? undoRedoManager)
    {
        UndoRedoManager = undoRedoManager ?? new UndoRedoManager();
        DragDrop.SetAllowDrop(this, true);

        // 画布可聚焦：保证点击画布后焦点从 TextBox 等控件转移到这里，
        // 窗口级 Delete/Ctrl+A 快捷键才能按预期生效
        Focusable = true;

        // 宿主位图放大时不插值：1×/2× 下与设备像素一一对应，否则文字发虚、选中框看着偏
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var screen = Screen;
        if (screen == null) return;

        // I9: 自适应分辨率预览 —— 解析目标分辨率并按屏幕模式计算变换
        var previewRes = AdaptiveLayoutCalculator.TryParseResolution(PreviewResolution);
        AdaptiveLayoutCalculator.AdaptiveTransform? adaptive = null;
        if (previewRes.HasValue && screen.AdaptiveMode != ScreenAdaptiveMode.None)
        {
            adaptive = AdaptiveLayoutCalculator.Compute(
                screen.Width, screen.Height,
                previewRes.Value.Width, previewRes.Value.Height,
                screen.AdaptiveMode);
        }

        // 应用平移偏移
        using (context.PushTransform(
            Matrix.CreateTranslation(PanOffsetX, PanOffsetY)))
        {
            // 绘制屏幕边界（预览时显示目标分辨率）
            var screenRect = adaptive.HasValue
                ? new Rect(0, 0, previewRes!.Value.Width * EffectiveZoom, previewRes.Value.Height * EffectiveZoom)
                : new Rect(0, 0, screen.Width * EffectiveZoom, screen.Height * EffectiveZoom);
            // 屏幕背景：未配置 BgColor 时保持透明（透出网格/画布底色），符合"背景透明"期望；
            // 配置了 BgColor（如白色 #FFFFFF）才填充，保证与运行时/模拟器一致。
            if (!string.IsNullOrEmpty(screen.BgColor))
            {
                var screenBg = ParseColor(screen.BgColor, DesignPalette.ScreenBgFallback);
                context.FillRectangle(screenBg, screenRect);
            }

            // 背景图片（按显示模式绘制，透明度作用于整图；随屏幕尺寸自适应）
            DrawScreenBackgroundImage(context, screen, screenRect);

            context.DrawRectangle(
                RenderResourceCache.Pen(DesignPalette.ScreenBorder, 1),
                screenRect);

            // 🩸 阶段 221（#98 的"接线"）：屏幕安全区**参考线**（虚线，只影响显示，不动几何）
            DrawSafeAreaGuides(context);

            // 底图：Host 模式直接画宿主渲染出的真机位图，控件不再自绘（两层同时画会重影）。
            // 位图是整屏最终像素，所以它天然带着运行时的 padding/圆角/字体排版 ——
            // 这正是"设计画布与实机一致"的唯一来源。
            if (UseHostFrame && HostFrame is { } hostFrame)
            {
                context.DrawImage(hostFrame,
                    new Rect(0, 0, screen.Width * EffectiveZoom, screen.Height * EffectiveZoom));
            }
            // 绘制所有控件（R2-T1: 跳过 IsVisible=false 控件）
            // 容器子控件不进平铺列表，按树递归画出（子坐标叠加父原点）
            else if (adaptive.HasValue)
            {
                // 预览变换: 先缩放(设计→目标比例), 再平移(letterbox 居中)
                using (context.PushTransform(
                    Matrix.CreateScale(adaptive.Value.ScaleX, adaptive.Value.ScaleY) *
                    Matrix.CreateTranslation(
                        adaptive.Value.OffsetX * EffectiveZoom,
                        adaptive.Value.OffsetY * EffectiveZoom)))
                {
                    foreach (var control in RootControls(screen))
                    {
                        if (!EffectiveVisible(control)) continue;
                        RenderTree(context, control, 0, 0);
                    }
                }
            }
            else
            {
                foreach (var control in RootControls(screen))
                {
                    if (!EffectiveVisible(control)) continue;
                    RenderTree(context, control, 0, 0);
                }
            }
        }

        // 框选矩形（视口坐标，在 PushTransform 之外绘制）
        if (_rubberBandRect.HasValue)
        {
            var rect = _rubberBandRect.Value;
            var fillBrush = RenderResourceCache.Brush(DesignPalette.SkyAccent, 0.15);
            var borderPen = new Pen(RenderResourceCache.Brush(DesignPalette.SkyAccent), 1)
            {
                DashStyle = DashStyle.Dash
            };
            context.FillRectangle(fillBrush, rect);
            context.DrawRectangle(null, borderPen, rect);
        }

        // 工具箱拖放幽灵预览（视口坐标，不进入模型）。
        if (_dragPreview.IsVisible)
        {
            var preview = _dragPreview.ViewRect!.Value;
            var color = _dragPreview.IsValid ? DesignPalette.SkyAccent : DesignPalette.Error;
            var brush = RenderResourceCache.Brush(color, 0.12);
            var pen = new Pen(RenderResourceCache.Brush(color, 0.9), 1.5)
            {
                DashStyle = DashStyle.Dash
            };
            context.FillRectangle(brush, preview);
            context.DrawRectangle(null, pen, preview);
        }

        // I10: 点击式绘制预览（Line / Polyline）—— 已点顶点连线 + 到鼠标的橡皮筋
        if (DrawTool.HasValue
            && DrawTool.Value is HmiControlType.Line or HmiControlType.Polyline
            && _clickPoints.Count > 0)
        {
            var ghostPen = RenderResourceCache.Pen(DesignPalette.SkyAccent, 1.5, PenLineCap.Round);
            var vertexBrush = RenderResourceCache.Brush(DesignPalette.SkyAccent);
            for (var i = 0; i < _clickPoints.Count; i++)
            {
                var p = _clickPoints[i];
                // 已确认顶点画小方块标记
                context.FillRectangle(vertexBrush, new Rect(p.X - 2, p.Y - 2, 4, 4));
                if (i > 0)
                    context.DrawLine(ghostPen, _clickPoints[i - 1], p);
            }
            // 橡皮筋：最后一个顶点 → 当前鼠标位置
            if (_clickPoints.Count > 0)
            {
                context.DrawLine(ghostPen, _clickPoints[^1], _clickHoverPoint);
            }
        }

        // I10: 绘图预览 —— 拖拽绘制时按当前工具实时显示图形轮廓（视口坐标）
        if (_isDrawing && DrawTool.HasValue)
        {
            var x = Math.Min(_drawStartPoint.X, _drawCurrentPoint.X);
            var y = Math.Min(_drawStartPoint.Y, _drawCurrentPoint.Y);
            var w = Math.Abs(_drawCurrentPoint.X - _drawStartPoint.X);
            var h = Math.Abs(_drawCurrentPoint.Y - _drawStartPoint.Y);
            if (w >= 2 && h >= 2)
            {
                var previewRect = new Rect(x, y, w, h);
                var ghostBrush = RenderResourceCache.Brush(DesignPalette.SkyAccent, 0.15);
                var ghostPen = RenderResourceCache.Pen(DesignPalette.SkyAccent, 1.5, PenLineCap.Round);
                switch (DrawTool.Value)
                {
                    case HmiControlType.Rectangle:
                        context.FillRectangle(ghostBrush, previewRect);
                        context.DrawRectangle(null, ghostPen, previewRect);
                        break;
                    case HmiControlType.Ellipse:
                        context.DrawEllipse(ghostBrush, ghostPen, previewRect);
                        break;
                    case HmiControlType.Polyline:
                    case HmiControlType.Line:
                        context.DrawLine(ghostPen, new Point(x, y), new Point(x + w, y + h));
                        break;
                    case HmiControlType.Arc:
                        var arcPen = RenderResourceCache.Pen(DesignPalette.SkyAccent, 6, PenLineCap.Round);
                        // 预览默认 0..270° 弧（LVGL 角度约定，提交后按 MinValue/MaxValue/Value 呈现）
                        DrawArcPolyline(context, arcPen, previewRect, 0, 270);
                        break;
                    default:
                        context.FillRectangle(ghostBrush, previewRect);
                        context.DrawRectangle(null, ghostPen, previewRect);
                        break;
                }
            }
        }

        // R2-T2: 预览模式横幅（视口坐标，画布顶部居中）
        if (IsPreviewMode)
            DrawPreviewBanner(context);
    }

    /// <summary>
    /// 预览模式横幅。与控件文本走**同一条容错路径** —— 平台侧文本对象失效
    /// （渲染后端重建 / 字体服务重载 / headless 会话结束）时，一次绘制失败不能让整个
    /// 画布渲染把异常抛回 UI 线程。这条约定本来就写在 <c>DrawAlignedText</c> 上，
    /// 而横幅此前是画布上**唯一**的裸 <c>DrawText</c> 调用 —— 本沙箱实测：
    /// headless 下横幅那次 <c>DrawText</c> 会抛（<c>CmapFormat4Table.TryGetGlyph</c>），
    /// 结果整块画布渲染失败，而其它文本因为被兜住所以看不出来。
    /// </summary>
    private void DrawPreviewBanner(DrawingContext context)
    {
        var bannerText = AppLocalization.Service["canvas.preview.banner"];
        var brush = RenderResourceCache.Brush(DesignPalette.Text);
        try
        {
            DrawPreviewBannerCore(context, bannerText, brush);
        }
        catch
        {
            RenderResourceCache.InvalidateTexts();
            try
            {
                DrawPreviewBannerCore(context, bannerText, brush);
            }
            catch
            {
                // 二次失败说明不是缓存陈旧所致；绘制路径不能把异常抛回 UI 线程。
            }
        }
    }

    private void DrawPreviewBannerCore(DrawingContext context, string bannerText, IBrush brush)
    {
        var ft = RenderResourceCache.Text(bannerText, 13, brush);
        var bannerW = ft.Width + 24;
        const int bannerH = 26;
        var bannerX = (Bounds.Width - bannerW) / 2;
        var bannerRect = new Rect(bannerX, 8, bannerW, bannerH);
        context.FillRectangle(
            RenderResourceCache.Brush(DesignPalette.Success, 0.9), bannerRect, 4);
        context.DrawText(ft, new Point(bannerX + 12, 8 + (bannerH - ft.Height) / 2));
    }

    /// <summary>
    /// **本轮渲染真正画出的安全区参考线数量**（判据用：证明渲染真的消费了 `SafePadding`，
    /// 而不是只有"算得出来"）。每次渲染都会重算，所以 0 表示这次没画。
    /// </summary>
    internal int SafeGuidesDrawnCount { get; private set; }

    /// <summary>
    /// **安全区参考线**（**画布坐标**＝模型坐标 × `EffectiveZoom`，不含平移 —— 平移由渲染的
    /// `PushTransform` 统一施加，与其它绘制一致）。四条边围绕
    /// `(Left, Top)`–`(W-Right, H-Bottom)` 这个内缩矩形；全零内边距 ⇒ 空集合。
    ///
    /// <para>抽成公开方法是为了让判据能直接断言它（渲染与判据共用同一处计算）。</para>
    /// </summary>
    public IReadOnlyList<(Point From, Point To)> SafeAreaGuides()
    {
        var screen = Screen;
        if (screen == null) return Array.Empty<(Point From, Point To)>();

        var pad = SafePadding;
        if (pad.Left <= 0 && pad.Top <= 0 && pad.Right <= 0 && pad.Bottom <= 0)
            return Array.Empty<(Point From, Point To)>();

        var z = EffectiveZoom;
        var l = pad.Left * z;
        var tp = pad.Top * z;
        var r = (screen.Width - pad.Right) * z;
        var b = (screen.Height - pad.Bottom) * z;
        if (r < l) (l, r) = (r, l);
        if (b < tp) (tp, b) = (b, tp);

        return new (Point, Point)[]
        {
            (new Point(l, tp), new Point(r, tp)),
            (new Point(r, tp), new Point(r, b)),
            (new Point(r, b), new Point(l, b)),
            (new Point(l, b), new Point(l, tp)),
        };
    }

    /// <summary>画安全区参考线（虚线；只影响显示）。</summary>
    private void DrawSafeAreaGuides(DrawingContext context)
    {
        var guides = SafeAreaGuides();
        SafeGuidesDrawnCount = guides.Count;
        if (guides.Count == 0) return;

        var pen = new Pen(RenderResourceCache.Brush(DesignPalette.Success, 0.6), 1,
            new DashStyle(new double[] { 6, 4 }, 0));
        foreach (var (from, to) in guides)
            context.DrawLine(pen, from, to);
    }

    /// <summary>按显示模式绘制屏幕背景图片（平铺/拉伸/居中），透明度作用于整图。</summary>
    private void DrawScreenBackgroundImage(DrawingContext context, HmiScreen screen, Rect screenRect)
    {
        if (string.IsNullOrEmpty(screen.BgImageAssetId)) return;

        var bitmap = ResolveBitmap(screen.BgImageAssetId);
        if (bitmap == null) return;

        var z = EffectiveZoom;
        var opacity = Math.Clamp(screen.BgImageOpacity, 0, 100) / 100.0;
        using (context.PushOpacity(opacity))
        using (context.PushClip(screenRect))
        {
            switch (screen.BgImageMode)
            {
                case ScreenBgImageMode.Tile:
                    var tileW = Math.Max(1, bitmap.PixelSize.Width * z);
                    var tileH = Math.Max(1, bitmap.PixelSize.Height * z);
                    for (double y = 0; y < screenRect.Height; y += tileH)
                    {
                        for (double x = 0; x < screenRect.Width; x += tileW)
                        {
                            context.DrawImage(bitmap, new Rect(screenRect.X + x, screenRect.Y + y, tileW, tileH));
                        }
                    }
                    break;
                case ScreenBgImageMode.Center:
                    var iw = bitmap.PixelSize.Width * z;
                    var ih = bitmap.PixelSize.Height * z;
                    context.DrawImage(bitmap, new Rect(
                        screenRect.X + (screenRect.Width - iw) / 2,
                        screenRect.Y + (screenRect.Height - ih) / 2,
                        iw, ih));
                    break;
                default: // Stretch：缩放填满整个屏幕
                    context.DrawImage(bitmap, screenRect);
                    break;
            }
        }
    }

    /// <summary>按控件不透明度渲染（Opacity<100 时压入透明度层）。</summary>
    private void RenderControlWithOpacity(DrawingContext context, HmiControl control,
        double originX, double originY)
    {
        if (control.Opacity >= 99.5)
        {
            RenderControl(context, control, originX, originY);
            return;
        }
        using (context.PushOpacity(Math.Clamp(control.Opacity, 0, 100) / 100.0))
            RenderControl(context, control, originX, originY);
    }

    /// <summary>
    /// 递归画一棵控件树：先画容器自身，再画子控件（叠加父原点，天然叠在父之上）。
    /// 隐藏/锁定的子树整棵跳过 —— 容器藏了，里面的子控件没有"单独可见"的意义。
    /// </summary>
    private void RenderTree(DrawingContext context, HmiControl control, double originX, double originY)
    {
        RenderControlWithOpacity(context, control, originX, originY);
        var childOriginX = originX + control.X;
        var childOriginY = originY + control.Y;
        foreach (var child in control.Children)
        {
            if (!EffectiveVisible(child)) continue;
            RenderTree(context, child, childOriginX, childOriginY);
        }
    }

    // ── 外部渲染器注册表（插件扩展点）──

    private static readonly Dictionary<HmiControlType, IControlRenderer> _extraRenderers = new();

    /// <summary>
    /// 注册自定义控件渲染器（覆盖内置渲染或为新增控件类型提供渲染）。
    /// 注册后立即对全部 DesignCanvas 实例生效；重复注册同类型以后者为准。
    /// </summary>
    public static void RegisterRenderer(HmiControlType type, IControlRenderer renderer)
    {
        if (renderer == null) throw new ArgumentNullException(nameof(renderer));
        _extraRenderers[type] = renderer;
    }

    private void RenderControl(DrawingContext context, HmiControl control, double originX, double originY)
    {
        var z = EffectiveZoom;
        var rect = new Rect((control.X + originX) * z, (control.Y + originY) * z,
            control.Width * z, control.Height * z);

        // 旋转/镜像：围绕控件中心应用变换（先镜像后旋转），内容随变换绘制、画布尺寸不变。
        // 无变换时零开销直接绘制（渲染热路径）。
        if (control.HasTransform)
        {
            var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            using (context.PushTransform(BuildControlTransform(
                center, control.EffectiveRotation(), control.MirrorX, control.MirrorY)))
            {
                RenderControlCore(context, control, rect, z);
            }
            return;
        }
        RenderControlCore(context, control, rect, z);
    }

    /// <summary>
    /// 构建控件旋转/镜像变换矩阵（围绕给定中心，先镜像后旋转）。
    /// 纯函数（不依赖实例状态），供渲染与命中测试共用、可单测。
    /// Avalonia 坐标系 y 向下：CreateRotation 正角度 = 顺时针；EffectiveRotation 正 = 顺时针，语义一致。
    /// </summary>
    internal static Matrix BuildControlTransform(Point center, double rotationDeg, bool mirrorX, bool mirrorY)
    {
        var m = Matrix.Identity;
        m *= Matrix.CreateTranslation(-center.X, -center.Y);
        if (mirrorX) m *= Matrix.CreateScale(-1, 1);   // 水平镜像（左右翻转）
        if (mirrorY) m *= Matrix.CreateScale(1, -1);   // 垂直镜像（上下翻转）
        if (rotationDeg != 0)
            m *= Matrix.CreateRotation(rotationDeg * Math.PI / 180);
        m *= Matrix.CreateTranslation(center.X, center.Y);
        return m;
    }

    /// <summary>
    /// 控件外形的唯一来源是宿主渲染出的位图（<see cref="UseHostFrame"/> + <see cref="HostFrame"/>）。
    ///
    /// <para>这里只画一个可选中、可拖动的裸框。C# 侧不再手写任何控件的近似外观：手写版要追
    /// LVGL 主题（渐变/阴影/padding/行高/刻度算法），追不上就是所见非所得，而"差一点"比
    /// "明显是占位"更难发现。也不画文字 —— 每帧一次文本布局就顶破渲染分配预算。</para>
    /// </summary>
    private void RenderControlCore(DrawingContext context, HmiControl control, Rect rect, double z)
    {
        // 外部注册的渲染器优先（插件扩展点，可覆盖内置实现或为新控件类型提供渲染）
        if (_extraRenderers.TryGetValue(control.Type, out var ext) && ext.CanRender(control.Type))
        {
            ext.Render(context, control, rect, z);
            return;
        }

        context.FillRectangle(RenderResourceCache.Brush(DesignPalette.Slate800), rect, (float)(2 * z));
        context.DrawRectangle(RenderResourceCache.Pen(DesignPalette.Border, Math.Max(1, 1 * z)),
            rect, (float)(2 * z));
    }

    /// <summary>把圆弧画成折线（拖放幽灵与安全区用；真实渲染走宿主位图）。</summary>
    private static void DrawArcPolyline(DrawingContext context, IPen? pen, Rect rect,
        double startAngle, double sweepAngle)
    {
        context.DrawGeometry(null, pen, RenderResourceCache.Arc(rect, startAngle, sweepAngle, asPolyline: true));
    }

    /// <summary>按 AssetItem.Id 解析位图（带缓存）。加载失败返回 null。</summary>
    private Bitmap? ResolveBitmap(string assetId)
    {
        if (_imageCache.TryGetValue(assetId, out var cached)) return cached;

        Bitmap? result = null;
        try
        {
            var library = DockLayoutState.AssetLibrary;
            var item = library?.Assets.FirstOrDefault(a => a.Id == assetId);
            if (item != null && !string.IsNullOrEmpty(item.FilePath))
            {
                var path = item.FilePath;
                if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(DockLayoutState.ProjectDirectory))
                    path = Path.Combine(DockLayoutState.ProjectDirectory, path);
                if (File.Exists(path))
                    result = new Bitmap(path);
            }
        }
        catch
        {
            result = null;
        }

        _imageCache[assetId] = result;
        return result;
    }

    // ── 表达式驱动的视觉属性（P1-1）──
    //  预览模式下，颜色/可见性可由绑定表达式驱动（如 temp > 100 ? 16711680 : 65280）。
    //  编辑态一律用静态设计值 —— 与 DisplayText 同一隔离契约（PreviewValueResolver 不写模型）。
    //  这两个方法是**画布侧的唯一接缝**：所有直读 control.XxxColor / control.IsVisible 的渲染分支
    //  都必须经它们取值，否则会出现「有的控件预览变色、有的不变」这种半生效状态。

    /// <summary>预览模式下由绑定驱动的颜色（<c>#RRGGBB</c>）；未绑定/求值失败时回退静态设计值。</summary>
    private string? EffectiveVisualColor(HmiControl control, string propertyName, string? staticValue)
        => IsPreviewMode
            ? PreviewValueResolver.ResolveVisualColor(control, PreviewValueProvider, propertyName) ?? staticValue
            : staticValue;

    /// <summary>预览模式下由绑定驱动的可见性；未绑定/求值失败时回退 <see cref="HmiControl.IsVisible"/>。</summary>
    private bool EffectiveVisible(HmiControl control)
        => IsPreviewMode
            ? PreviewValueResolver.ResolveVisualVisibility(control, PreviewValueProvider) ?? control.IsVisible
            : control.IsVisible;

    private static IBrush ParseColor(string? hexColor, Color fallback)
    {
        if (string.IsNullOrEmpty(hexColor)) return RenderResourceCache.Brush(fallback);
        try
        {
            if (hexColor.StartsWith('#'))
            {
                return RenderResourceCache.Brush(Color.Parse(hexColor));
            }
            return RenderResourceCache.Brush(Color.Parse("#" + hexColor.TrimStart('#')));
        }
        catch
        {
            return RenderResourceCache.Brush(fallback);
        }
    }

    private static Color ParseColorHex(string? hexColor, Color fallback)
    {
        if (string.IsNullOrEmpty(hexColor)) return fallback;
        try
        {
            return Color.Parse(hexColor.StartsWith('#') ? hexColor : "#" + hexColor);
        }
        catch { return fallback; }
    }

    /// <summary>
    /// 主题优先取色：主题对应色（非空）→ 默认兜底。
    /// 供渲染层消费已注入的 HmiTheme（P1-2：设计器渲染层接入主题，兜底色随主题变化）。
    /// </summary>
    private static Color ThemeFallback(string? themeColor, Color fallback)
        => string.IsNullOrEmpty(themeColor) ? fallback : ParseColorHex(themeColor, fallback);
}

/// <summary>
/// 控件拖放事件参数 —— 携带控件类型和模型坐标。
/// <paramref name="ParentContainer"/> 非空表示落点在容器图元里：新控件应作为该容器的
/// 子控件（坐标相对容器），而不是屏幕上的顶层控件。
/// </summary>
public sealed class ControlDroppedEventArgs : EventArgs
{
    public HmiControlType ControlType { get; }
    public double X { get; }
    public double Y { get; }
    public HmiControl? ParentContainer { get; }

    public ControlDroppedEventArgs(HmiControlType controlType, double x, double y,
        HmiControl? parentContainer = null)
    {
        ControlType = controlType;
        X = x;
        Y = y;
        ParentContainer = parentContainer;
    }
}

/// <summary>
/// 模板拖放事件参数 —— 携带模板 ID 和模型坐标。
/// </summary>
public sealed class TemplateDroppedEventArgs : EventArgs
{
    public string TemplateId { get; }
    public double X { get; }
    public double Y { get; }

    public TemplateDroppedEventArgs(string templateId, double x, double y)
    {
        TemplateId = templateId;
        X = x;
        Y = y;
    }
}

/// <summary>
/// 资源拖放事件参数 —— 携带资源 ID 和模型坐标。
/// </summary>
public sealed class ResourceDroppedEventArgs : EventArgs
{
    public string ResourceId { get; }
    public double X { get; }
    public double Y { get; }

    public ResourceDroppedEventArgs(string resourceId, double x, double y)
    {
        ResourceId = resourceId;
        X = x;
        Y = y;
    }
}

/// <summary>
/// I10: 绘图提交事件参数 —— 携带已创建的控件实例（已加入 Screen.Controls，宿主负责撤销/刷新）。
/// 控件已按网格吸附并配置默认几何；顶点为控件局部坐标（0..Width / 0..Height）。
/// </summary>
public sealed class ControlDrawnEventArgs : EventArgs
{
    public HmiControl Control { get; }
    public HmiControlType ControlType => Control.Type;
    public IReadOnlyList<PointData>? Points => Control.Points;

    public ControlDrawnEventArgs(HmiControl control)
    {
        Control = control;
    }
}
