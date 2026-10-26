using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using HmiEditor.App.Resources;
using HmiEditor.App.Controls;
using HmiEditor.Core.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace HmiEditor.App.Views;

/// <summary>
/// 工具箱视图 —— 仅承载控件拖拽列表（模板库已迁移至独立的 Faceplate 面板）。
///
/// [dock-blank-after-drag] 最终方案：
/// 每次 Build 创建新实例（工厂模式），OnAttachedToVisualTree 中绑定事件到 DockLayoutState，
/// 数据从 DockLayoutState 读取。事件通过 DockLayoutState 事件聚合器转发到 MainWindow。
/// </summary>
public partial class ToolboxView : UserControl
{
    /// <summary>
    /// 选中并要添加一个控件。载荷是整条 <see cref="ToolboxItem"/> 而不是只带类型 ——
    /// 图元要靠 <see cref="ToolboxItem.ShapeKind"/> 区分"圆"和"矩形"，只给类型会退化成默认形状。
    /// </summary>
    public event EventHandler<ToolboxItem>? ToolItemSelected;

    private readonly ObservableCollection<ToolboxItem> _allItems = new();

    private ToolboxItem? _dragItem;
    private Point _dragStartPoint;
    private bool _dragStarted;
    private PointerPressedEventArgs? _dragTriggerEvent;
    private const double DragThreshold = 4.0;

    public Dock.Model.Mvvm.Controls.Tool? HostTool { get; set; }

    // 持有转发委托以便在 Detached 时对称解绑（Dock 面板会被反复重建，避免事件累积）。
    private EventHandler<ToolboxItem>? _forwardToolSelected;

    /// <summary>将焦点移入工具箱搜索框并选中现有筛选条件。</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public ToolboxView()
    {
        InitializeComponent();
        PopulateItems();

        ToolboxList.ItemsSource = _allItems;
        // 第一项是组头（不可添加），选中落到第一个真正的控件上
        ToolboxList.SelectedIndex = Math.Max(0, _allItems.ToList().FindIndex(i => !i.IsHeader));
        ResultCountText.Text = AppLocalization.Service.Format("tb.search.count", _allItems.Count);

        AttachDragHandlers(ToolboxList);
    }

    /// <summary>
    /// 附加到视觉树时：绑定事件到 DockLayoutState（事件聚合），从 DockLayoutState 读取数据。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 将事件转发到 DockLayoutState 事件聚合器
        _forwardToolSelected ??= (s, item) => DockLayoutState.RaiseToolItemSelected(this, item);
        ToolItemSelected += _forwardToolSelected;
    }

    // 与 Attached 对称：Dock 面板重建/分离时解除事件转发，避免重复实例累积订阅。
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_forwardToolSelected != null)
            ToolItemSelected -= _forwardToolSelected;
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachDragHandlers(ListBox listBox)
    {
        listBox.AddHandler(InputElement.PointerPressedEvent, OnToolboxListPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        listBox.AddHandler(InputElement.PointerMovedEvent, OnToolboxListPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        listBox.AddHandler(InputElement.PointerReleasedEvent, OnToolboxListPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // 控件类型 → 矢量图标（Path 数据，viewBox 0 0 24 24，跨字体可靠渲染）
    //
    // ⚠ 与 Assets/Icons.axaml 的关系（阶段 72 起有判据）：26 个类型里 19 个在那里有**镜像键**
    //   （IconQuick<T> 14 个 / IconDraw<T> 5 个），几何**必须逐字一致** —— 同一个控件在
    //   「主窗口绘图工具栏」与「工具箱」里长得不一样，用户是能同时看到的。
    //   曾经有 4 条分叉过：Rectangle / Ellipse / Polyline / Arc 在这里是「收缩一格的版本」
    //   （5..19 / 半径 7），而字典里是 4..20 / 半径 8。
    //   守它的判据：ToolboxIconDictionaryTests。**改这里任何一条几何前先看那里。**
    //
    // ⚠ internal 只为判据可调用（HmiEditor.App.csproj 的 InternalsVisibleTo App.UiTests），
    //   不是给产品代码用的构造 API。
    internal static string ControlIcon(HmiControlType type) => type switch
    {
        HmiControlType.Screen     => "M3,4 L21,4 L21,18 L3,18 Z",                            // 容器：矩形框
        HmiControlType.Button     => "M4,7 L20,7 L20,17 L4,17 Z",                            // 按钮：圆角矩形
        HmiControlType.Slider     => "M4,12 L20,12 M14,12 m-3,0 a3,3 0 1,0 6,0 a3,3 0 1,0 -6,0", // 滑条：横线+圆点
        HmiControlType.Switch     => "M6,5 L18,5 A7,7 0 0 1 18,19 L6,19 A7,7 0 0 1 6,5 Z",   // 开关：胶囊
        HmiControlType.CheckBox   => "M4,4 L20,4 L20,20 L4,20 Z M7,12 L11,16 L17,8",         // 复选：方框+勾
        HmiControlType.Label      => "M4,8 L20,8 M4,12 L20,12 M4,16 L14,16",                 // 标签：文本行
        HmiControlType.Image      => "M4,5 L20,5 L20,19 L4,19 Z M7,16 L11,11 L14,14 L17,9",   // 图片：框+山
        HmiControlType.Bar        => "M5,19 L5,9 L8,9 L8,19 Z M10,19 L10,5 L13,5 L13,19 Z M15,19 L15,12 L18,12 L18,19 Z", // 柱状
        HmiControlType.Dropdown   => "M4,6 L20,6 L20,16 L4,16 Z M6,19 L12,14 L18,19 Z",       // 下拉：框+箭头
        HmiControlType.TextArea   => "M6,3 L14,3 L19,8 L19,21 L6,21 Z M14,3 L14,8 L19,8",     // 文本域：文档
        HmiControlType.Gauge      => "M4,15 A8,8 0 0 1 20,15 M12,15 L12,11 L14.5,13",         // 仪表：弧+指针
        HmiControlType.Knob       => "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 Z M12,12 L16,9", // 旋钮：圆+指针
        HmiControlType.Keyboard   => "M4,7 L20,7 L20,17 L4,17 Z M6,9.5 L6,11 M9.5,9.5 L9.5,11 M13,9.5 L13,11 M16.5,9.5 L16.5,11 M7,14 L17,14", // 键盘
        HmiControlType.Led        => "M12,5 A7,7 0 1 0 12,19 A7,7 0 1 0 12,5 Z",             // LED：圆
        HmiControlType.Spinner    => "M12,4 A8,8 0 0 1 20,12 M12,4 L12,9",                    // 加载：弧+针
        HmiControlType.TabView    => "M4,6 L20,6 L20,18 L4,18 Z M4,6 L20,6 M4,6 L4,10",       // 页签：容器+标签
        HmiControlType.Line       => "M4,18 L20,6",                                           // 直线
        HmiControlType.Table      => "M4,4 L20,4 L20,20 L4,20 Z M4,10 L20,10 M4,16 L20,16 M10,4 L10,20", // 表格
        HmiControlType.Svg        => "M12,4 L20,12 L12,20 L4,12 Z",                           // SVG：菱形
        HmiControlType.Tank       => "M6,5 L18,5 L18,15 A6,6 0 0 1 6,15 Z",                   // 罐体：圆柱
        HmiControlType.Pipe       => "M5,8 L19,8 M5,16 L19,16",                               // 管道：双线
        HmiControlType.Valve      => "M12,4 L20,12 L12,20 L4,12 Z M12,4 L12,20",              // 阀门：菱形+线
        // I10: 基础绘图图形
        HmiControlType.Rectangle  => "M4,4 L20,4 L20,20 L4,20 Z",                             // 矩形：方框
        HmiControlType.Ellipse    => "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 Z",              // 椭圆：圆
        HmiControlType.Polyline   => "M4,17 L9,9 L13,13 L20,5",                              // 折线：多段线
        HmiControlType.Arc        => "M5,13 A7,7 0 1 0 12,6 M12,6 L17,6",                     // 圆弧：弧+端点
        // chenge-runtime 目标新增
        HmiControlType.Value      => "M4,7 L16,7 M4,12 L20,12 M4,17 L13,17",                  // 数值：三行数字
        HmiControlType.AlarmList  => "M3,5 L21,5 M3,10 L21,10 M3,15 L21,15 M3,20 L21,20",     // 报警列表：多行
        HmiControlType.HistoryCurve => "M4,19 L4,5 M4,19 L21,19 M5,16 L9,10 L13,14 L19,7",    // 曲线：坐标轴+折线
        HmiControlType.RealtimeCurve => "M4,19 L4,5 M4,19 L21,19 M5,15 L9,9 L13,13 L19,6 M20,4 L20,8 M18,6 L22,6",  // 实时曲线：折线+时钟
        HmiControlType.AlarmBar => "M3,10 L21,10 M3,14 L17,14 M3,18 L20,18 M3,6 L8,6",   // 报警条：单行滚动
        HmiControlType.QrCode => "M4,4 L9,4 L9,9 L4,9 Z M15,4 L20,4 L20,9 L15,9 Z M4,15 L9,15 L9,20 L4,20 Z M13,13 L20,13 L20,20 L13,20",  // 二维码
        HmiControlType.Message => "M3,8 L21,8 L21,18 L14,18 L10,21 L10,18 L3,18 Z",   // 消息条
        HmiControlType.EnumText => "M4,7 L20,7 M4,13 L16,13 M4,19 L12,19",   // 枚举文本：几行字
        HmiControlType.Roller => "M5,5 L19,5 M5,12 L19,12 M5,19 L19,19 M8,8 L8,10 M8,15 L8,17",   // 滚轮：多条+选中线
        HmiControlType.StateImage => "M3,4 L21,4 L21,20 L3,20 Z M3,16 L9,10 L13,14 L17,8 L21,12",   // 值换图：图片框+山
        HmiControlType.FrameAnim => "M4,6 L8,6 L8,18 L4,18 Z M10,6 L14,6 L14,18 L10,18 Z M16,6 L20,6 L20,18 L16,18 Z",   // 帧动画：三格
        HmiControlType.Shape => "M12,3 L21,20 L3,20 Z",   // 通用图元：三角形
        HmiControlType.Chart => "M4,20 L4,4 M4,20 L21,20 M7,20 L7,12 M11,20 L11,8 M15,20 L15,14 M19,20 L19,6",   // 图表：柱
        HmiControlType.XyCurve => "M4,20 L4,4 M4,20 L21,20 M7,17 L11,9 L15,13 L20,5",   // XY：散点折线
        HmiControlType.HistoryTable => "M3,5 L21,5 M3,10 L21,10 M3,15 L21,15 M3,20 L21,20 M9,5 L9,20",   // 历史表：表格
        HmiControlType.RecipeTable => "M3,5 L21,5 M3,10 L21,10 M3,15 L21,15 M3,20 L21,20 M15,5 L15,20",   // 配方表
        HmiControlType.AsciiDisplay => "M4,7 L8,16 L12,7 M6,13 L10,13 M14,7 L14,16 L18,16 M14,12 L17,12",   // ASCII：字母 A
        _ => "M4,4 L20,4 L20,20 L4,20 Z",
    };

    /// <summary>图元的工具箱图标（viewBox 0 0 24 24，描边风格）。按 kind 区分形状。</summary>
    private static string FigureIcon(string kind) => kind switch
    {
        "container"      => "M3,5 L21,5 L21,19 L3,19 Z M6,8 L18,8 L18,16 L6,16 Z",
        "rect"           => "M4,5 L20,5 L20,19 L4,19 Z",
        "round_rect"     => "M8,5 L16,5 A4,4 0 0 1 16,19 L8,19 A4,4 0 0 1 8,5 Z",
        "circle"         => "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 Z",
        "ellipse"        => "M2,12 A10,6 0 1 0 22,12 A10,6 0 1 0 2,12 Z",
        "line"           => "M4,18 L20,6",
        "polyline"       => "M4,17 L9,9 L13,13 L20,5",
        "polygon"        => "M12,3 L20,8 L17,19 L7,19 L4,8 Z",
        "arc"            => "M4,17 A8,8 0 0 1 20,17",
        "pie"            => "M12,12 L12,4 A8,8 0 0 1 19.5,16 Z",
        "triangle"       => "M12,4 L21,20 L3,20 Z",
        "right_triangle" => "M4,4 L4,20 L20,20 Z",
        "diamond"        => "M12,3 L21,12 L12,21 L3,12 Z",
        "parallelogram"  => "M7,5 L21,5 L17,19 L3,19 Z",
        "trapezoid"      => "M6,5 L18,5 L21,19 L3,19 Z",
        "hexagon"        => "M12,3 L19,7.5 L19,16.5 L12,21 L5,16.5 L5,7.5 Z",
        "octagon"        => "M8,3 L16,3 L21,8 L21,16 L16,21 L8,21 L3,16 L3,8 Z",
        "star5"          => "M12,3 L14.6,9.6 L21.5,10 L16.2,14.4 L17.9,21 L12,17.4 L6.1,21 L7.8,14.4 L2.5,10 L9.4,9.6 Z",
        "star"           => "M12,2 L14.5,8 L21,8 L16,12.5 L18,19 L12,15.5 L6,19 L8,12.5 L3,8 L9.5,8 Z",
        "cross"          => "M9,3 L15,3 L15,9 L21,9 L21,15 L15,15 L15,21 L9,21 L9,15 L3,15 L3,9 L9,9 Z",
        _                => ControlIcon(HmiControlType.Shape),
    };

    private void PopulateItems()
    {
        // 组头只为把列表切开：之前 46 项平铺，"分类"只是每项下面那行小字，看着像没有分类。
        // 组头不可拖、不可选、不进搜索匹配。
        void Group(string title) => _allItems.Add(
            new ToolboxItem(new StreamGeometry(), title, title, (HmiControlType)(-1), IsHeader: true));

        void Add(HmiControlType type, string category) => _allItems.Add(
            new ToolboxItem(StreamGeometry.Parse(ControlIcon(type)),
                Services.ControlTypeTextKeys.Describe(type), category, type));

        // 图元按形状逐项列出：拖出来就是这个形状，不必再去属性面板改「种类」。
        // 图标按 kind 逐个画 —— 都用「图元」的三角形会让一列条目看起来一模一样。
        void Figure(string kind, string displayKey, string category) => _allItems.Add(
            new ToolboxItem(StreamGeometry.Parse(FigureIcon(kind)),
                AppLocalization.Service[displayKey], category, HmiControlType.Shape, kind));

        var input = AppLocalization.Service[Lang.TbCatInput];
        var display = AppLocalization.Service[Lang.TbCatDisplay];
        var chart = AppLocalization.Service[Lang.TbCatChart];
        var special = AppLocalization.Service[Lang.TbCatSpecial];
        var process = AppLocalization.Service[Lang.TbCatProcess];
        var container = AppLocalization.Service[Lang.TbCatContainer];
        var draw = AppLocalization.Service[Lang.TbCatDraw];
        var select = AppLocalization.Service[Lang.TbCatSelect];

        Group(input);
        Add(HmiControlType.Button, input);
        Add(HmiControlType.Slider, input);
        Add(HmiControlType.Switch, input);
        Add(HmiControlType.CheckBox, input);
        Add(HmiControlType.TextArea, input);
        Add(HmiControlType.Knob, input);
        // 键盘不进工具箱：运行时没有独立键盘控件（只有 input 点击时弹出的模态键盘），
        // 导出器对它直接 return null —— 拖出来只会在位图里消失，纯误导。
        Add(HmiControlType.Spinner, special);

        Group(select);
        Add(HmiControlType.Dropdown, select);
        Add(HmiControlType.Roller, select);

        Group(display);
        Add(HmiControlType.Label, display);
        Add(HmiControlType.Image, display);
        Add(HmiControlType.Led, display);
        Add(HmiControlType.Value, display);
        Add(HmiControlType.QrCode, display);
        Add(HmiControlType.Message, display);
        Add(HmiControlType.EnumText, display);
        Add(HmiControlType.StateImage, display);
        Add(HmiControlType.FrameAnim, display);
        Add(HmiControlType.AsciiDisplay, display);
        Add(HmiControlType.Svg, display);

        Group(chart);
        Add(HmiControlType.Bar, chart);
        Add(HmiControlType.Gauge, chart);
        Add(HmiControlType.RealtimeCurve, chart);
        Add(HmiControlType.HistoryCurve, chart);
        Add(HmiControlType.Chart, chart);
        Add(HmiControlType.XyCurve, chart);
        Add(HmiControlType.HistoryTable, chart);

        Group(special);
        Add(HmiControlType.AlarmList, special);
        Add(HmiControlType.AlarmBar, special);
        Add(HmiControlType.RecipeTable, special);

        Group(process);
        Add(HmiControlType.Tank, process);
        Add(HmiControlType.Pipe, process);
        Add(HmiControlType.Valve, process);

        Group(container);
        Add(HmiControlType.TabView, container);
        Add(HmiControlType.Table, container);
        // 容器图元：它才是「放子控件的容器」（Screen 是页面级的东西，不该进工具箱）
        Figure("container", Lang.TbFigureContainer, container);

        Group(draw);
        Figure("rect", Lang.TbFigureRect, draw);
        Figure("round_rect", Lang.TbFigureRoundRect, draw);
        Figure("circle", Lang.TbFigureCircle, draw);
        Figure("ellipse", Lang.TbFigureEllipse, draw);
        Figure("line", Lang.TbFigureLine, draw);
        Figure("polyline", Lang.TbFigurePolyline, draw);
        Figure("polygon", Lang.TbFigurePolygon, draw);
        Figure("arc", Lang.TbFigureArc, draw);
        Figure("pie", Lang.TbFigurePie, draw);
        Figure("triangle", Lang.TbFigureTriangle, draw);
        Figure("right_triangle", Lang.TbFigureRightTriangle, draw);
        Figure("diamond", Lang.TbFigureDiamond, draw);
        Figure("parallelogram", Lang.TbFigureParallelogram, draw);
        Figure("trapezoid", Lang.TbFigureTrapezoid, draw);
        Figure("hexagon", Lang.TbFigureHexagon, draw);
        Figure("octagon", Lang.TbFigureOctagon, draw);
        Figure("star5", Lang.TbFigureStar5, draw);
        Figure("star", Lang.TbFigureStar, draw);
        Figure("cross", Lang.TbFigureCross, draw);
    }

    // ── 拖拽 ──

    private void OnToolboxListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var item = FindToolboxItem(e.Source);
        if (item == null || item.IsHeader) return;   // 组头不可拖、不可添加
        _dragItem = item;
        _dragStartPoint = e.GetPosition(this);
        _dragStarted = false;
        _dragTriggerEvent = e;
        e.Handled = true;
    }

    private async void OnToolboxListPointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (_dragItem == null || _dragStarted) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStartPoint.X) <= DragThreshold && Math.Abs(pos.Y - _dragStartPoint.Y) <= DragThreshold)
                return;
            _dragStarted = true;
            // 载荷带种类：`Type|kind`（图元区分矩形/圆/容器…），接收侧按 '|' 切分
            var payload = _dragItem.ShapeKind is { Length: > 0 } kind
                ? $"{_dragItem.ControlType}|{kind}"
                : _dragItem.ControlType.ToString();
            var item = DataTransferItem.Create(DesignCanvas.DragDataFormat, payload);
            var data = new DataTransfer();
            data.Add(item);
            await DragDrop.DoDragDropAsync(_dragTriggerEvent!, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Toolbox] 拖拽异常: {ex}");
        }
    }

    private void OnToolboxListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragItem != null && !_dragStarted)
            ToolItemSelected?.Invoke(this, _dragItem);
        _dragItem = null;
        _dragStarted = false;
        _dragTriggerEvent = null;
    }

    private static ToolboxItem? FindToolboxItem(object? source)
    {
        var visual = source as Visual;
        while (visual != null)
        {
            if (visual is Control c && c.DataContext is ToolboxItem ti) return ti;
            visual = visual.GetVisualParent();
        }
        return null;
    }

    // ── 事件 ──

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (Parent == null) return;
        var filter = SearchBox.Text?.Trim() ?? "";
        var previousType = (ToolboxList.SelectedItem as ToolboxItem)?.ControlType;
        ClearSearchButton.IsVisible = filter.Length > 0;
        // 搜索时不出组头：结果只放匹配项，混进组头会把一屏切得零碎
        var filtered = string.IsNullOrEmpty(filter)
            ? _allItems.ToList()
            : _allItems.Where(i => !i.IsHeader && (
                    i.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    i.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    i.ControlType.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        ToolboxList.ItemsSource = filtered;
        EmptyStateBorder.IsVisible = filtered.Count == 0;
        ToolboxList.IsVisible = filtered.Count > 0;
        ResultCountText.Text = filtered.Count == 0
            ? ""
            : AppLocalization.Service.Format("tb.search.count", filtered.Count);
        if (filtered.Count > 0)
        {
            var preservedIndex = previousType.HasValue
                ? filtered.FindIndex(i => i.ControlType == previousType.Value)
                : -1;
            ToolboxList.SelectedIndex = preservedIndex >= 0
                ? preservedIndex
                : ToolboxList.SelectedIndex >= 0
                    ? Math.Clamp(ToolboxList.SelectedIndex, 0, filtered.Count - 1)
                    : 0;
            if (ToolboxList.SelectedIndex >= 0)
                ToolboxList.ScrollIntoView(ToolboxList.SelectedIndex);
        }
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && HasOnlyModifier(e, KeyModifiers.Control))
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.A && HasOnlyModifier(e, KeyModifiers.Control))
        {
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if ((e.Key == Key.Down || e.Key == Key.Up) && ToolboxList.IsVisible && ToolboxList.ItemCount > 0)
        {
            ToolboxList.Focus();
            ToolboxList.SelectedIndex = e.Key == Key.Up
                ? ToolboxList.ItemCount - 1
                : Math.Max(0, ToolboxList.SelectedIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ToolboxList.ItemCount > 0)
        {
            if (ToolboxList.SelectedIndex < 0)
                ToolboxList.SelectedIndex = 0;
            if (ToolboxList.SelectedItem is ToolboxItem { IsHeader: false } item)
                ToolItemSelected?.Invoke(this, item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
                SearchBox.Clear();
            else if (ToolboxList.IsVisible && ToolboxList.ItemCount > 0)
                ToolboxList.Focus();
            e.Handled = true;
        }
    }

    private void OnToolboxListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ToolboxList.SelectedItem is ToolboxItem { IsHeader: false } item)
        {
            ToolItemSelected?.Invoke(this, item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        // 输入可打印字符时：焦点转回搜索框并开始输入，实现即输即筛（type-ahead 过滤）。
        else if (IsPrintableKey(e.Key) &&
                 (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
        {
            SearchBox.Focus();
            // 将字符插入当前光标位置，而不是覆盖已有筛选条件。
            if (e.KeySymbol is { Length: 1 } symbol)
            {
                var text = SearchBox.Text ?? string.Empty;
                var caret = Math.Clamp(SearchBox.CaretIndex, 0, text.Length);
                SearchBox.Text = text.Insert(caret, symbol);
                SearchBox.CaretIndex = caret + symbol.Length;
            }
            e.Handled = true;
        }
    }

    private static bool HasOnlyModifier(KeyEventArgs e, KeyModifiers modifier)
        => (e.KeyModifiers & modifier) != 0 &&
           (e.KeyModifiers & ~modifier) == 0;

    // 判断是否为可输入字符键（字母/数字/空格，排除控制与导航键）。
    private static bool IsPrintableKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return true;
        if (key >= Key.D0 && key <= Key.D9) return true;
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return true;
        return key == Key.Space || key == Key.OemMinus || key == Key.OemPlus ||
               key == Key.OemPeriod || key == Key.OemComma || key == Key.OemQuestion;
    }
}

public record ToolboxItem(
    Geometry Icon,      // 矢量图标（Path 数据解析后的几何，viewBox 0 0 24 24）
    string DisplayName,
    string Category,
    HmiControlType ControlType,
    /// <summary>
    /// 图元的具体种类（shape 的 kind）。非空时拖出来直接就是这个种类，
    /// 用户不必再到属性面板里改 —— 20 个 kind 逐项列出，工具箱才真正可用。
    /// </summary>
    string? ShapeKind = null,
    /// <summary>
    /// 分组标题行：只为把列表切开（40 多项平铺时分类只是每项下面那行小字），
    /// 不可拖、不可选、不参与搜索匹配。
    /// </summary>
    bool IsHeader = false
);
