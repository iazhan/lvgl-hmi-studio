import re

# 1) 选择类 43 -> 45
p = 'tests/HmiEditor.App.UiTests/SelectionFaceClassificationGuardTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('("PropertyPanelView", 43),', '("PropertyPanelView", 45),')
open(p, 'w', encoding='utf-8').write(s)

# 2) 数字框 66 -> 68
p = 'tests/HmiEditor.App.UiTests/AppNumberBoxWiringCensusTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('new("PropertyPanelView.axaml", 66, "enumerated", "PropertyPanelWiringTests.cs"),',
              'new("PropertyPanelView.axaml", 68, "enumerated", "PropertyPanelWiringTests.cs"),')
open(p, 'w', encoding='utf-8').write(s)

# 3) 工具箱引用 38 -> 43
p = 'tests/HmiEditor.App.UiTests/ControlTypeDisplayTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('("Views/ToolboxView.axaml.cs", 38)', '("Views/ToolboxView.axaml.cs", 43)')
s = s.replace('期望 38 处', '期望 43 处')
open(p, 'w', encoding='utf-8').write(s)

# 4) 图标差异表 +5
p = 'tests/HmiEditor.App.UiTests/ToolboxIconDictionaryTests.cs'
s = open(p, encoding='utf-8').read()
a = '        [HmiControlType.Shape] = "同上（通用图元，kind 在属性里选）",'
b = a + '''
        [HmiControlType.Chart] = "批次 5：工具箱用内联几何，字典里没有独立镜像键",
        [HmiControlType.XyCurve] = "同上",
        [HmiControlType.HistoryTable] = "同上",
        [HmiControlType.RecipeTable] = "同上",
        [HmiControlType.AsciiDisplay] = "同上",'''
if 'HmiControlType.Chart]' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 5) TextBox 台账（7 个）
p = 'tests/HmiEditor.App.UiTests/PropertyPanelTextBoxWiringTests.cs'
s = open(p, encoding='utf-8').read()
a = '        ["PipeChannelColorBox"] = "管道通道底色：同上（**XAML 属性式接线**，不在本面）",'
b = a + '''
        // 批次 5
        ["ChartSeriesBox"] = "图表系列：同上（**XAML 属性式接线**，不在本面）",
        ["XyTagXBox"] = "XY 曲线的 X 变量：同上",
        ["XyTagYBox"] = "XY 曲线的 Y 变量：同上",
        ["HistColumnsBox"] = "历史表列定义：同上",
        ["HistGroupBox"] = "历史表趋势组：同上",
        ["RecipeColumnsBox"] = "配方表列定义：同上",
        ["RecipeGroupBox"] = "配方表配方组：同上",'''
if 'ChartSeriesBox' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 6) 属性式登记 +7，计数 49 -> 56
p = 'tests/HmiEditor.App.UiTests/AppXamlAttributeWiringFaceTests.cs'
s = open(p, encoding='utf-8').read()
a = '''        new("PropertyPanelView.axaml", "PipeChannelColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：管道通道底色"),'''
b = a + '''
        new("PropertyPanelView.axaml", "ChartSeriesBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：图表系列"),
        new("PropertyPanelView.axaml", "XyTagXBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：XY 曲线的 X 变量"),
        new("PropertyPanelView.axaml", "XyTagYBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：XY 曲线的 Y 变量"),
        new("PropertyPanelView.axaml", "HistColumnsBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：历史表列定义"),
        new("PropertyPanelView.axaml", "HistGroupBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：历史表趋势组"),
        new("PropertyPanelView.axaml", "RecipeColumnsBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：配方表列定义"),
        new("PropertyPanelView.axaml", "RecipeGroupBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：配方表配方组"),'''
if 'ChartSeriesBox' not in s:
    s = s.replace(a, b, 1)
s = s.replace('Assert.Equal(49, driven);', 'Assert.Equal(56, driven);')
s = s.replace('批次 2 +10', '批次 2 +10、批次 6 +7')
open(p, 'w', encoding='utf-8').write(s)

# 7) 豁免：批次 5 的整块显隐行
axaml = open('src/HmiEditor.App/Views/PropertyPanelView.axaml', encoding='utf-8').read()
names = set(re.findall(r'x:Name="((?:Chart|Xy|Hist|Recipe|Ascii)[A-Za-z0-9]+)"', axaml))
names = sorted(n for n in names if not n.startswith('Lbl'))

p = 'tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '        ["ShapeKindCombo"] = "通用图元的种类下拉：夹具里还没有 Shape 类型",'
missing = [n for n in names if f'["{n}"]' not in s]
add = ''.join('\n        ["%s"] = "批次 5 控件专属行：夹具里还没有这些类型，整块显隐" % n' for n in missing)
if add:
    s = s.replace(anchor, anchor + add, 1)
open(p, 'w', encoding='utf-8').write(s)
print('guards ok, exempt added:', len(missing))
