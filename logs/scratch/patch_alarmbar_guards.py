# 1) 数字框 63 -> 65
p = 'tests/HmiEditor.App.UiTests/AppNumberBoxWiringCensusTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('new("PropertyPanelView.axaml", 63, "enumerated", "PropertyPanelWiringTests.cs"),',
              'new("PropertyPanelView.axaml", 65, "enumerated", "PropertyPanelWiringTests.cs"),')
open(p, 'w', encoding='utf-8').write(s)

# 2) ToolboxView 引用 30 -> 31
p = 'tests/HmiEditor.App.UiTests/ControlTypeDisplayTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('("Views/ToolboxView.axaml.cs", 30)', '("Views/ToolboxView.axaml.cs", 31)')
s = s.replace('期望 30 处', '期望 31 处')
open(p, 'w', encoding='utf-8').write(s)

# 3) 图标差异表 +AlarmBar
p = 'tests/HmiEditor.App.UiTests/ToolboxIconDictionaryTests.cs'
s = open(p, encoding='utf-8').read()
a = '        [HmiControlType.RealtimeCurve] = "与 HistoryCurve 同批新增：工具箱用内联几何，字典里没有独立镜像键",'
b = a + '\n        [HmiControlType.AlarmBar] = "与 AlarmList 同批：工具箱用内联几何，字典里没有独立镜像键",'
if 'HmiControlType.AlarmBar]' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 4) TextBox 台账
p = 'tests/HmiEditor.App.UiTests/PropertyPanelTextBoxWiringTests.cs'
s = open(p, encoding='utf-8').read()
a = '        ["AlarmTimeFormatBox"] = "报警时间格式：同上（**XAML 属性式接线**，不在本面）",'
b = a + '\n        ["AlarmNormalTextBox"] = "报警条无报警文字：同上（**XAML 属性式接线**，不在本面）",'
if 'AlarmNormalTextBox' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 5) 属性式登记 + 计数 47 -> 48
p = 'tests/HmiEditor.App.UiTests/AppXamlAttributeWiringFaceTests.cs'
s = open(p, encoding='utf-8').read()
a = '''        new("PropertyPanelView.axaml", "AlarmTimeFormatBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：报警时间格式"),'''
b = a + '''
        new("PropertyPanelView.axaml", "AlarmNormalTextBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：报警条无报警文字"),'''
if 'AlarmNormalTextBox' not in s:
    s = s.replace(a, b, 1)
s = s.replace('Assert.Equal(47, driven);', 'Assert.Equal(48, driven);')
s = s.replace('批次 2 +8', '批次 2 +9')
open(p, 'w', encoding='utf-8').write(s)

# 6) 豁免表
p = 'tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs'
s = open(p, encoding='utf-8').read()
a = '        ["HistorySBox"] = "实时曲线保留历史：夹具里还没有 RealtimeCurve 类型",'
b = a + '''
        ["AlarmScrollMsBox"] = "报警条滚动间隔：夹具里还没有 AlarmBar 类型",
        ["AlarmScrollCharsBox"] = "报警条滚动步长：同上",'''
if 'AlarmScrollMsBox' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)
print('guards ok')
