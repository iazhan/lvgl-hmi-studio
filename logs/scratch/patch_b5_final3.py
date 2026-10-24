p = 'src/HmiEditor.App/Views/PropertyPanelView.Parameters.cs'
s = open(p, encoding='utf-8', newline='').read()

new_file = '''namespace HmiEditor.App.Views;

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
'''
open('src/HmiEditor.App/Views/PropertyPanelView.Parameters.Detail1a.cs', 'w', encoding='utf-8', newline='').write(new_file)

# 1) 隐藏区 → 三次调用
start = s.index('        SetParamRow(LblGaugeArcWidth, GaugeArcWidthBox, false);')
end = s.index('        SetParamRow(LblBtnOffStyle, OffStyleCombo, false);\n', start) + len('        SetParamRow(LblBtnOffStyle, OffStyleCombo, false);\n')
s = s[:start] + '''        ShowGaugeRows(false);
        ShowLampRows(false);
        ShowButtonRows(false);
''' + s[end:]

# 2) Gauge 分支
start = s.index('            case HmiControlType.Gauge:')
end = s.index('                break;\n', s.index('SetParamRow(LblGaugeRanges, GaugeRangesBox, true);', start)) + len('                break;\n')
s = s[:start] + '''            case HmiControlType.Gauge:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblTickCount, TickCountBox, true);
                ShowGaugeRows(true);
                DetailParamExpander.IsVisible = true;
                break;
''' + s[end:]

# 3) Led 分支
start = s.index('            case HmiControlType.Led:')
end = s.index('                break;\n', s.index('SetParamRow(LblLampRanges, LampRangesBox, true);', start)) + len('                break;\n')
s = s[:start] + '''            case HmiControlType.Led:
                SetParamRow(LblBrightness, BrightnessBox, true);
                ShowLampRows(true);
                DetailParamExpander.IsVisible = true;
                break;
''' + s[end:]

# 4) Button 分支
start = s.index('            case HmiControlType.Button:')
end = s.index('                break;\n', s.index('SetParamRow(LblBtnOffStyle, OffStyleCombo, true);', start)) + len('                break;\n')
s = s[:start] + '''            case HmiControlType.Button:
                ShowButtonRows(true);
                DetailParamExpander.IsVisible = true;
                break;
''' + s[end:]

# 5) 回填
start = s.index('        GaugeArcWidthBox.Value = control.GaugeArcWidth;')
end = s.index('        SelectComboByTag(OffStyleCombo, control.ButtonOffStyle, 0);\n', start) + len('        SelectComboByTag(OffStyleCombo, control.ButtonOffStyle, 0);\n')
s = s[:start] + '        FillDetail1aFields(control);\n' + s[end:]

open(p, 'w', encoding='utf-8', newline='').write(s)
print('lines now:', s.count('\n') + 1)
