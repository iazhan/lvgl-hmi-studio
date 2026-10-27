namespace HmiEditor.App.Views;

using Avalonia.Controls;
using HmiEditor.Core.Models;

/// <summary>
/// 「内容」与「外观样式」两组的**按类型门控**。
///
/// <para>判据表在 <see cref="AppearanceSupport"/>（与导出器共用同一张表）；这里只负责
/// 把结果落到行上。改动前先读那个类的注释 —— 两张表分家就会出现"面板上能改、
/// 画布和板子都不动"。</para>
///
/// <para><b>切的是整行</b>：外观组每行是 <c>StackPanel</c> 里一个具名 <c>Grid</c>。只切行内
/// 的标签/输入框的话，空 <c>Grid</c> 仍在 <c>StackPanel</c> 里占一个 <c>Spacing</c>，
/// 面板顶部就会出现一片空白 —— 这是这块踩过两次的坑。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>按控件类型显隐「内容」与「外观样式」两组的行；整组都空时把折叠面板一起收起。</summary>
    private void ShowAppearanceRows(HmiControlType type)
    {
        var any = false;

        // 内容：只有这几类把 control.Text 导出成 props.text（Value 的文字来自绑定，
        // Message/QrCode/Roller/TabView/Table 各有自己的字段）
        any |= SetRow(RowText, type is HmiControlType.Label or HmiControlType.Button
            or HmiControlType.CheckBox or HmiControlType.Switch or HmiControlType.EnumText);

        // 开关的这段文字导出成 text_on（画在控件框右边），标签换成"打开时文字"；
        // 关闭态那格是另一条（text_off），只有开关有
        LblPropText.IsVisible = type != HmiControlType.Switch;
        LblPropTextOn.IsVisible = type == HmiControlType.Switch;
        any |= SetRow(RowSwitchTextOff, type == HmiControlType.Switch);

        // 底色 / 圆角 / 描边 / 不透明度：由运行时的建页收口统一应用（见 AppearanceSupport）
        any |= SetRow(RowBgColor, AppearanceSupport.HasBgColor(type));
        any |= SetRow(RowRadius, AppearanceSupport.HasBoxBorder(type));
        any |= SetRow(RowBorderWidth, AppearanceSupport.HasBoxBorder(type));
        any |= SetRow(RowBorderColor, AppearanceSupport.HasBoxBorder(type));
        any |= SetRow(RowTextColor, AppearanceSupport.HasTextColor(type));
        any |= SetRow(RowFontSize, AppearanceSupport.HasFontSize(type));

        // 对齐：水平与垂直同一批类型；输入框只有水平（build_input 不读 valign）
        var hasAlign = AppearanceSupport.HasTextAlign(type);
        any |= SetRow(RowTextAlign, hasAlign || AppearanceSupport.HasInputAlignOnly(type));
        any |= SetRow(RowTextVAlign, hasAlign);

        // 字体族：导出器只写 font_size（SetFont），从不读 FontFamily ⇒ 全类型隐藏
        any |= SetRow(RowFontFamily, false);

        // 不透明度：props.opacity 由建页收口统一应用（0..255）。
        any |= SetRow(RowOpacity, AppearanceSupport.IsExported(type));

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
