using Avalonia;
using Avalonia.Controls;
using HmiEditor.App.Resources;
using HmiEditor.Core;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using System.Linq;

namespace HmiEditor.App.Controls;

/// <summary>
/// DesignCanvas 分部 —— 容器层级（图元 kind=container 放子控件）。
///
/// <para><b>坐标约定</b>：模型里子控件的 X/Y 相对父容器；画布的渲染、命中测试、选中框、
/// 拖拽全部先沿父链叠加原点换算成屏幕绝对坐标，再按原有逻辑算 —— 一套换算，四处共用
/// （<see cref="OriginOf"/> 是唯一入口）。拖拽写回时再做一次逆运算（绝对 → 相对）。</para>
/// </summary>
public partial class DesignCanvas : Control
{
    /// <summary>画布上的顶层控件：ParentId 为空的才是根（子控件经父的 Children 递归画/命中）。</summary>
    private static System.Collections.Generic.IEnumerable<HmiControl> RootControls(HmiScreen screen)
        => screen.Controls.Where(c => string.IsNullOrEmpty(c.ParentId));

    /// <summary>
    /// 控件的父链原点（屏幕绝对坐标）。父控件也在本次拖拽中时用它的**新**绝对位置 ——
    /// 否则父子同时移动会把偏移算两遍。
    /// </summary>
    private (double X, double Y) ParentAbsoluteOrigin(
        HmiControl control, Dictionary<HmiControl, (double X, double Y)>? moving,
        Dictionary<string, HmiControl> index)
    {
        double x = 0, y = 0;
        var current = ContainerHierarchy.ParentOf(control, index);
        while (current != null)
        {
            if (moving != null && moving.TryGetValue(current, out var m))
            {
                x += m.X;
                y += m.Y;
            }
            else
            {
                x += current.X;
                y += current.Y;
            }
            current = ContainerHierarchy.ParentOf(current, index);
        }
        return (x, y);
    }

    /// <summary>整屏控件索引（Id → 控件）。父链解析与绝对坐标换算用。</summary>
    private Dictionary<string, HmiControl> BuildIndex() => ContainerHierarchy.Index(Screen!);

    /// <summary>控件在屏幕坐标下的矩形（叠加父链原点；根控件即自身矩形）。</summary>
    private (double X, double Y, double Width, double Height) AbsoluteRectOf(
        HmiControl control, Dictionary<string, HmiControl> index)
        => ContainerHierarchy.AbsoluteRect(control, index);

    /// <summary>
    /// 落点处最上层的容器图元（模型坐标）。只认容器本身，不认盖在它上面的普通控件 ——
    /// 工具箱拖放时"放进哪个容器"要可预测：小按钮盖在大容器上时，落的还是那个容器。
    /// </summary>
    private HmiControl? TopmostContainerAt(Point modelPoint)
    {
        var screen = Screen;
        if (screen == null) return null;

        HmiControl? Walk(HmiControl control, double ox, double oy)
        {
            var childOriginX = ox + control.X;
            var childOriginY = oy + control.Y;
            for (var i = control.Children.Count - 1; i >= 0; i--)
            {
                var hit = Walk(control.Children[i], childOriginX, childOriginY);
                if (hit != null) return hit;
            }
            var rect = new Rect(control.X + ox, control.Y + oy, control.Width, control.Height);
            return ContainerHierarchy.IsContainer(control) && rect.Contains(modelPoint) ? control : null;
        }

        var roots = RootControls(screen)
            .OrderByDescending(c => c.LayerIndex)
            .ThenByDescending(c => screen.Controls.IndexOf(c));
        foreach (var root in roots)
        {
            var hit = Walk(root, 0, 0);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// 把新控件放进容器（坐标相对容器）。工具箱拖入容器时走这条：
    /// 子控件<b>不进</b> <see cref="HmiScreen.Controls"/> 平铺列表（层级单一事实来源在 Children）。
    /// </summary>
    public HmiControl AddControlTo(HmiControlType type, double centerX, double centerY, HmiControl container,
        string? shapeKind = null)
    {
        var screen = Screen;
        if (screen == null)
            throw new InvalidOperationException("Screen must be set before adding controls.");
        if (!ContainerHierarchy.IsContainer(container))
            throw new ArgumentException("目标不是容器图元（shape.kind=container）", nameof(container));

        var (defaultW, defaultH) = DefaultSize(type);
        var control = BuildControl(type,
            SnapToGrid(centerX - defaultW / 2), SnapToGrid(centerY - defaultH / 2), defaultW, defaultH, null,
            shapeKind);
        ContainerHierarchy.AttachAt(container, control, BuildIndex());
        SelectControl(control);
        InvalidateVisual();
        return control;
    }

    // ── 拖拽收尾：拖进 / 拖出容器 ──

    /// <summary>
    /// 拖拽释放时的层级裁定（只在移动手势结束时做一次）：
    /// 子控件整体拖出父容器 → 移出（坐标还原为绝对，进平铺列表）；
    /// 顶层控件拖到容器上 → 放入（坐标改为相对，出平铺列表）。
    /// 返回是否有结构变更（调用方据此决定是否记撤销）。
    /// </summary>
    private bool ApplyContainerMembership(
        List<HmiControl> moved, Dictionary<string, HmiControl> index)
    {
        var screen = Screen;
        if (screen == null) return false;
        var changed = false;

        foreach (var control in moved)
        {
            var parent = ContainerHierarchy.ParentOf(control, index);
            if (parent != null)
            {
                var childRect = AbsoluteRectOf(control, index);
                var parentRect = AbsoluteRectOf(parent, index);
                var outside = childRect.X + childRect.Width <= parentRect.X
                    || childRect.X >= parentRect.X + parentRect.Width
                    || childRect.Y + childRect.Height <= parentRect.Y
                    || childRect.Y >= parentRect.Y + parentRect.Height;
                if (outside)
                {
                    ContainerHierarchy.DetachTo(parent, control, index);
                    if (!screen.Controls.Contains(control)) screen.Controls.Add(control);
                    changed = true;
                }
            }
            else
            {
                var rect = AbsoluteRectOf(control, index);
                var container = TopmostContainerAt(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
                // 不能把容器拖进自己子孙里（成环）
                if (container != null && !ContainerHierarchy.ContainsControl(control, container))
                {
                    screen.Controls.Remove(control);
                    ContainerHierarchy.AttachAt(container, control, index);
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>把控件挂到指定父下（null = 变成屏幕顶层控件）。平铺列表同步增减。</summary>
    private void ReparentTo(HmiControl control, HmiControl? parent, Dictionary<string, HmiControl> index)
    {
        var screen = Screen;
        var current = ContainerHierarchy.ParentOf(control, index);
        if (ReferenceEquals(current, parent)) return;
        if (current != null) current.Children.Remove(control);
        if (parent != null)
        {
            if (!parent.Children.Contains(control)) parent.Children.Add(control);
            screen?.Controls.Remove(control);
        }
        else
        {
            if (screen != null && !screen.Controls.Contains(control)) screen.Controls.Add(control);
        }
        control.ParentId = parent?.Id;
    }

    /// <summary>撤销快照的一帧：控件 + 当时的父（null=顶层）+ 几何（父系下的相对坐标与尺寸）。</summary>
    private static (HmiControl Control, HmiControl? Parent, double X, double Y, double Width, double Height)
        SnapshotNode(HmiControl control, Dictionary<string, HmiControl> index)
        => (control, ContainerHierarchy.ParentOf(control, index),
            control.X, control.Y, control.Width, control.Height);

    /// <summary>按快照恢复层级与几何（撤销/重做共用）。</summary>
    private void RestoreNodes(
        System.Collections.Generic.IReadOnlyList<(HmiControl Control, HmiControl? Parent, double X, double Y, double Width, double Height)> nodes,
        Dictionary<string, HmiControl> index)
    {
        _historyReplaying = true;
        try
        {
            foreach (var (control, parent, x, y, width, height) in nodes)
            {
                ReparentTo(control, parent, index);
                control.X = x;
                control.Y = y;
                control.Width = width;
                control.Height = height;
            }
            UpdateMultiAdorner();
            InvalidateVisual();
        }
        finally
        {
            _historyReplaying = false;
        }
    }

    /// <summary>从模型里摘掉控件（连同子孙一起从层级里消失，但不逐个 Detach：整体删除无需坐标换算）。</summary>
    private static void RemoveNode(HmiControl control, Dictionary<string, HmiControl> index)
    {
        var parent = ContainerHierarchy.ParentOf(control, index);
        if (parent != null) parent.Children.Remove(control);
        // ParentId 是反向引用，摘除时必须一起清：否则撤销时 ReparentTo 按「父没变」提前返回，
        // 控件再也回不到容器里（删除子控件后撤销为空）。
        control.ParentId = null;
    }

    /// <summary>
    /// 删除控件（含容器子控件），带撤销。层级感知：子控件从父的 Children 移除，
    /// 顶层控件从平铺列表按原下标移除；撤销时各自归位。
    /// </summary>
    public bool DeleteControlsWithHistory(System.Collections.Generic.IReadOnlyList<HmiControl> controls)
    {
        var screen = Screen;
        if (screen == null || controls.Count == 0) return false;

        var index = BuildIndex();
        var targets = controls
            .Where(c => index.ContainsValue(c))
            .Distinct()
            .ToList();
        if (targets.Count == 0) return false;

        var snapshots = targets.Select(c => SnapshotNode(c, index)).ToList();
        var flatIndexes = targets
            .Select(c => (Control: c, Index: screen.Controls.IndexOf(c)))
            .ToList();

        void Undo()
        {
            foreach (var (control, parent, x, y, width, height) in snapshots)
            {
                ReparentTo(control, parent, index);
                control.X = x;
                control.Y = y;
                control.Width = width;
                control.Height = height;
                if (parent == null)
                {
                    var at = flatIndexes.First(f => ReferenceEquals(f.Control, control)).Index;
                    if (!screen.Controls.Contains(control))
                        screen.Controls.Insert(Math.Min(at, screen.Controls.Count), control);
                }
            }
            _selectedControls.Clear();
            _selectedControls.AddRange(targets);
            FireSelectionChanged();
            UpdateMultiAdorner();
            InvalidateVisual();
        }

        void Redo()
        {
            foreach (var (control, index0) in flatIndexes.Where(f => f.Index >= 0))
                screen.Controls.Remove(control);
            foreach (var control in targets)
                RemoveNode(control, index);
            _selectedControls.Clear();
            FireSelectionChanged();
            ClearSelectionVisuals();
            InvalidateVisual();
        }

        var description = targets.Count == 1
            ? AppLocalization.Service.Format(Lang.UndoDeleteControlNamed,
                Services.ControlNameDefaults.Display(targets[0]))
            : AppLocalization.Service.Format(Lang.UndoDeleteControls, targets.Count);
        // 位图重建由 UndoRedoManager.StateChanged 统一触发（本命令入栈即通知），
        // 这里不再单独发事件 —— 三条删除入口都经过它，不会漏。
        UndoRedoManager.Execute(new SnapshotCommand(description, Undo, Redo));
        return true;
    }
}
