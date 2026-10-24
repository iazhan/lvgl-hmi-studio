namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板里**批次 1a**的「控件细节」行（仪表 / 指示灯 / 按钮）。
/// 抽出来的原因同曲线、报警、批次 5：<c>Parameters.cs</c> 有 500 行上限。
/// </summary>
public partial class PropertyPanelView
{
    private void ShowGaugeRows(bool visible)
    {
        SetParamRow(LblGaugeArcWidth, GaugeArcWidthBox, visible);
        SetParamRow(LblGaugeTickLen, GaugeTickLenBox, visible);
        SetParamRow(LblGaugeNeedleLen, GaugeNeedleLenBox, visible);
        SetParamRow(LblGaugeNeedleWidth, GaugeNeedleWidthBox, visible);
        SetParamRow(LblGaugeCenterRadius, GaugeCenterRadiusBox, visible);
        SetParamRow(LblGaugeShowValue, GaugeShowValueCheck, visible);
        SetParamRow(LblGaugeRanges, GaugeRangesBox, visible);
    }

    private void ShowLampRows(bool visible)
    {
        SetParamRow(LblLampSize, LampSizeBox, visible);
        SetParamRow(LblLampRanges, LampRangesBox, visible);
    }

    private void ShowButtonRows(bool visible)
    {
        SetParamRow(LblBtnConfirm, ConfirmTextBox, visible);
        SetParamRow(LblBtnConfirmYes, ConfirmYesBox, visible);
        SetParamRow(LblBtnConfirmNo, ConfirmNoBox, visible);
        SetParamRow(LblBtnConfirmWait, ConfirmWaitMsBox, visible);
        SetParamRow(LblBtnHold, HoldMsBox, visible);
        SetParamRow(LblBtnLongPress, LongPressMsBox, visible);
        SetParamRow(LblBtnOffStyle, OffStyleCombo, visible);
    }

    /// <summary>批次 1a 的字段回填。</summary>
    private void FillDetail1aFields(HmiControl control)
    {
        GaugeArcWidthBox.Value = control.GaugeArcWidth;
        GaugeTickLenBox.Value = control.GaugeTickLen;
        GaugeNeedleLenBox.Value = control.GaugeNeedleLen;
        GaugeNeedleWidthBox.Value = control.GaugeNeedleWidth;
        GaugeCenterRadiusBox.Value = control.GaugeCenterRadius;
        GaugeShowValueCheck.IsChecked = control.GaugeShowValue;
        GaugeRangesBox.Text = control.GaugeRanges ?? "";
        LampSizeBox.Value = control.LampSize;
        LampRangesBox.Text = control.LampRanges ?? "";
        ConfirmTextBox.Text = control.ButtonConfirmText ?? "";
        ConfirmYesBox.Text = control.ButtonConfirmYes ?? "";
        ConfirmNoBox.Text = control.ButtonConfirmNo ?? "";
        ConfirmWaitMsBox.Value = control.ButtonConfirmWaitMs;
        HoldMsBox.Value = control.ButtonHoldMs;
        LongPressMsBox.Value = control.ButtonLongPressMs;
        SelectComboByTag(OffStyleCombo, control.ButtonOffStyle, 0);
    }
}
