namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板「控件功能参数」里**仪表 / 指示灯 / 按钮**专属的行，以及它们的回填。
/// 拆文件的唯一原因：<c>Parameters.cs</c> 有 500 行上限（与曲线、报警、内容型同规矩）。
/// 行按控件类型整组显隐 —— 隐藏的行不占 RowSpacing，但必须显式复位，否则切控件类型时
/// 上一类的行会留在下一个控件上。
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
        // 显示刻度 / 显示指针：仪表的两组键都由它们驱动（ticks / style）。这两行以前
        // 对**任何**控件都不显示，"刻度数"那个数字框因此永远只是个孤立的数
        SetParamRow(LblShowTicks, ShowTicksCheck, visible);
        SetParamRow(LblShowPointer, ShowPointerCheck, visible);
    }

    private void ShowLampRows(bool visible)
    {
        SetParamRow(LblLampSize, LampSizeBox, visible);
    }

    /// <summary>
    /// 按钮的交互行。它们在「动作与高级」的交互行为组里，各行是一个具名 <c>Grid</c>，
    /// 显隐切整行（切行内元素会在 StackPanel 里留空白）。
    /// </summary>
    private void ShowButtonRows(bool visible)
    {
        RowBtnConfirm.IsVisible = visible;
        RowBtnConfirmYes.IsVisible = visible;
        RowBtnConfirmNo.IsVisible = visible;
        RowBtnConfirmWait.IsVisible = visible;
        RowBtnHold.IsVisible = visible;
        RowBtnLongPress.IsVisible = visible;
        RowBtnOffStyle.IsVisible = visible;
    }

    /// <summary>仪表 / 指示灯 / 按钮那些行的回填（值与显隐无关，一律填）。</summary>
    private void FillGaugeLampButtonFields(HmiControl control)
    {
        GaugeArcWidthBox.Value = control.GaugeArcWidth;
        GaugeTickLenBox.Value = control.GaugeTickLen;
        GaugeNeedleLenBox.Value = control.GaugeNeedleLen;
        GaugeNeedleWidthBox.Value = control.GaugeNeedleWidth;
        GaugeCenterRadiusBox.Value = control.GaugeCenterRadius;
        GaugeShowValueCheck.IsChecked = control.GaugeShowValue;
        LampSizeBox.Value = control.LampSize;
        ConfirmTextBox.Text = control.ButtonConfirmText ?? "";
        ConfirmYesBox.Text = control.ButtonConfirmYes ?? "";
        ConfirmNoBox.Text = control.ButtonConfirmNo ?? "";
        ConfirmWaitMsBox.Value = control.ButtonConfirmWaitMs;
        HoldMsBox.Value = control.ButtonHoldMs;
        LongPressMsBox.Value = control.ButtonLongPressMs;
        SelectComboByTag(OffStyleCombo, control.ButtonOffStyle, 0);
    }
}
