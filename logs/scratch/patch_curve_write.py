# ---------- Changes.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Changes.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''            if (TabPosCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.TabPos), GetComboTag(TabPosCombo), v => _currentControl.TabPos = v);'''
b = a + '''
            // 批次 2：曲线外观与刻度（面板可见时才写）
            if (CurveAxisColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveAxisColor), string.IsNullOrWhiteSpace(CurveAxisColorBox.Text) ? null : CurveAxisColorBox.Text.Trim(), v => _currentControl.CurveAxisColor = v);
            if (CurveBgColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveBgColor), string.IsNullOrWhiteSpace(CurveBgColorBox.Text) ? null : CurveBgColorBox.Text.Trim(), v => _currentControl.CurveBgColor = v);
            if (CurveBorderColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveBorderColor), string.IsNullOrWhiteSpace(CurveBorderColorBox.Text) ? null : CurveBorderColorBox.Text.Trim(), v => _currentControl.CurveBorderColor = v);
            if (CurveGridColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveGridColor), string.IsNullOrWhiteSpace(CurveGridColorBox.Text) ? null : CurveGridColorBox.Text.Trim(), v => _currentControl.CurveGridColor = v);
            if (CurveTickColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveTickColor), string.IsNullOrWhiteSpace(CurveTickColorBox.Text) ? null : CurveTickColorBox.Text.Trim(), v => _currentControl.CurveTickColor = v);
            if (CurveLabelColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveLabelColor), string.IsNullOrWhiteSpace(CurveLabelColorBox.Text) ? null : CurveLabelColorBox.Text.Trim(), v => _currentControl.CurveLabelColor = v);
            if (CurveGridCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveGrid), CurveGridCheck.IsChecked == true, v => _currentControl.CurveGrid = v);
            if (CurveLegendCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveLegend), CurveLegendCheck.IsChecked == true, v => _currentControl.CurveLegend = v);
            if (CurveShowAxisCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveShowAxis), CurveShowAxisCheck.IsChecked == true, v => _currentControl.CurveShowAxis = v);
            if (CurveShowLabelsCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveShowLabels), CurveShowLabelsCheck.IsChecked == true, v => _currentControl.CurveShowLabels = v);
            if (CurveDirectionCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveDirection), GetComboTag(CurveDirectionCombo), v => _currentControl.CurveDirection = v);
            if (CurveXFormatCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveXFormat), GetComboTag(CurveXFormatCombo), v => _currentControl.CurveXFormat = v);
            if (CurveXDivBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveXDiv), (int)CurveXDivBox.Value, v => _currentControl.CurveXDiv = v);
            if (CurveXMinorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveXMinor), (int)CurveXMinorBox.Value, v => _currentControl.CurveXMinor = v);
            if (CurveXLabelStepBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveXLabelStep), (int)CurveXLabelStepBox.Value, v => _currentControl.CurveXLabelStep = v);
            if (CurveYDivBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveYDiv), (int)CurveYDivBox.Value, v => _currentControl.CurveYDiv = v);
            if (CurveYMinorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveYMinor), (int)CurveYMinorBox.Value, v => _currentControl.CurveYMinor = v);
            if (CurveYDecimalsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveYDecimals), (int)CurveYDecimalsBox.Value, v => _currentControl.CurveYDecimals = v);
            if (CurveYDigitsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveYDigits), (int)CurveYDigitsBox.Value, v => _currentControl.CurveYDigits = v);
            if (CurveY2MinBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveY2Min), (int)CurveY2MinBox.Value, v => _currentControl.CurveY2Min = v);
            if (CurveY2MaxBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveY2Max), (int)CurveY2MaxBox.Value, v => _currentControl.CurveY2Max = v);
            if (CurveY2DecimalsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveY2Decimals), (int)CurveY2DecimalsBox.Value, v => _currentControl.CurveY2Decimals = v);
            if (CurveY2DigitsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveY2Digits), (int)CurveY2DigitsBox.Value, v => _currentControl.CurveY2Digits = v);
            if (CurveLastMinBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveRangeLastMin), (int)CurveLastMinBox.Value, v => _currentControl.CurveRangeLastMin = v);'''
if a in s:
    s = s.replace(a, b, 1)
    print('Changes ok')
else:
    print('Changes MISS')
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 挂 ValueChanged ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''        TabHeightBox.ValueChanged += OnNumberChanged;'''
b = a + '''
        // 批次 2：曲线刻度
        CurveXDivBox.ValueChanged += OnNumberChanged;
        CurveXMinorBox.ValueChanged += OnNumberChanged;
        CurveXLabelStepBox.ValueChanged += OnNumberChanged;
        CurveYDivBox.ValueChanged += OnNumberChanged;
        CurveYMinorBox.ValueChanged += OnNumberChanged;
        CurveYDecimalsBox.ValueChanged += OnNumberChanged;
        CurveYDigitsBox.ValueChanged += OnNumberChanged;
        CurveY2MinBox.ValueChanged += OnNumberChanged;
        CurveY2MaxBox.ValueChanged += OnNumberChanged;
        CurveY2DecimalsBox.ValueChanged += OnNumberChanged;
        CurveY2DigitsBox.ValueChanged += OnNumberChanged;
        CurveLastMinBox.ValueChanged += OnNumberChanged;'''
if a in s:
    s = s.replace(a, b, 1)
    print('wiring ok')
else:
    print('wiring MISS')
open(p, 'w', encoding='utf-8', newline='').write(s)
