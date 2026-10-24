# 1) 选择类 42 -> 43（ShapeKindCombo）
p = 'tests/HmiEditor.App.UiTests/SelectionFaceClassificationGuardTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('("PropertyPanelView", 42),', '("PropertyPanelView", 43),')
open(p, 'w', encoding='utf-8').write(s)

# 2) 工具箱引用 37 -> 38
p = 'tests/HmiEditor.App.UiTests/ControlTypeDisplayTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('("Views/ToolboxView.axaml.cs", 37)', '("Views/ToolboxView.axaml.cs", 38)')
s = s.replace('期望 37 处', '期望 38 处')
open(p, 'w', encoding='utf-8').write(s)

# 3) 图标差异表 +Shape
p = 'tests/HmiEditor.App.UiTests/ToolboxIconDictionaryTests.cs'
s = open(p, encoding='utf-8').read()
a = '        [HmiControlType.FrameAnim] = "同上",'
b = a + '\n        [HmiControlType.Shape] = "同上（通用图元，kind 在属性里选）",'
if 'HmiControlType.Shape]' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 4) 豁免表：ShapeKindCombo
p = 'tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs'
s = open(p, encoding='utf-8').read()
a = '        ["AlarmScrollCharsBox"] = "报警条滚动步长：同上",'
b = a + '\n        ["ShapeKindCombo"] = "通用图元的种类下拉：夹具里还没有 Shape 类型",'
if 'ShapeKindCombo' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
