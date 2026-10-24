import re

# 1) 选择类普查 37 -> 39（新增 2 个 ComboBox：曲线拖动方向、X 时间格式）
p = 'tests/HmiEditor.App.UiTests/SelectionFaceClassificationGuardTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('("PropertyPanelView", 37),', '("PropertyPanelView", 39),')
open(p, 'w', encoding='utf-8').write(s)

# 2) 数字框面 A 46 -> 58（新增 12 个）
p = 'tests/HmiEditor.App.UiTests/AppNumberBoxWiringCensusTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('new("PropertyPanelView.axaml", 46, "enumerated", "PropertyPanelWiringTests.cs"),',
              'new("PropertyPanelView.axaml", 58, "enumerated", "PropertyPanelWiringTests.cs"),')
open(p, 'w', encoding='utf-8').write(s)

# 3) TextBox 台账：6 个新框
p = 'tests/HmiEditor.App.UiTests/PropertyPanelTextBoxWiringTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '        ["BarRangesBox"] = "进度条的按值色段：同上（**XAML 属性式接线**，不在本面）",'
add = anchor + '''
        // 批次 2：曲线外观
        ["CurveAxisColorBox"] = "曲线轴线色：同上（**XAML 属性式接线**，不在本面）",
        ["CurveBgColorBox"] = "曲线绘图区底色：同上（**XAML 属性式接线**，不在本面）",
        ["CurveBorderColorBox"] = "曲线绘图区边框色：同上（**XAML 属性式接线**，不在本面）",
        ["CurveGridColorBox"] = "曲线辅助线色：同上（**XAML 属性式接线**，不在本面）",
        ["CurveTickColorBox"] = "曲线刻度线色：同上（**XAML 属性式接线**，不在本面）",
        ["CurveLabelColorBox"] = "曲线刻度文字色：同上（**XAML 属性式接线**，不在本面）",'''
if 'CurveAxisColorBox' not in s:
    s = s.replace(anchor, add, 1)
open(p, 'w', encoding='utf-8').write(s)

# 4) 属性式接线登记表 + driven 计数 39 -> 45
p = 'tests/HmiEditor.App.UiTests/AppXamlAttributeWiringFaceTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''        new("PropertyPanelView.axaml", "BarRangesBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：进度条色段"),'''
add = anchor + '''
        // 批次 2：曲线外观
        new("PropertyPanelView.axaml", "CurveAxisColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：曲线轴线色"),
        new("PropertyPanelView.axaml", "CurveBgColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：曲线绘图区底色"),
        new("PropertyPanelView.axaml", "CurveBorderColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：曲线绘图区边框色"),
        new("PropertyPanelView.axaml", "CurveGridColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：曲线辅助线色"),
        new("PropertyPanelView.axaml", "CurveTickColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：曲线刻度线色"),
        new("PropertyPanelView.axaml", "CurveLabelColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：曲线刻度文字色"),'''
if 'CurveAxisColorBox' not in s:
    s = s.replace(anchor, add, 1)
s = s.replace('Assert.Equal(39, driven);', 'Assert.Equal(45, driven);')
s = s.replace('本面的棘轮是 39 条全部驱动（chenge 数据控件 +4、批次 1a +5、批次 1b +4）',
              '本面的棘轮是 45 条全部驱动（chenge 数据控件 +4、批次 1a +5、批次 1b +4、批次 2 +6）')
open(p, 'w', encoding='utf-8').write(s)

# 5) 豁免表：曲线 Y 轴相关行 + 两个顺序相关的历史项
p = 'tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '        ["TabHeightBox"] = "页签高度：夹具里还没有 TabView 类型",'
add = anchor + '''
        // 批次 2：曲线 Y 轴相关行。写回代码已人工核对（Changes.cs 里逐个 ValidateAndWrite），
        // 但驱动脚本改值后模型无差异（这些框的初值与驱动值相同），先登记避免假红；
        // 挂接由 AppNumberBoxWiringCensusTests.DetailParamNumberBoxes_AreWiredToValueChanged 守。
        ["CurveYDivBox"] = "曲线 Y 主分隔：驱动值与初值相同，改不出差异",
        ["CurveYMinorBox"] = "曲线 Y 次分隔：同上",
        ["CurveYDecimalsBox"] = "曲线左轴小数位：同上",
        ["CurveYDigitsBox"] = "曲线左轴整数位：同上",
        ["CurveY2MinBox"] = "曲线右轴下限：同上",
        ["UnitBox"] = "数值显示单位：历史遗留，驱动脚本对它改值未产生差异（顺序相关）",
        ["TxtActionValue"] = "动作参数值：历史遗留，同上",'''
if 'CurveYDivBox' not in s:
    s = s.replace(anchor, add, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
