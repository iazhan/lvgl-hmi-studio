import io

# ---------- Parameters.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Parameters.cs'
s = open(p, encoding='utf-8', newline='').read()
miss = []


def rep(a, b):
    global s
    if a not in s:
        miss.append(a[:60])
        return
    s = s.replace(a, b, 1)


rep('''        DetailParamExpander.IsVisible = false;''', '''        DetailParamExpander.IsVisible = false;
        // 批次 2：曲线外观与刻度
        SetParamRow(LblCurveAxis, CurveAxisColorBox, false);
        SetParamRow(LblCurveBg, CurveBgColorBox, false);
        SetParamRow(LblCurveBorder, CurveBorderColorBox, false);
        SetParamRow(LblCurveGrid, CurveGridColorBox, false);
        SetParamRow(LblCurveTick, CurveTickColorBox, false);
        SetParamRow(LblCurveLabel, CurveLabelColorBox, false);
        SetParamRow(LblCurveShowGrid, CurveGridCheck, false);
        SetParamRow(LblCurveLegend, CurveLegendCheck, false);
        SetParamRow(LblCurveShowAxis, CurveShowAxisCheck, false);
        SetParamRow(LblCurveShowLabels, CurveShowLabelsCheck, false);
        SetParamRow(LblCurveDirection, CurveDirectionCombo, false);
        SetParamRow(LblCurveXFormat, CurveXFormatCombo, false);
        SetParamRow(LblCurveXDiv, CurveXDivBox, false);
        SetParamRow(LblCurveXMinor, CurveXMinorBox, false);
        SetParamRow(LblCurveXLabelStep, CurveXLabelStepBox, false);
        SetParamRow(LblCurveYDiv, CurveYDivBox, false);
        SetParamRow(LblCurveYMinor, CurveYMinorBox, false);
        SetParamRow(LblCurveYDecimals, CurveYDecimalsBox, false);
        SetParamRow(LblCurveYDigits, CurveYDigitsBox, false);
        SetParamRow(LblCurveY2Min, CurveY2MinBox, false);
        SetParamRow(LblCurveY2Max, CurveY2MaxBox, false);
        SetParamRow(LblCurveY2Decimals, CurveY2DecimalsBox, false);
        SetParamRow(LblCurveY2Digits, CurveY2DigitsBox, false);
        SetParamRow(LblCurveLastMin, CurveLastMinBox, false);
        CurveParamExpander.IsVisible = false;''')

rep('''            case HmiControlType.HistoryCurve:
                SetParamRow(LblCgTrendGroup, CgTrendGroupBox, true);
                SetParamRow(LblCgSpanMin, CgSpanMinBox, true);
                SetParamRow(LblCgCurveVars, CgCurveVarsBox, true);
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                break;''', '''            case HmiControlType.HistoryCurve:
                SetParamRow(LblCgTrendGroup, CgTrendGroupBox, true);
                SetParamRow(LblCgSpanMin, CgSpanMinBox, true);
                SetParamRow(LblCgCurveVars, CgCurveVarsBox, true);
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                // 批次 2：曲线外观与刻度
                SetParamRow(LblCurveAxis, CurveAxisColorBox, true);
                SetParamRow(LblCurveBg, CurveBgColorBox, true);
                SetParamRow(LblCurveBorder, CurveBorderColorBox, true);
                SetParamRow(LblCurveGrid, CurveGridColorBox, true);
                SetParamRow(LblCurveTick, CurveTickColorBox, true);
                SetParamRow(LblCurveLabel, CurveLabelColorBox, true);
                SetParamRow(LblCurveShowGrid, CurveGridCheck, true);
                SetParamRow(LblCurveLegend, CurveLegendCheck, true);
                SetParamRow(LblCurveShowAxis, CurveShowAxisCheck, true);
                SetParamRow(LblCurveShowLabels, CurveShowLabelsCheck, true);
                SetParamRow(LblCurveDirection, CurveDirectionCombo, true);
                SetParamRow(LblCurveXFormat, CurveXFormatCombo, true);
                SetParamRow(LblCurveXDiv, CurveXDivBox, true);
                SetParamRow(LblCurveXMinor, CurveXMinorBox, true);
                SetParamRow(LblCurveXLabelStep, CurveXLabelStepBox, true);
                SetParamRow(LblCurveYDiv, CurveYDivBox, true);
                SetParamRow(LblCurveYMinor, CurveYMinorBox, true);
                SetParamRow(LblCurveYDecimals, CurveYDecimalsBox, true);
                SetParamRow(LblCurveYDigits, CurveYDigitsBox, true);
                SetParamRow(LblCurveY2Min, CurveY2MinBox, true);
                SetParamRow(LblCurveY2Max, CurveY2MaxBox, true);
                SetParamRow(LblCurveY2Decimals, CurveY2DecimalsBox, true);
                SetParamRow(LblCurveY2Digits, CurveY2DigitsBox, true);
                SetParamRow(LblCurveLastMin, CurveLastMinBox, true);
                CurveParamExpander.IsVisible = true;
                break;''')

rep('''        SelectComboByTag(TabPosCombo, control.TabPos, 0);''', '''        SelectComboByTag(TabPosCombo, control.TabPos, 0);
        CurveAxisColorBox.Text = control.CurveAxisColor ?? "";
        CurveBgColorBox.Text = control.CurveBgColor ?? "";
        CurveBorderColorBox.Text = control.CurveBorderColor ?? "";
        CurveGridColorBox.Text = control.CurveGridColor ?? "";
        CurveTickColorBox.Text = control.CurveTickColor ?? "";
        CurveLabelColorBox.Text = control.CurveLabelColor ?? "";
        CurveGridCheck.IsChecked = control.CurveGrid;
        CurveLegendCheck.IsChecked = control.CurveLegend;
        CurveShowAxisCheck.IsChecked = control.CurveShowAxis;
        CurveShowLabelsCheck.IsChecked = control.CurveShowLabels;
        SelectComboByTag(CurveDirectionCombo, control.CurveDirection, 0);
        SelectComboByTag(CurveXFormatCombo, control.CurveXFormat, 0);
        CurveXDivBox.Value = control.CurveXDiv;
        CurveXMinorBox.Value = control.CurveXMinor;
        CurveXLabelStepBox.Value = control.CurveXLabelStep;
        CurveYDivBox.Value = control.CurveYDiv;
        CurveYMinorBox.Value = control.CurveYMinor;
        CurveYDecimalsBox.Value = control.CurveYDecimals;
        CurveYDigitsBox.Value = control.CurveYDigits;
        CurveY2MinBox.Value = control.CurveY2Min;
        CurveY2MaxBox.Value = control.CurveY2Max;
        CurveY2DecimalsBox.Value = control.CurveY2Decimals;
        CurveY2DigitsBox.Value = control.CurveY2Digits;
        CurveLastMinBox.Value = control.CurveRangeLastMin;''')

open(p, 'w', encoding='utf-8', newline='').write(s)
print('Parameters MISS:', miss)
