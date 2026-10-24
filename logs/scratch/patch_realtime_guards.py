p = 'tests/HmiEditor.App.UiTests/AppNumberBoxWiringCensusTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('new("PropertyPanelView.axaml", 61, "enumerated", "PropertyPanelWiringTests.cs"),',
              'new("PropertyPanelView.axaml", 63, "enumerated", "PropertyPanelWiringTests.cs"),')
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/ControlTypeDisplayTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('Views/ToolboxView.axaml.cs: 期望 29 处', 'Views/ToolboxView.axaml.cs: 期望 30 处')
s = s.replace('("Views/ToolboxView.axaml.cs", 29)', '("Views/ToolboxView.axaml.cs", 30)')
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/ToolboxIconDictionaryTests.cs'
s = open(p, encoding='utf-8').read()
# 差异表里加 RealtimeCurve（与 HistoryCurve 同样：工具箱用内联几何，没有字典镜像键）
import re
m = re.search(r'\[HmiControlType\.HistoryCurve\]\s*=\s*"([^"]*)"', s)
if m and 'RealtimeCurve' not in s:
    s = s.replace(m.group(0), m.group(0) + '\n        [HmiControlType.RealtimeCurve] = "与 HistoryCurve 同批新增：工具箱用内联几何，字典里没有独立镜像键"', 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '        ["AlarmBlinkMsBox"] = "报警闪烁周期：同上",'
add = anchor + '''
        ["SampleMsBox"] = "实时曲线采样周期：夹具里还没有 RealtimeCurve 类型",
        ["HistorySBox"] = "实时曲线保留历史：夹具里还没有 RealtimeCurve 类型",'''
if 'SampleMsBox' not in s:
    s = s.replace(anchor, add, 1)
open(p, 'w', encoding='utf-8').write(s)
print('guards ok')
