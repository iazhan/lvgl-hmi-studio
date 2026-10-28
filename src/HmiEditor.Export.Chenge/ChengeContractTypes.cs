using HmiEditor.Core.Models;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 编辑器控件类型 → 契约类型名（<c>spec/widget-schema.json</c> 的 <c>widgets</c> / <c>figures</c> 键），
/// 没有对应时 <c>null</c>。
///
/// <para><b>为什么单独一个类</b>：导出器（写 <c>type</c>）与属性面板（按 <c>bind[]</c> 过滤
/// 「目标属性」下拉）都要这张表。两处各写一张必然漂移，而漂移的表现是
/// "面板允许选、导出时一条 <c>bind.not_bindable</c> 警告后静默丢掉" —— 画布上什么都不动，
/// 却没有任何一处报错。</para>
/// </summary>
public static class ChengeContractTypes
{
    /// <summary>
    /// 几类编辑器控件共用一个契约类型：矩形/椭圆/直线/折线/弧/储罐/阀门都是 <c>shape</c>
    /// （按 <c>props.kind</c> 区分），所以它们的可绑定属性也共用 <c>shape.bind</c>。
    /// </summary>
    public static string? Of(HmiControlType type) => type switch
    {
        HmiControlType.Label => "label",
        HmiControlType.Value => "value",
        HmiControlType.Button => "button",
        HmiControlType.Switch => "switch",
        HmiControlType.CheckBox => "checkbox",
        HmiControlType.Led => "lamp",
        HmiControlType.Bar => "bar",
        HmiControlType.Slider => "slider",
        HmiControlType.Gauge => "gauge",
        HmiControlType.Knob => "knob",
        HmiControlType.Dropdown => "combobox",
        HmiControlType.TextArea => "input",
        HmiControlType.Image => "image",
        HmiControlType.Table => "table",
        HmiControlType.TabView => "tabview",
        HmiControlType.Spinner => "spinner",
        HmiControlType.Keyboard => "keyboard",
        HmiControlType.Rectangle or HmiControlType.Ellipse or HmiControlType.Line
            or HmiControlType.Polyline or HmiControlType.Arc or HmiControlType.Tank
            or HmiControlType.Valve or HmiControlType.Shape => "shape",
        // 管道在契约里是**控件**（widgets.pipe，带 direction/wall/color/channel_color/flow_ms），
        // 不是图元：图元版只读 wall，也不支持流动动画。
        HmiControlType.Pipe => "pipe",
        HmiControlType.AlarmList => "alarm_list",
        HmiControlType.HistoryCurve => "history_curve",
        HmiControlType.RealtimeCurve => "realtime_curve",
        HmiControlType.AlarmBar => "alarm_bar",
        HmiControlType.QrCode => "qrcode",
        HmiControlType.Message => "message",
        HmiControlType.EnumText => "enum_text",
        HmiControlType.Roller => "roller",
        HmiControlType.StateImage => "state_image",
        HmiControlType.FrameAnim => "frame_anim",
        HmiControlType.Chart => "chart",
        HmiControlType.XyCurve => "xy_curve",
        HmiControlType.HistoryTable => "history_table",
        HmiControlType.RecipeTable => "recipe_table",
        HmiControlType.AsciiDisplay => "ascii_display",
        HmiControlType.Svg or HmiControlType.Screen => null,
        _ => null,
    };
}
