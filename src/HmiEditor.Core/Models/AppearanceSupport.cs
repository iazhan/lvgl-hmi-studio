namespace HmiEditor.Core.Models;

/// <summary>
/// 「面板上这一行该不该出现」与「导出产物里该不该有这个键」的**唯一一张表**。
///
/// <para><b>为什么必须集中在一处</b>：同一个属性有三个消费方 —— 属性面板（显示/隐藏行）、
/// 导出器（写不写这个键）、运行时（读不读这个键）。三者各写一份的话，漏一处就是
/// "面板上能改、画布和板子都不动"（用户反馈过多次）。所以：面板与导出器共用本表，
/// 运行时的对应实现是 <c>chenge-runtime/src/ui/widgets.c</c> 的建页收口
/// <c>apply_common_style</c>（通用框样式 + opacity + visible）。</para>
///
/// <para><b>改这张表之前先跑像素判据</b>：<c>chenge-runtime/tools/appearance_probe.py
/// --matrix</c> 逐类型逐键改两个值、位图必须不同；编辑器侧对应
/// <c>ExportAppearanceRenderTests</c>。探针说"没变"就是运行时不读，别在这里放行。</para>
/// </summary>
public static class AppearanceSupport
{
    /// <summary>
    /// 能被导出的类型（<c>ChengeWidgetMapper</c> 有映射）。Screen / Svg / Keyboard 没有
    /// 运行时对应物，外观行一律不显示 —— 改了也不会进产物。
    /// </summary>
    public static bool IsExported(HmiControlType type) => type is not
        (HmiControlType.Screen or HmiControlType.Svg or HmiControlType.Keyboard);

    /// <summary>
    /// 有独立「底色」层的类型。<c>Led</c> 的底色就是灯珠色（<c>props.color</c>），给它刷
    /// <c>bg_color</c> 会把灯珠颜色盖掉；<c>QrCode</c> 的底由 <c>light_color</c> 决定。
    /// <c>Switch</c> 特殊：没有独立背景层，<c>bg_color</c> 当"断开时的轨道色"兜底。
    /// </summary>
    /// <summary>
    /// 有独立"背景色"这一行的类型。
    ///
    /// <para><b>Pipe 不在其列</b>：管道是自己画几何的复合控件（管壁 + 通道 + 流动块），
    /// 对象本身必须完全透明 —— 给它背景色只会把整个控件矩形刷成一块底色、盖住管壁。
    /// 同理也不该导出 <c>bg_color</c>（运行时对 pipe 直接忽略，但键留在产物里会误导人）。</para>
    /// </summary>
    public static bool HasBgColor(HmiControlType type) => type is not
        (HmiControlType.Led or HmiControlType.QrCode or HmiControlType.Pipe) && IsExported(type);

    /// <summary>
    /// 有矩形框（圆角 / 边框宽度 / 边框颜色）的类型 —— 也是导出器**总是**写
    /// <c>radius</c> / <c>border_width</c> 的类型。
    ///
    /// <para><b>为什么总是写（含 0）</b>：面板显示 0 就必须画出 0。运行时对缺键是
    /// "吃控件主题"（按钮 8、滑块/进度条是圆头），而面板上的 0 是模型默认值 ——
    /// 只在 <c>&gt; 0</c> 时写键，就等于"面板说 0、板子给圆角"，这正是用户报的那个不一致。
    /// 代价：编辑器导出的工程里，没配圆角的控件是直角（手写 JSON 的工程不受影响，
    /// 缺键照样吃主题）。</para>
    ///
    /// <para>没有框的类型：Led（正圆）、QrCode、Line/Polyline/Arc（只描边）、
    /// Pipe/Valve（复合几何）。</para>
    /// </summary>
    public static bool HasBoxBorder(HmiControlType type) => type is not
        (HmiControlType.Led or HmiControlType.QrCode
        or HmiControlType.Line or HmiControlType.Polyline or HmiControlType.Arc
        or HmiControlType.Pipe or HmiControlType.Valve
        or HmiControlType.Screen or HmiControlType.Svg or HmiControlType.Keyboard);

    /// <summary>
    /// 文字色（<c>props.color</c>）会被运行时读的类型。input / table / message 的文字色
    /// 在各自 build 里硬编码（契约里也没有这几个键），改不动。
    /// </summary>
    public static bool HasTextColor(HmiControlType type) => type is HmiControlType.Label
        or HmiControlType.Value or HmiControlType.Button or HmiControlType.CheckBox
        or HmiControlType.EnumText or HmiControlType.AsciiDisplay
        or HmiControlType.AlarmBar or HmiControlType.Led or HmiControlType.Dropdown;

    /// <summary>水平/垂直对齐：运行时只有这几类走 apply_text_style / apply_text_valign。</summary>
    public static bool HasTextAlign(HmiControlType type) => type is HmiControlType.Label
        or HmiControlType.Value or HmiControlType.EnumText
        or HmiControlType.AsciiDisplay or HmiControlType.Message;

    /// <summary>水平对齐、没有垂直对齐的输入框（build_input 只认 align）。</summary>
    public static bool HasInputAlignOnly(HmiControlType type) => type is HmiControlType.TextArea;

    /// <summary>字号（<c>font_size</c>）会被运行时按类型取 ui_font 的类型。</summary>
    public static bool HasFontSize(HmiControlType type) => type is HmiControlType.Label
        or HmiControlType.Value or HmiControlType.Button or HmiControlType.CheckBox
        or HmiControlType.TextArea or HmiControlType.EnumText
        or HmiControlType.AsciiDisplay or HmiControlType.AlarmBar
        or HmiControlType.AlarmList or HmiControlType.Roller or HmiControlType.Dropdown
        or HmiControlType.Table or HmiControlType.HistoryTable
        or HmiControlType.RecipeTable or HmiControlType.HistoryCurve
        or HmiControlType.RealtimeCurve or HmiControlType.XyCurve;

    /// <summary>
    /// 运行时 <c>apply_transform</c> 支持的档位：图片 / 图元 / 文本类。其余类型连旋转行
    /// 都不显示 —— 显示出来只会让人在导出时撞 Error（越档是硬失败，整页建不起来）。
    /// </summary>
    public static bool HasTransform(HmiControlType type) => type is
        HmiControlType.Label or HmiControlType.Value or HmiControlType.EnumText
        or HmiControlType.TextArea or HmiControlType.Dropdown or HmiControlType.Button
        or HmiControlType.CheckBox or HmiControlType.AsciiDisplay or HmiControlType.Message
        or HmiControlType.AlarmBar or HmiControlType.Roller
        or HmiControlType.Image
        or HmiControlType.Rectangle or HmiControlType.Ellipse or HmiControlType.Line
        or HmiControlType.Polyline or HmiControlType.Arc or HmiControlType.Tank
        or HmiControlType.Valve or HmiControlType.Shape;
}
