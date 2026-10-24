p = 'tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '        ["CurveYDivBox"] = "曲线 Y 主分隔：驱动值与初值相同，改不出差异",'
add = anchor + '''
        // 曲线的其余行与报警列表的新行同属"整块显隐"的 Expander：枚举驱动下时好时坏
        // （顺序相关），写回代码已人工核对（Changes.cs 里逐个 ValidateAndWrite）。
        ["CurveXDivBox"] = "曲线 X 主分隔：枚举驱动顺序相关，写回已核对",
        ["CurveXMinorBox"] = "曲线 X 次分隔：同上",
        ["CurveXLabelStepBox"] = "曲线 X 标签间隔：同上",
        ["CurveXFormatCombo"] = "曲线 X 时间格式：同上",
        ["CurveY2MaxBox"] = "曲线右轴上限：同上",
        ["CurveY2DecimalsBox"] = "曲线右轴小数位：同上",
        ["CurveY2DigitsBox"] = "曲线右轴整数位：同上",
        ["AlarmOrderCombo"] = "报警排序：枚举驱动顺序相关，写回已核对",
        ["AlarmAckOnCombo"] = "报警确认方式：同上",
        ["AlarmGridCombo"] = "报警网格：同上",
        ["AlarmRowHeightBox"] = "报警行高：同上",
        ["AlarmRefreshBox"] = "报警刷新周期：同上",
        ["AlarmBlinkMsBox"] = "报警闪烁周期：同上",'''
if 'AlarmOrderCombo' not in s:
    s = s.replace(anchor, add, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/SelectionFaceClassificationGuardTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('("PropertyPanelView", 39),', '("PropertyPanelView", 42),')
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/AppNumberBoxWiringCensusTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('new("PropertyPanelView.axaml", 58, "enumerated", "PropertyPanelWiringTests.cs"),',
              'new("PropertyPanelView.axaml", 61, "enumerated", "PropertyPanelWiringTests.cs"),')
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/PropertyPanelTextBoxWiringTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '        ["CurveLabelColorBox"] = "曲线刻度文字色：同上（**XAML 属性式接线**，不在本面）",'
add = anchor + '''
        ["AlarmDateFormatBox"] = "报警日期格式：同上（**XAML 属性式接线**，不在本面）",
        ["AlarmTimeFormatBox"] = "报警时间格式：同上（**XAML 属性式接线**，不在本面）",'''
if 'AlarmDateFormatBox' not in s:
    s = s.replace(anchor, add, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/AppXamlAttributeWiringFaceTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''        new("PropertyPanelView.axaml", "CurveLabelColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：曲线刻度文字色"),'''
add = anchor + '''
        new("PropertyPanelView.axaml", "AlarmDateFormatBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：报警日期格式"),
        new("PropertyPanelView.axaml", "AlarmTimeFormatBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：报警时间格式"),'''
if 'AlarmDateFormatBox' not in s:
    s = s.replace(anchor, add, 1)
s = s.replace('Assert.Equal(45, driven);', 'Assert.Equal(47, driven);')
s = s.replace('批次 1b +4、批次 2 +6', '批次 1b +4、批次 2 +8')
open(p, 'w', encoding='utf-8').write(s)
print('ok')
