namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 内容型控件（消息条 / 枚举文本 / 滚轮）的写回。行定义见
/// <c>PropertyPanelView.Parameters.Content.cs</c>；拆文件的唯一原因是行数上限。
/// 空白一律写 null —— 模型里 null 与 "" 语义不同（null = 未配置）。
/// </summary>
public partial class PropertyPanelView
{
    private void WriteContentFields()
    {
        if (MessageTextBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.MessageText), string.IsNullOrWhiteSpace(MessageTextBox.Text) ? null : MessageTextBox.Text.Trim(), v => _currentControl.MessageText = v);
        if (MessageLevelCombo.IsVisible)
            ValidateAndWrite(nameof(HmiControl.MessageLevel), GetComboTag(MessageLevelCombo), v => _currentControl.MessageLevel = v);
        if (MessageAutoHideBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.MessageAutoHideMs), (int)MessageAutoHideBox.Value, v => _currentControl.MessageAutoHideMs = v);
        if (MessageMarqueeCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.MessageMarquee), MessageMarqueeCheck.IsChecked == true, v => _currentControl.MessageMarquee = v);

        if (EnumTextValueBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.EnumTextValue), string.IsNullOrWhiteSpace(EnumTextValueBox.Text) ? null : EnumTextValueBox.Text.Trim(), v => _currentControl.EnumTextValue = v);

        if (RollerOptionsBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.RollerOptions), string.IsNullOrWhiteSpace(RollerOptionsBox.Text) ? null : RollerOptionsBox.Text.Trim(), v => _currentControl.RollerOptions = v);
        if (RollerRowsBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.RollerVisibleRows), (int)RollerRowsBox.Value, v => _currentControl.RollerVisibleRows = v);
    }
}
