namespace HmiEditor.Core.Services;

using HmiEditor.Core.Models;

/// <summary>
/// 从工具箱新建控件时的默认外观。
///
/// <para><b>为什么单独一张表</b>：这些默认值会直接进导出产物（<c>BgColor</c> → <c>props.bg_color</c>
/// → 运行时的 <c>apply_common_style</c>），所以"拖出来长什么样"和"板子上长什么样"用的是同一份
/// 数据。集中在一处，判据（<c>ControlDefaultsTests</c>）才能逐个类型钉住，不用起 UI。</para>
/// </summary>
public static class ControlDefaults
{
    /// <summary>
    /// 新控件的默认底色。<c>null</c> = 不写 <c>bg_color</c>，控件没有底。
    ///
    /// <para><b>为什么 Spinner 没有底色</b>：加载动画的可视部分只有一圈弧，底色会被运行时画成
    /// 一块圆角方块，把弧糊在中间；弧色走 <c>IndicatorColor</c>（缺省取主题主色）。</para>
    /// </summary>
    public static string? NewControlBgColor(HmiControlType type) => type switch
    {
        HmiControlType.Button => "#2563eb",
        HmiControlType.Label => null,
        HmiControlType.Slider => "#334155",
        HmiControlType.Switch => "#2563eb",
        HmiControlType.CheckBox => null,
        HmiControlType.Bar => "#2563eb",
        HmiControlType.Gauge => "#38bdf8",
        HmiControlType.Knob => "#a78bfa",
        HmiControlType.Keyboard => "#334155",
        HmiControlType.Led => "#22c55e",
        HmiControlType.Spinner => null,
        HmiControlType.TabView => "#1e293b",
        HmiControlType.Line => "#ffffff",
        HmiControlType.Table => "#1e293b",
        // 过程图形控件
        HmiControlType.Tank => "#0ea5e9",     // 天蓝（液体）
        HmiControlType.Pipe => "#64748b",     // 管身灰
        HmiControlType.Valve => "#ef4444",    // 红色（关状态）
        // 基础绘图图形
        HmiControlType.Rectangle => "#334155", // 填充深灰蓝（边框由 BorderColor 配置）
        HmiControlType.Ellipse => "#334155",
        HmiControlType.Polyline => "#ffffff",  // 线色（与 Line 同语义）
        HmiControlType.Arc => "#334155",       // 轨道色（指示色取 IndicatorColor/TextColor）
        _ => "#334155",
    };
}
