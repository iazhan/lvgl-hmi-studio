namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板里**内容型控件**的行：消息条（message）/ 枚举文本（enum_text）/ 滚轮（roller）——
/// 它们的显示内容由配置或变量决定，没有内容就画不出东西。
///
/// <para>这三类此前在面板上一行都没有。枚举文本只给「显示文字」一行：<c>EnumTextMap</c>
/// 运行时不读 <c>props.map</c>（映射要配在绑定的 map 里，导出器会就此告警），
/// 摆一行改不动的输入框只会误导。</para>
///
/// <para>拆成独立分部文件的唯一原因是 <c>PropertyPanelView.*.cs</c> 有 500 行上限
/// （见 <c>SingleFileSizeGuardTests</c>），<c>Parameters.cs</c> 已接近上限。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>内容型控件的全部行一起显隐（进面板先整块关掉）。</summary>
    private void ShowContentRows(bool visible)
    {
        SetParamRow(LblMessageText, MessageTextBox, visible);
        SetParamRow(LblMessageLevel, MessageLevelCombo, visible);
        SetParamRow(LblMessageAutoHide, MessageAutoHideBox, visible);
        SetParamRow(LblMessageMarquee, MessageMarqueeCheck, visible);
        SetParamRow(LblEnumTextValue, EnumTextValueBox, visible);
        SetParamRow(LblRollerOptions, RollerOptionsBox, visible);
        SetParamRow(LblRollerRows, RollerRowsBox, visible);
    }

    /// <summary>打开该类型自己的行；不是内容型则什么都不做。</summary>
    private void ShowContentRowsFor(HmiControlType type)
    {
        switch (type)
        {
            case HmiControlType.Message:
                SetParamRow(LblMessageText, MessageTextBox, true);
                SetParamRow(LblMessageLevel, MessageLevelCombo, true);
                SetParamRow(LblMessageAutoHide, MessageAutoHideBox, true);
                SetParamRow(LblMessageMarquee, MessageMarqueeCheck, true);
                break;
            case HmiControlType.EnumText:
                SetParamRow(LblEnumTextValue, EnumTextValueBox, true);
                break;
            case HmiControlType.Roller:
                SetParamRow(LblRollerOptions, RollerOptionsBox, true);
                SetParamRow(LblRollerRows, RollerRowsBox, true);
                break;
        }
    }

    /// <summary>把内容型控件的字段回填到面板。</summary>
    private void FillContentFields(HmiControl control)
    {
        MessageTextBox.Text = control.MessageText ?? "";
        SelectComboByTag(MessageLevelCombo, control.MessageLevel, 0);
        MessageAutoHideBox.Value = control.MessageAutoHideMs;
        MessageMarqueeCheck.IsChecked = control.MessageMarquee;

        EnumTextValueBox.Text = control.EnumTextValue ?? "";

        RollerOptionsBox.Text = control.RollerOptions ?? "";
        RollerRowsBox.Value = control.RollerVisibleRows;
    }
}
