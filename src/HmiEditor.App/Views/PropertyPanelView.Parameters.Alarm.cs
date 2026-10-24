namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板的**报警控件行**（alarm_list 与 alarm_bar）。
///
/// <para>抽出来的原因同曲线：<c>PropertyPanelView.Parameters.cs</c> 有 500 行上限，
/// 而报警这两类控件是整块显隐、整块回填，放在一起读起来也顺。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>报警列表专属行（10 项显示细节）。</summary>
    private void ShowAlarmListRows(bool visible)
    {
        SetParamRow(LblAlarmOrder, AlarmOrderCombo, visible);
        SetParamRow(LblAlarmAckOn, AlarmAckOnCombo, visible);
        SetParamRow(LblAlarmGrid, AlarmGridCombo, visible);
        SetParamRow(LblAlarmRowHeight, AlarmRowHeightBox, visible);
        SetParamRow(LblAlarmScrollbar, AlarmScrollbarCheck, visible);
        SetParamRow(LblAlarmRefresh, AlarmRefreshBox, visible);
        SetParamRow(LblAlarmBlink, AlarmBlinkCheck, visible);
        SetParamRow(LblAlarmBlinkMs, AlarmBlinkMsBox, visible);
        SetParamRow(LblAlarmDateFormat, AlarmDateFormatBox, visible);
        SetParamRow(LblAlarmTimeFormat, AlarmTimeFormatBox, visible);
    }

    /// <summary>报警条专属行（6 项）。</summary>
    private void ShowAlarmBarRows(bool visible)
    {
        SetParamRow(LblAlarmNormalText, AlarmNormalTextBox, visible);
        SetParamRow(LblAlarmOnlyUnacked, AlarmOnlyUnackedCheck, visible);
        SetParamRow(LblAlarmScrollMs, AlarmScrollMsBox, visible);
        SetParamRow(LblAlarmScrollChars, AlarmScrollCharsBox, visible);
        SetParamRow(LblAlarmShowDate, AlarmShowDateCheck, visible);
        SetParamRow(LblAlarmShowTime, AlarmShowTimeCheck, visible);
    }

    /// <summary>把报警相关字段回填到面板（列表与条共用同一批字段）。</summary>
    private void FillAlarmFields(HmiControl control)
    {
        SelectComboByTag(AlarmOrderCombo, control.AlarmOrder, 0);
        SelectComboByTag(AlarmAckOnCombo, control.AlarmAckOn, 1);
        SelectComboByTag(AlarmGridCombo, control.AlarmGrid, 0);
        AlarmRowHeightBox.Value = control.AlarmRowHeight;
        AlarmScrollbarCheck.IsChecked = control.AlarmScrollbar;
        AlarmRefreshBox.Value = control.AlarmRefreshMs;
        AlarmBlinkCheck.IsChecked = control.AlarmBlink;
        AlarmBlinkMsBox.Value = control.AlarmBlinkMs;
        AlarmDateFormatBox.Text = control.AlarmDateFormat ?? "";
        AlarmTimeFormatBox.Text = control.AlarmTimeFormat ?? "";

        AlarmNormalTextBox.Text = control.AlarmNormalText ?? "";
        AlarmOnlyUnackedCheck.IsChecked = control.AlarmOnlyUnacked;
        AlarmScrollMsBox.Value = control.AlarmScrollMs;
        AlarmScrollCharsBox.Value = control.AlarmScrollChars;
        AlarmShowDateCheck.IsChecked = control.AlarmShowDate;
        AlarmShowTimeCheck.IsChecked = control.AlarmShowTime;
    }
}
