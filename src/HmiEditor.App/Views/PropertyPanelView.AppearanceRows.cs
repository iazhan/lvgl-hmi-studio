namespace HmiEditor.App.Views;

using Avalonia.Controls;
using HmiEditor.Core.Models;

/// <summary>
/// 「外观样式」与「内容」两组的**按类型门控**。
///
/// <para><b>为什么要有这张表</b>：这两组行此前对所有类型都显示，但运行时真正读的键只有一部分 ——
/// 例如 <c>bg_color</c> 只有 label / button / 图元 会应用（其余 20 多类各自硬编码或干脆不设），
/// <c>opacity</c> 至今没有任何控件读，<c>font_family</c> 导出器根本不看（字号才是 <c>font_size</c>）。
/// 全显示的结果就是"面板上能改、画布和板子都不动"，用户会以为设置丢了。</para>
///
/// <para><b>切的是整行</b>：外观组每行是 <c>StackPanel</c> 里一个具名 <c>Grid</c>。只切行内的
/// 标签/输入框的话，空 <c>Grid</c> 仍在 <c>StackPanel</c> 里占一个 <c>Spacing</c>，
/// 面板顶部就会出现一片空白 —— 这是这块踩过两次的坑。</para>
///
/// <para>判据来自运行时（<c>src/ui/*.c</c> 的 hmi_json_get 与 apply_box_style 调用点）与导出器
/// 各分支实际写的键；改这里之前先确认那一侧真的读。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>
    /// 运行时 <c>apply_transform</c> 支持的档位：图片 / 图元 / 文本类。其余类型（bar、slider、
    /// gauge、knob、lamp、spinner、table、chart、曲线、报警、二维码、值换图、帧动画、
    /// tabview、keyboard）连旋转行都不显示 —— 显示出来只会让人在导出时撞 Error。
    /// </summary>
    private static bool HasTransform(HmiControlType type) => type is
        HmiControlType.Label or HmiControlType.Value or HmiControlType.EnumText
        or HmiControlType.TextArea or HmiControlType.Dropdown or HmiControlType.Button
        or HmiControlType.CheckBox or HmiControlType.AsciiDisplay or HmiControlType.Message
        or HmiControlType.AlarmBar or HmiControlType.Roller
        or HmiControlType.Image
        or HmiControlType.Rectangle or HmiControlType.Ellipse or HmiControlType.Line
        or HmiControlType.Polyline or HmiControlType.Arc or HmiControlType.Tank
        or HmiControlType.Valve or HmiControlType.Shape;

    /// <summary>按控件类型显隐「内容」与「外观样式」两组的行；整组都空时把折叠面板一起收起。</summary>
    private void ShowAppearanceRows(HmiControlType type)
    {
        var any = false;

        // 内容：只有这五类把 control.Text 导出成 props.text（Value 的文字来自绑定，
        // Message/QrCode/Roller/TabView/Table 各有自己的字段）
        any |= SetRow(RowText, type is HmiControlType.Label or HmiControlType.Button
            or HmiControlType.CheckBox or HmiControlType.Switch or HmiControlType.EnumText);

        // 背景色：导出器对每个类型都写 bg_color，但运行时只在 label / button / 图元上应用
        // （switch 把它当 color_off 的兜底，所以也算）
        any |= SetRow(RowBgColor, type is HmiControlType.Label
            or HmiControlType.Button or HmiControlType.Switch
            or HmiControlType.Rectangle or HmiControlType.Ellipse or HmiControlType.Line
            or HmiControlType.Polyline or HmiControlType.Arc or HmiControlType.Tank
            or HmiControlType.Pipe or HmiControlType.Valve or HmiControlType.Shape);

        // 圆角 / 边框：只有 label、button 与图元族真的画（table/input/combobox/roller 都硬编码）
        var hasBorder = type is HmiControlType.Label or HmiControlType.Button
            or HmiControlType.Rectangle or HmiControlType.Ellipse or HmiControlType.Polyline
            or HmiControlType.Arc or HmiControlType.Tank or HmiControlType.Valve
            or HmiControlType.Shape;
        any |= SetRow(RowRadius, hasBorder);
        any |= SetRow(RowBorderWidth, hasBorder);
        any |= SetRow(RowBorderColor, hasBorder);

        // 文字色：导出器写 color 且运行时会读的类型（combobox/input/table 导出但运行时不读，故不含）
        any |= SetRow(RowTextColor, type is HmiControlType.Label
            or HmiControlType.Value or HmiControlType.Button or HmiControlType.CheckBox
            or HmiControlType.EnumText or HmiControlType.AsciiDisplay
            or HmiControlType.AlarmBar or HmiControlType.Led);

        // 水平/垂直对齐：运行时只有 apply_text_style / apply_text_valign 这几类读
        var hasAlign = type is HmiControlType.Label or HmiControlType.Value
            or HmiControlType.EnumText or HmiControlType.AsciiDisplay or HmiControlType.Message;
        any |= SetRow(RowTextAlign, hasAlign);
        any |= SetRow(RowTextVAlign, hasAlign);

        // 字号：运行时按类型取 ui_font(font_size)；combobox/message/tabview 等硬编码 16，改不动
        any |= SetRow(RowFontSize, type is HmiControlType.Label
            or HmiControlType.Value or HmiControlType.Button or HmiControlType.CheckBox
            or HmiControlType.TextArea or HmiControlType.EnumText or HmiControlType.AsciiDisplay
            or HmiControlType.AlarmBar or HmiControlType.AlarmList or HmiControlType.Roller
            or HmiControlType.Table or HmiControlType.HistoryTable or HmiControlType.RecipeTable
            or HmiControlType.HistoryCurve or HmiControlType.RealtimeCurve or HmiControlType.XyCurve);

        // 字体族：导出器只写 font_size（SetFont），从来不读 FontFamily ⇒ 全类型隐藏
        any |= SetRow(RowFontFamily, false);

        // 不透明度：props.opacity 目前没有任何建页路径读（只有背景图与动画读同名键）⇒ 全类型隐藏
        any |= SetRow(RowOpacity, false);

        // 这两行的显隐由 BindControl 按"支持指示器色 / 支持选中态"决定（不在本表里），
        // 但只要它们可见，外观组就不能收起。
        AppearanceExpander.IsVisible = any || RowIndicatorColor.IsVisible || RowIsChecked.IsVisible;
    }

    /// <summary>整行显隐；返回该行是否可见。</summary>
    private static bool SetRow(Control row, bool visible)
    {
        row.IsVisible = visible;
        return visible;
    }
}
