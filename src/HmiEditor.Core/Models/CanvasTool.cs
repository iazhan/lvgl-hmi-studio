namespace HmiEditor.Core.Models;

/// <summary>
/// 画布绘图工具的**完整目标**：控件类型 + 图元种类 + 点约束。
///
/// <para>只带 <see cref="HmiControlType"/> 是不够的：<c>Shape</c> 一个类型覆盖契约 40 个
/// kind，<c>Pipe</c> 一个类型覆盖直管 / 弯管 / 多段三种走向。工具要是分不清它们，
/// 画出来的东西就只能靠"落一个默认框"再手工改属性 —— 这些字段就是让"选工具"把
/// 形状本身也一起定下来。</para>
///
/// <para><see cref="Mode"/> 是**创建时**的意图（点几下）；控件建好后该怎么编辑由
/// <see cref="FigureGeometry.ModeFor(HmiControl)"/> 从已有的 kind / 点数反推 ——
/// 两条路径不能互相依赖，否则手工改过点数的控件会被工具的旧意图锁住。</para>
/// </summary>
public readonly record struct CanvasTool(
    HmiControlType Type,
    string? ShapeKind = null,
    FigurePointMode Mode = FigurePointMode.Box)
{
    /// <summary>归一化后的图元种类：<c>Shape</c> 取 <see cref="ShapeKind"/>，其余类型映射到导出 kind。</summary>
    public string? Kind => Type switch
    {
        HmiControlType.Shape => string.IsNullOrWhiteSpace(ShapeKind) ? "rect" : ShapeKind,
        HmiControlType.Line => "line",
        HmiControlType.Polyline => "polyline",
        HmiControlType.Arc => "arc",
        HmiControlType.Pipe => "pipe",
        HmiControlType.Rectangle => "rect",
        HmiControlType.Ellipse => "ellipse",
        HmiControlType.Tank => "tank",
        _ => null,
    };

    /// <summary>是否是"按点画"的几何工具（形状由点击位置决定，而不是拖一个框）。</summary>
    public bool IsPointTool => Mode != FigurePointMode.Box;

    /// <summary>同一目标（类型 + kind + 点约束）—— 用于"再点一次同一工具项取消"。</summary>
    public bool SameTarget(CanvasTool other)
        => Type == other.Type
           && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
           && Mode == other.Mode;

    /// <summary>
    /// 按控件类型选工具。类型本身就带点约束的（Line / Polyline / Arc）自动取对应的取点方式 ——
    /// 否则「线条」按钮会退化成拖框，与「通用图元 → line」画出两套行为。
    /// </summary>
    public static CanvasTool Widget(HmiControlType type) => new(type, null, ModeOfType(type));

    public static CanvasTool Figure(string kind)
        => new(HmiControlType.Shape, kind, FigureGeometry.ModeForKind(kind));

    public static CanvasTool Pipe(FigurePointMode mode) => new(HmiControlType.Pipe, null, mode);

    private static FigurePointMode ModeOfType(HmiControlType type) => type switch
    {
        HmiControlType.Line => FigurePointMode.TwoPoint,
        HmiControlType.Polyline => FigurePointMode.OpenPath,
        HmiControlType.Arc => FigurePointMode.Angle,
        _ => FigurePointMode.Box,
    };
}
