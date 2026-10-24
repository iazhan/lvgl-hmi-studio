import re

p = 'tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs'
s = open(p, encoding='utf-8').read()

# 删掉写错的 17 行
s = '\n'.join(l for l in s.split('\n') if '["%s"]' not in l)

axaml = open('src/HmiEditor.App/Views/PropertyPanelView.axaml', encoding='utf-8').read()
names = set(re.findall(r'x:Name="((?:Chart|Xy|Hist|Recipe|Ascii)[A-Za-z0-9]+)"', axaml))
names = sorted(n for n in names if not n.startswith('Lbl'))

anchor = '        ["ShapeKindCombo"] = "通用图元的种类下拉：夹具里还没有 Shape 类型",'
missing = [n for n in names if ('["%s"]' % n) not in s]
lines = []
for n in missing:
    lines.append('        ["%s"] = "批次 5 控件专属行：夹具里还没有这些类型，整块显隐",' % n)
if lines:
    s = s.replace(anchor, anchor + '\n' + '\n'.join(lines), 1)
open(p, 'w', encoding='utf-8').write(s)
print('fixed, added:', len(missing))
