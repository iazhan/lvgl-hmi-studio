namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 「动作与高级」区**交互行为组**的写回：二次确认 / 按下保持 / 长按触发 / 禁用表现 / 输入提交时机。
///
/// <para><b>闸门必须是这几行自己</b>：它们画在交互区，而按钮与输入框的「控件功能参数」可能
/// 一行都不显示（按钮本区没有数据参数）—— 挂在那个可见性下面就是"改了不生效"。行不可见时
/// 编辑器里留的是上一个控件的值，所以逐行判 <c>Row*</c>。</para>
///
/// <para>单独成文件的第二个原因：<c>PropertyPanelView.*.cs</c> 有 500 行上限
/// （<c>SingleFileSizeGuardTests</c>），<c>Changes.cs</c> 已接近。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>交互行为组可见时逐行写回。</summary>
    private void SaveInteractionRows()
    {
        if (!InteractionExpander.IsVisible) return;

        if (RowBtnConfirm.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ButtonConfirmText), string.IsNullOrWhiteSpace(ConfirmTextBox.Text) ? null : ConfirmTextBox.Text.Trim(), v => _currentControl.ButtonConfirmText = v);
        if (RowBtnConfirmYes.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ButtonConfirmYes), string.IsNullOrWhiteSpace(ConfirmYesBox.Text) ? null : ConfirmYesBox.Text.Trim(), v => _currentControl.ButtonConfirmYes = v);
        if (RowBtnConfirmNo.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ButtonConfirmNo), string.IsNullOrWhiteSpace(ConfirmNoBox.Text) ? null : ConfirmNoBox.Text.Trim(), v => _currentControl.ButtonConfirmNo = v);
        if (RowBtnConfirmWait.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ButtonConfirmWaitMs), (int)ConfirmWaitMsBox.Value, v => _currentControl.ButtonConfirmWaitMs = v);
        if (RowBtnHold.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ButtonHoldMs), (int)HoldMsBox.Value, v => _currentControl.ButtonHoldMs = v);
        if (RowBtnLongPress.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ButtonLongPressMs), (int)LongPressMsBox.Value, v => _currentControl.ButtonLongPressMs = v);
        if (RowBtnOffStyle.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ButtonOffStyle), GetComboTag(OffStyleCombo), v => _currentControl.ButtonOffStyle = v);
        if (RowInputCommit.IsVisible)
            ValidateAndWrite(nameof(HmiControl.InputCommit), GetComboTag(InputCommitCombo), v => _currentControl.InputCommit = v);
    }
}
