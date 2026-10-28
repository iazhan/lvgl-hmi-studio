using System.Linq;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using HmiEditor.App.Controls;
using HmiEditor.App.Services;
using HmiEditor.App.Views;
using HmiEditor.Core.Models;
using HmiEditor.Core.Models.Alarm;
using HmiEditor.Core.Models.Scripting;
using HmiEditor.Core.Models.User;
using HmiEditor.Core.Diagnostics;
using HmiEditor.Core.Serialization;
using HmiEditor.Core.Services;
using HmiEditor.App.Resources;

namespace HmiEditor.App;

/// <summary>画布/工具箱/绘图/资源库/导航/选中联动事件。</summary>
/// <remarks>
/// 自 MainWindow.WireEvents()（原 992 行单方法）按域拆出，纯代码搬移：
/// 方法体逐行未改，WireEvents() 只保留对这几个分域方法的调用。
/// </remarks>
public partial class MainWindow
{
    private void WireCanvasAndSelectionEvents()
    {
        // 启动即清上一轮被强杀留下的宿主（ppid=1）：它们攥着共享内存与 LVGL 缓冲，
        // 新会话抢不到资源时画布就是空白 —— 与"点两次真机渲染开关才有图"是同一件事。
        ReapOrphanHosts();

        // 将变量管理器传递给属性面板，用于变量绑定下拉列表
        PropertyPanel.SetVariableManager(_currentProject.VariableManager);
        PropertyPanel.SetProject(_currentProject);

        // ── 撤销/重做状态更新 ──
        _undoRedoManager.StateChanged += (_, _) => UpdateUndoRedoMenu();

        // ── 工具箱 → 画布（支持撤销） ──
        // 只订总线：ToolboxView 在 OnAttachedToVisualTree 里把 ToolItemSelected 转发到
        // DockLayoutState，并在 OnDetachedFromVisualTree 解除（ToolboxView.axaml.cs:64-79）——
        // 这份转发比直订更稳：视图重新 attach 时会自己重建，直订订的则是建窗那一刻的实例。
        DockLayoutState.ToolItemSelected += OnToolItemSelected;

        // ── I10: 绘图工具栏（画图按钮 + 控件快捷按钮）──
        WireDrawToolbar();

        // ── I10: 画布绘图提交 → 撤销命令 + 面板刷新（Dock 聚合路由）──
        DockLayoutState.ControlDrawn += (_, args) =>
        {
            var canvasView = GetActiveView<DesignCanvasView>("Canvas") ?? CanvasPanel;
            var screen = canvasView.Canvas.Screen;
            if (screen == null || args.Control == null) return;

            // 控件已由 DesignCanvas.CommitDraw 创建并加入 Screen.Controls；命令幂等包装实现撤销
            var control = args.Control;
            _undoRedoManager.Execute(new AddControlCommand(screen.Controls, control));

            ProjectExplorerPanel.Refresh(screen);
            LayerPanel.Refresh();
            SetUnsavedChanges(true);
            UpdateStatusBar();
        };

        // ── 工具箱 → 画布（拖放定位，支持撤销） ──
        // 经 DockLayoutState 聚合器订阅：工厂模式下 CanvasPanel 字段可能指向旧实例，
        // 实际显示的画布由 ViewLocator.GetActiveView("Canvas") 获取。
        // 撤销命令与项目浏览器刷新均以"实际显示画布的 Screen"为准，保证三者一致。
        DockLayoutState.ControlDropped += (_, args) =>
        {
            var canvasView = GetActiveView<DesignCanvasView>("Canvas") ?? CanvasPanel;
            var screen = canvasView.Canvas.Screen;
            if (screen == null) return;

            // 按点图元（线/折线/弧/管道）拖进来 ≠ 落一个默认框：这些形状由**点**决定，
            // 落一个框只能得到"没有走向的直管"或"一条对角线"。改成把落点当第一个点，
            // 后面的点用户接着点（工具同时选中，工具栏高亮）。
            if (args.ParentContainer == null)
            {
                var droppedTool = args.ControlType == HmiControlType.Shape
                    ? CanvasTool.Figure(args.ShapeKind ?? "rect")
                    : CanvasTool.Widget(args.ControlType);
                if (droppedTool.IsPointTool)
                {
                    // 顺序不能反：SetDrawTool 会把画布上的在途绘制**放弃**掉（切工具语义），
                    // 先播种第一个点就会被它清掉 —— 必须"先选中工具，再落下第一个点"。
                    SetDrawTool(droppedTool);
                    canvasView.Canvas.BeginPointTool(droppedTool, args.X, args.Y);
                    return;
                }
            }

            // 落在容器图元里 → 作为子控件放入（坐标相对容器，不进平铺列表）
            HmiControl control;
            IUndoableCommand command;
            if (args.ParentContainer is { } container)
            {
                control = canvasView.Canvas.AddControlTo(args.ControlType, args.X, args.Y, container,
                    args.ShapeKind);
                command = new AttachControlCommand(screen, container, control);
            }
            else
            {
                control = canvasView.Canvas.AddControl(args.ControlType, args.X, args.Y, args.ShapeKind);
                command = new AddControlCommand(screen.Controls, control);
            }
            _undoRedoManager.Execute(command);

            ProjectExplorerPanel.Refresh(screen);
            LayerPanel.Refresh();
            SetUnsavedChanges(true);
            UpdateStatusBar();
        };

        // ── 模板库 → 画布（拖放定位，支持撤销） ──
        DockLayoutState.TemplateDropped += (_, args) =>
        {
            var canvasView = GetActiveView<DesignCanvasView>("Canvas") ?? CanvasPanel;
            var screen = canvasView.Canvas.Screen;
            if (screen == null) return;

            var template = _templateManager.FindById(args.TemplateId);
            if (template == null) return;

            // 参数化模板用 Faceplate 实例，无参数模板直接展开
            if (template.Parameters.Count > 0)
            {
                var instance = _templateManager.CreateFaceplateInstance(template, args.X, args.Y);
                var command = new AddControlCommand(screen.Controls, instance);
                _undoRedoManager.Execute(command);
            }
            else
            {
                var controls = _templateManager.ApplyTemplate(template, args.X, args.Y);
                foreach (var c in controls)
                {
                    _undoRedoManager.Execute(new AddControlCommand(screen.Controls, c));
                }
            }

            ProjectExplorerPanel.Refresh(screen);
            LayerPanel.Refresh();
            SetUnsavedChanges(true);
            UpdateStatusBar();
        };

        // ── HMI 图库 → 画布（拖放定位，支持撤销） ──
        DockLayoutState.ResourceDropped += (_, args) =>
        {
            var canvasView = GetActiveView<DesignCanvasView>("Canvas") ?? CanvasPanel;
            var screen = canvasView.Canvas.Screen;
            if (screen == null) return;

            var resource = _resourceLibrary.FindById(args.ResourceId);
            if (resource == null) return;

            PlaceResourceOnCanvas(screen, resource, args.X, args.Y);
        };

        // ── 图库面板双击 → 画布中心 ──
        // 只订总线：ResourceLibraryView 构造函数转发 ResourcePlaced（ResourceLibraryView.axaml.cs:71）。
        DockLayoutState.ResourcePlaced += (_, resource) => PlaceResourceAtCanvasCenter(resource);

        // ── 画布选中变化 → 属性面板 + 控件树 + 层面板 ──
        // 只订总线。注意 `CanvasPanel.Canvas` 就是 DesignCanvasView 内的 CanvasLayer，而该视图对
        // CanvasLayer.SelectionChanged 的处理器**只 raise 总线、不 invoke 实例事件**
        //（DesignCanvasView.axaml.cs:148-151）⇒ 总线这一路覆盖全部触发点，实例直订纯属第二遍。
        // `_suppressSelectionEvent` 保留：它防的是 HandleCanvasSelectionChanged **自身引发的回环**
        //（BindControl / SelectControl 会再 raise 别的总线事件），不是"双路"本身。
        DockLayoutState.CanvasControlSelected += (_, control) =>
        {
            if (_suppressSelectionEvent) return;
            _suppressSelectionEvent = true;
            HandleCanvasSelectionChanged(control);
            _suppressSelectionEvent = false;
        };

        // 位图重建只有一个入口：撤销栈的每一次操作（拖动/属性/增删/粘贴/撤销/重做）。
        // 不再另加"结构变化"事件 —— 它被这条完全覆盖，两处都发只会让每次编辑排两次重建。
        _undoRedoManager.StateChanged += (_, _) => _designHost?.NotifyStructureChanged();

        // 只订总线（视图侧转发见 DesignCanvasView.axaml.cs:154-157）。
        // 开了真机渲染时转给预览服务：拖动中发几何补丁（跟手），停手 300ms 整页重载（精确）。
        // 总线事件不带控件（EventHandler），所以取当前选中集——拖动中的就是被拖的那些。
        DockLayoutState.CanvasControlPropertyChanged += (_, _) =>
        {
            SetUnsavedChanges(true);

            // 拖动/对齐改的是模型：面板里的坐标框必须跟着走，否则用户紧接着改别的属性时
            // SaveProperties 会把陈旧的 X/Y 写回模型，位置被拉回拖动前。
            // 但拖动中每 16ms 刷四个数字框会让 FANumberBox 反复排版（手感发滞），
            // 100ms 一次足够看清坐标，也仍赶在"接着改属性"之前纠正旧值。
            var nowMs = Environment.TickCount64;
            if (nowMs - _lastGeometryRefreshMs >= 100)
            {
                _lastGeometryRefreshMs = nowMs;
                PropertyPanel.RefreshGeometry();
            }

            if (_designHost?.IsRunning != true) return;
            var canvasView = GetActiveView<DesignCanvasView>("Canvas") ?? CanvasPanel;
            foreach (var control in canvasView.Canvas.SelectedControls)
                _designHost.OnControlChanged(control);
        };

        // ── 画布右键菜单操作路由 ──
        DockLayoutState.CanvasContextActionRequested += (_, action) => HandleCanvasContextAction(action);

        // ── 属性面板修改 → 画布重绘 ──
        // 只订总线（PropertyPanelView 构造函数转发 PropertyChanged，:55-59）。
        // `_suppressPropertyEvent` 保留：它防的是 HandlePropertyPanelChanged 写回控件后
        // 又触发 PropertyChanged 的**回环**，不是"双路"本身。
        DockLayoutState.PropertyChanged += (_, control) =>
        {
            if (_suppressPropertyEvent) return;
            _suppressPropertyEvent = true;
            HandlePropertyPanelChanged(control);
            _suppressPropertyEvent = false;
        };

        // ── 屏幕页面属性修改 → 画布/项目浏览器刷新 ──
        // 只订总线（PropertyPanelView 构造函数转发 ScreenPropertyChanged，:62-66）。
        DockLayoutState.ScreenPropertyChanged += (_, screen) =>
        {
            if (_suppressPropertyEvent) return;
            _suppressPropertyEvent = true;
            OnScreenPropertyPanelChanged(this, screen);
            _suppressPropertyEvent = false;
        };

        // ── 层面板选中 → 画布高亮 + 属性面板 ──
        // 只订总线（LayerPanelView 构造函数转发 SelectionChanged，:32-33）。
        DockLayoutState.LayerSelectionChanged += (_, control) =>
        {
            if (_suppressLayerEvent) return;
            _suppressLayerEvent = true;
            HandleLayerSelectionChanged(control);
            _suppressLayerEvent = false;
        };

        // ── 层面板属性变化（锁定/隐藏/排序）→ 画布重绘 ──
        // 只订总线（LayerPanelView 构造函数转发 LayerChanged，:34-35）。
        DockLayoutState.LayerChanged += (_, _) =>
        {
            CanvasPanel.Refresh();
            SetUnsavedChanges(true);
        };

        // ── 资源管理器（资产库）4 个事件 ──
        // 一律**不直订**：AssetManagerView 构造函数把这四个都转发到总线
        //（AssetManagerView.axaml.cs:56-59）——
        //   AssetSelected        → DockLayoutState.AssetSelected       （状态栏提示）
        //   FontSubsetRequested  → DockLayoutState.FontSubsetRequested （字体子集化对话框）
        //   AiIconRequested      → DockLayoutState.AiIconRequested     （AI 图标生成）
        //   SvgApplyRequested    → DockLayoutState.SvgApplyRequested   （SVG 应用到选中控件）
        // 消费体全部在 Wiring.DockAggregate.cs:55-91，与此前这里的四段逐字等价。
        // 后果对照：AiIcon 那一路曾被 OpenAiIconDialogAsync 的 `_suppressAiIconEvent` 压住第二次
        //（所以它一直只弹 1 个，读数 +1），**其余三路没有任何标志** ⇒ 状态栏写两遍、
        // 字体子集化对话框弹 2 个、SVG 文件被读两遍并应用两遍（登记表 #46）。

        // ── R2-T5: 导航流（双击节点切换屏幕 / 图变更标记未保存）──
        // 只订总线：NavigationFlowView 构造函数转发 NodeActivated / GraphChanged
        //（NavigationFlowView.axaml.cs:41-42）→ DockLayoutState 的 NavNodeActivated / NavGraphChanged。
        //
        // ── 控件树（选中 → 画布高亮 / 删除请求）──
        // 只订总线：ProjectExplorerView 构造函数转发 SelectionChanged / DeleteRequested
        //（ProjectExplorerView.axaml.cs:50-51）→ ExplorerSelectionChanged / ExplorerDeleteRequested。
        //
        // ⚠ 这两组正是"两份副本会走样"的实证：总线路的 ExplorerSelectionChanged 曾经漏掉
        // `SetTransformButtonsVisible(control != null)`，一直靠实例直订那一份兜着；
        // 本阶段已把漏的那行补进 Wiring.DockAggregate.cs，才具备删除实例直订的前提。
    }
}
