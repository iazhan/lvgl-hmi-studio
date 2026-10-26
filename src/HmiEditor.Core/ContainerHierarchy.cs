using HmiEditor.Core.Models;

namespace HmiEditor.Core;

/// <summary>
/// 容器图元（<c>Shape</c> 且 <c>ShapeKind == "container"</c>）与子控件的层级模型。
///
/// <para><b>单一事实来源是树</b>：子控件只存在于父的 <see cref="HmiControl.Children"/> 里，
/// <b>不重复进</b> <see cref="HmiScreen.Controls"/> 平铺列表 —— 否则工程文件里同一控件会存两份
/// （平铺数组 + 嵌套 children），读回来变成两个对象，删除/改名各改一份。
/// <see cref="HmiControl.ParentId"/> 是**反向引用**（子 → 父），供平铺遍历方快速判断"这是不是根"。</para>
///
/// <para><b>坐标语义</b>：子控件的 X/Y 始终<b>相对父容器</b>（与 37 契约
/// <c>figures.container.children</c>「坐标相对容器」一致，运行时直接把它当 LVGL 子对象坐标）。
/// 渲染与命中测试由画布沿父链叠加原点还原成屏幕坐标；进出容器的坐标换算在
/// <see cref="AttachAt"/> / <see cref="DetachTo"/> 里做（要沿父链求容器自己的绝对原点，
/// 所以只有拿着整屏索引的那两个入口能算）。<see cref="Attach"/> / <see cref="Detach"/>
/// 是纯结构操作：调用方必须**自己先把坐标换到目标坐标系**。</para>
/// </summary>
public static class ContainerHierarchy
{
    /// <summary>是不是容器图元（契约 figures.container：v1 只有 kind=container 一种能放子控件）。</summary>
    public static bool IsContainer(HmiControl control)
        => control.Type == HmiControlType.Shape
           && string.Equals(control.ShapeKind, "container", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 结构挂载：把控件挂进容器。<b>不动坐标</b> —— child.X/Y 必须是**相对 container** 的值。
    /// 幂等：已挂着就直接返回（撤销命令重放不会重复动结构）。
    /// </summary>
    public static void Attach(HmiControl container, HmiControl child)
    {
        if (!IsContainer(container) || ReferenceEquals(container, child)) return;
        if (container.Children.Contains(child)) return;   // 已挂着：幂等
        if (ContainsControl(child, container)) return;    // 容器挂进自己子孙里会成环
        child.ParentId = container.Id;
        container.Children.Add(child);
    }

    /// <summary>
    /// 结构摘除：把子控件从容器里摘出来。<b>不动坐标</b> —— 调用方负责把 X/Y
    /// 换成目标坐标系的值（顶层 = 屏幕绝对坐标）。
    /// </summary>
    public static void Detach(HmiControl container, HmiControl child)
    {
        child.ParentId = null;
        container.Children.Remove(child);
    }

    /// <summary>
    /// 把控件放入容器，坐标按**屏幕绝对坐标**给（child.X/Y 当前值），
    /// 减去容器自身的绝对原点（容器位置 + 它自己的父链）换算成相对值，再结构挂载。
    /// 幂等：已在容器里就直接返回，不重复换算坐标。
    /// </summary>
    public static void AttachAt(HmiControl container, HmiControl child, Dictionary<string, HmiControl> index)
    {
        if (!IsContainer(container) || ReferenceEquals(container, child)) return;
        if (container.Children.Contains(child)) return;   // 已挂着：幂等，不重复换算
        if (ContainsControl(child, container)) return;    // 成环
        var abs = AbsoluteRect(container, index);
        child.X -= abs.X;
        child.Y -= abs.Y;
        child.ParentId = container.Id;
        container.Children.Add(child);
    }

    /// <summary>
    /// 把子控件移出容器：相对坐标加上容器自身的绝对原点，还原成**屏幕绝对坐标**，再结构摘除。
    /// </summary>
    public static void DetachTo(HmiControl container, HmiControl child, Dictionary<string, HmiControl> index)
    {
        var abs = AbsoluteRect(container, index);
        child.X += abs.X;
        child.Y += abs.Y;
        child.ParentId = null;
        container.Children.Remove(child);
    }

    /// <summary>整屏控件索引（Id → 控件，含子控件）。父链解析用，避免每次遍历整棵树。</summary>
    public static Dictionary<string, HmiControl> Index(HmiScreen screen)
    {
        var map = new Dictionary<string, HmiControl>(StringComparer.Ordinal);
        foreach (var root in screen.Controls) IndexInto(map, root);
        return map;
    }

    private static void IndexInto(Dictionary<string, HmiControl> map, HmiControl control)
    {
        map[control.Id] = control;
        foreach (var child in control.Children) IndexInto(map, child);
    }

    /// <summary>按 Id 找父控件；不是子控件返回 null。</summary>
    public static HmiControl? ParentOf(HmiControl child, Dictionary<string, HmiControl> index)
        => !string.IsNullOrEmpty(child.ParentId) ? index.GetValueOrDefault(child.ParentId) : null;

    /// <summary>父链叠加出的原点（不含控件自身）。根控件为 (0,0)。</summary>
    public static (double X, double Y) OriginOf(HmiControl control, Dictionary<string, HmiControl> index)
    {
        double x = 0, y = 0;
        var current = ParentOf(control, index);
        while (current != null)
        {
            x += current.X;
            y += current.Y;
            current = ParentOf(current, index);
        }
        return (x, y);
    }

    /// <summary>屏幕坐标下的控件矩形（含父链原点叠加）。</summary>
    public static (double X, double Y, double Width, double Height) AbsoluteRect(
        HmiControl control, Dictionary<string, HmiControl> index)
    {
        var (ox, oy) = OriginOf(control, index);
        return (control.X + ox, control.Y + oy, control.Width, control.Height);
    }

    /// <summary>整屏全部控件（深度优先，父先于子）。</summary>
    public static IEnumerable<HmiControl> Flatten(HmiScreen screen)
    {
        foreach (var root in screen.Controls)
        {
            yield return root;
            foreach (var descendant in Descendants(root)) yield return descendant;
        }
    }

    /// <summary>控件自身及其全部子孙（深度优先，父先于子）。</summary>
    public static IEnumerable<HmiControl> Descendants(HmiControl control)
    {
        foreach (var child in control.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>control 是否在 ancestor 的子树里（含自身）。用于防止容器挂进自己子孙（成环）。</summary>
    public static bool ContainsControl(HmiControl ancestor, HmiControl control)
    {
        if (ReferenceEquals(ancestor, control)) return true;
        foreach (var descendant in Descendants(ancestor))
            if (ReferenceEquals(descendant, control)) return true;
        return false;
    }

    /// <summary>
    /// 克隆控件后重指子孙的 ParentId：克隆只换顶层 Id，子孙的 ParentId 仍指着旧 Id，
    /// 不重指就会"父不是父"（层级查询按 Id 解析，直接断链）。
    /// </summary>
    public static void RepointDescendants(HmiControl root)
    {
        foreach (var child in root.Children)
        {
            child.ParentId = root.Id;
            RepointDescendants(child);
        }
    }
}
