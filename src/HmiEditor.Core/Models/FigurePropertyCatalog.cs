using System;
using System.Collections.Generic;
using System.Linq;

namespace HmiEditor.Core.Models;

/// <summary>图元专属属性的值类型（决定面板用哪种编辑器、导出写 JSON 的哪种字面量）。</summary>
public enum FigurePropType
{
    Float,
    Int,
    Bool,
    Enum,
}

/// <summary>
/// 一个图元 kind 的专属属性：契约键、类型、缺省值/范围/枚举、以及到 <see cref="HmiControl"/> 的读写。
///
/// <para>模型字段可空：null = 用户没改过，导出不写键、由运行时用自己的缺省；面板显示
/// <see cref="Default"/>。每个 kind 的契约缺省不同（star5 的 inner_ratio 0.4、star 0.5），
/// 用可空字段 + 这张表才不会把一个 kind 的缺省安到另一个 kind 上。</para>
/// </summary>
public sealed class FigureProperty
{
    public FigureProperty(
        string key,
        FigurePropType type,
        object @default,
        double min,
        double max,
        Func<HmiControl, object?> get,
        Action<HmiControl, object?> set,
        params string[] options)
    {
        Key = key;
        Type = type;
        Default = @default;
        Min = min;
        Max = max;
        Get = get;
        Set = set;
        Options = options;
    }

    public string Key { get; }
    public FigurePropType Type { get; }
    public object Default { get; }
    public double Min { get; }
    public double Max { get; }
    public IReadOnlyList<string> Options { get; }
    public Func<HmiControl, object?> Get { get; }
    public Action<HmiControl, object?> Set { get; }

    /// <summary>当前控件的值；null = 未设置 ⇒ 用 <see cref="Default"/>。</summary>
    public object Effective(HmiControl control) => Get(control) ?? Default;

    public bool HasUserValue(HmiControl control) => Get(control) != null;
}

/// <summary>
/// 40 个图元 kind 的专属属性表。
///
/// <para><b>这是唯一一处登记</b>：面板动态生成行、导出器写 props、覆盖矩阵测试都读这张表，
/// 三处不再各写一份 kind → 属性清单。键名/缺省/范围逐条对齐 <c>spec/widget-schema.json</c>
/// 的 <c>figures</c>，运行时的读取位置见 <c>docs/chenge-rt/52</c> 与 <c>src/ui/ui_figure.c</c>。</para>
/// </summary>
public static class FigurePropertyCatalog
{
    private static readonly string[] Corner4 = { "tl", "tr", "bl", "br" };
    private static readonly string[] Light6 = { "tl", "tc", "tr", "bl", "bc", "br" };

    private static readonly Dictionary<string, FigureProperty[]> ByKind = Build();

    /// <summary>契约里 40 个 kind（顺序与 spec 一致，便于差集报告）。</summary>
    public static IReadOnlyList<string> Kinds { get; } = ByKind.Keys.ToArray();

    public static IReadOnlyList<FigureProperty> For(string? kind) =>
        kind != null && ByKind.TryGetValue(kind, out var props) ? props : Array.Empty<FigureProperty>();

    /// <summary>所有 kind 的属性键去重（覆盖矩阵按"键"统计时用）。</summary>
    public static IReadOnlyCollection<string> AllKeys { get; } =
        ByKind.Values.SelectMany(v => v).Select(p => p.Key).Distinct().OrderBy(k => k).ToArray();

    private static FigureProperty[] Add(FigureProperty[] list, FigureProperty prop) =>
        list.Append(prop).ToArray();

    private static Dictionary<string, FigureProperty[]> Build()
    {
        var map = new Dictionary<string, FigureProperty[]>(StringComparer.Ordinal);

        void Kind(string kind, params FigureProperty[] props) => map[kind] = props;

        // 只有公共属性的 12 个 kind：显式登记成空数组（差集报告据此区分"没有专属项"与"漏登记"）。
        foreach (var kind in new[]
                 {
                     "rect", "round_rect", "circle", "ellipse", "pie", "polygon",
                     "polyline", "line", "diamond", "octagon", "hexagon", "triangle",
                     "container",
                 })
            Kind(kind);

        Kind("arc",
            new FigureProperty("min", FigurePropType.Int, 0, 0, 1e9,
                c => c.MinValue, (c, v) => c.MinValue = ToDouble(v)),
            new FigureProperty("max", FigurePropType.Int, 100, 1, 1e9,
                c => c.MaxValue, (c, v) => c.MaxValue = ToDouble(v)));

        Kind("parallelogram",
            new FigureProperty("skew", FigurePropType.Float, 0.25, 0, 1,
                c => c.FigureSkew, (c, v) => c.FigureSkew = ToDouble(v)));

        Kind("trapezoid",
            new FigureProperty("top_w", FigurePropType.Float, 0.5, 0.05, 1,
                c => c.FigureTopWidth, (c, v) => c.FigureTopWidth = ToDouble(v)));

        Kind("right_triangle",
            new FigureProperty("flip", FigurePropType.Enum, "bl", 0, 0,
                c => c.FigureFlip, (c, v) => c.FigureFlip = v as string, Corner4));

        Kind("star5",
            new FigureProperty("inner_ratio", FigurePropType.Float, 0.4, 0.1, 0.9,
                c => c.FigureInnerRatio, (c, v) => c.FigureInnerRatio = ToDouble(v)));

        Kind("star",
            new FigureProperty("spikes", FigurePropType.Int, 12, 5, 32,
                c => c.FigureSpikes, (c, v) => c.FigureSpikes = ToInt(v)),
            new FigureProperty("inner_ratio", FigurePropType.Float, 0.5, 0.1, 0.9,
                c => c.FigureInnerRatio, (c, v) => c.FigureInnerRatio = ToDouble(v)));

        Kind("cross",
            new FigureProperty("thickness", FigurePropType.Float, 0.3, 0.05, 0.9,
                c => c.FigureThickness, (c, v) => c.FigureThickness = ToDouble(v)));

        Kind("callout",
            new FigureProperty("tail", FigurePropType.Enum, "bl", 0, 0,
                c => c.FigureTail, (c, v) => c.FigureTail = v as string, "tl", "tr", "bl", "br", "none"),
            new FigureProperty("tail_size", FigurePropType.Int, 12, 0, 80,
                c => c.FigureTailSize, (c, v) => c.FigureTailSize = ToInt(v)));

        Kind("cube",
            new FigureProperty("depth", FigurePropType.Float, 0.3, 0.05, 0.6,
                c => c.FigureDepth, (c, v) => c.FigureDepth = ToDouble(v)),
            new FigureProperty("light", FigurePropType.Enum, "tl", 0, 0,
                c => c.FigureLight, (c, v) => c.FigureLight = v as string, Corner4));

        Kind("wedge",
            new FigureProperty("tip", FigurePropType.Float, 0.5, 0.05, 0.95,
                c => c.FigureTip, (c, v) => c.FigureTip = ToDouble(v)));

        Kind("tank",
            new FigureProperty("wall", FigurePropType.Float, 0.08, 0, 0.3,
                c => c.FigureWall, (c, v) => c.FigureWall = ToDouble(v)),
            new FigureProperty("flare", FigurePropType.Float, 0.15, 0.05, 0.4,
                c => c.FigureFlare, (c, v) => c.FigureFlare = ToDouble(v)),
            new FigureProperty("top", FigurePropType.Bool, true, 0, 0,
                c => c.FigureTop, (c, v) => c.FigureTop = ToBool(v)));

        Kind("pipe_h",
            new FigureProperty("wall", FigurePropType.Float, 0.15, 0.02, 0.45,
                c => c.FigureWall, (c, v) => c.FigureWall = ToDouble(v)));

        Kind("pipe_v",
            new FigureProperty("wall", FigurePropType.Float, 0.15, 0.02, 0.45,
                c => c.FigureWall, (c, v) => c.FigureWall = ToDouble(v)));

        Kind("pipe_elbow",
            new FigureProperty("bend", FigurePropType.Float, 0.5, 0.1, 1,
                c => c.FigureBend, (c, v) => c.FigureBend = ToDouble(v)),
            new FigureProperty("wall", FigurePropType.Float, 0.15, 0.02, 0.45,
                c => c.FigureWall, (c, v) => c.FigureWall = ToDouble(v)));

        Kind("pipe_joint",
            new FigureProperty("flaps", FigurePropType.Int, 4, 2, 8,
                c => c.FigureFlaps, (c, v) => c.FigureFlaps = ToInt(v)));

        foreach (var kind in new[] { "valve_ball", "valve_butterfly", "valve_gate" })
            Kind(kind,
                new FigureProperty("open", FigurePropType.Float, 0.5, 0, 1,
                    c => c.FigureOpen, (c, v) => c.FigureOpen = ToDouble(v)));

        Kind("plane_concave",
            new FigureProperty("light", FigurePropType.Enum, "tc", 0, 0,
                c => c.FigureLight, (c, v) => c.FigureLight = v as string, Light6));

        Kind("plane_convex",
            new FigureProperty("light", FigurePropType.Enum, "tc", 0, 0,
                c => c.FigureLight, (c, v) => c.FigureLight = v as string, Light6));

        Kind("plane_groove",
            new FigureProperty("groove_w", FigurePropType.Float, 0.3, 0.05, 0.9,
                c => c.FigureGrooveWidth, (c, v) => c.FigureGrooveWidth = ToDouble(v)));

        string[] arrowDirs = { "l", "r", "u", "d" };
        Kind("arrow",
            new FigureProperty("head", FigurePropType.Float, 0.3, 0.05, 0.8,
                c => c.FigureHead, (c, v) => c.FigureHead = ToDouble(v)),
            new FigureProperty("direction", FigurePropType.Enum, "r", 0, 0,
                c => c.FigureDirection, (c, v) => c.FigureDirection = v as string, arrowDirs));
        Kind("arrow_thick",
            new FigureProperty("head", FigurePropType.Float, 0.35, 0.05, 0.8,
                c => c.FigureHead, (c, v) => c.FigureHead = ToDouble(v)),
            new FigureProperty("direction", FigurePropType.Enum, "r", 0, 0,
                c => c.FigureDirection, (c, v) => c.FigureDirection = v as string, arrowDirs));
        Kind("arrow_thin",
            new FigureProperty("head", FigurePropType.Float, 0.25, 0.05, 0.8,
                c => c.FigureHead, (c, v) => c.FigureHead = ToDouble(v)),
            new FigureProperty("direction", FigurePropType.Enum, "r", 0, 0,
                c => c.FigureDirection, (c, v) => c.FigureDirection = v as string, arrowDirs));

        Kind("ball3d",
            new FigureProperty("light", FigurePropType.Enum, "tl", 0, 0,
                c => c.FigureLight, (c, v) => c.FigureLight = v as string, Corner4));

        Kind("torus3d",
            new FigureProperty("thickness", FigurePropType.Float, 0.25, 0.05, 0.45,
                c => c.FigureThickness, (c, v) => c.FigureThickness = ToDouble(v)),
            new FigureProperty("light", FigurePropType.Enum, "tl", 0, 0,
                c => c.FigureLight, (c, v) => c.FigureLight = v as string, Corner4));

        Kind("cone3d",
            new FigureProperty("light", FigurePropType.Enum, "tl", 0, 0,
                c => c.FigureLight, (c, v) => c.FigureLight = v as string, Corner4));

        return map;
    }

    public static double ToDouble(object? v) => v switch
    {
        null => 0,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    public static int ToInt(object? v) => (int)Math.Round(ToDouble(v));

    public static bool ToBool(object? v) => v switch
    {
        bool b => b,
        string s => s == "true" || s == "1",
        _ => false,
    };
}
