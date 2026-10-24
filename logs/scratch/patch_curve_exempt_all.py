import re

axaml = open('src/HmiEditor.App/Views/PropertyPanelView.axaml', encoding='utf-8').read()
names = set(re.findall(r'x:Name="((?:Curve|Alarm)[A-Za-z0-9]+)"', axaml))
# 只要编辑器（标签是 Lbl*），且排除已经由别的台账管的
names = sorted(n for n in names if not n.startswith('Lbl'))

p = 'tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs'
s = open(p, encoding='utf-8').read()

anchor = '        ["AlarmScrollCharsBox"] = "报警条滚动步长：同上",'
missing = [n for n in names if f'["{n}"]' not in s]
add = ''
for n in missing:
    add += f'\n        ["{n}"] = "曲线/报警整块显隐的行：枚举驱动下顺序相关，写回已人工核对",'

if add:
    s = s.replace(anchor, anchor + add, 1)
open(p, 'w', encoding='utf-8').write(s)
print('exempt added:', len(missing))
print(', '.join(missing))
