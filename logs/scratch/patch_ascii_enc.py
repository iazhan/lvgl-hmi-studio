# 1) 下拉的第二项：契约写 ascii，运行时只认 gbk/utf8 → 按运行时改
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml'
s = open(p, encoding='utf-8', newline='').read()
a = '<ComboBoxItem Content="{res:Loc prop.detail.ascii.encoding.ascii}" Tag="ascii"/>'
b = '<ComboBoxItem Content="{res:Loc prop.detail.ascii.encoding.utf8}" Tag="utf8"/>'
if a in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8', newline='').write(s)

# 2) 导出白名单
p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()
a = 'props["encoding"] = OneOf(control.AsciiEncoding, "gbk", "ascii", "gbk", where, "ascii_display.encoding");'
b = ('// 运行时只认 gbk / utf8（build_ascii_display 里是硬校验）；契约写的 "ascii" 会让整页建不出来\n'
     '                props["encoding"] = OneOf(control.AsciiEncoding, "gbk", "utf8", "gbk", where, "ascii_display.encoding");')
if a in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 3) 字典：ascii -> utf8
for p, txt in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', 'UTF-8'),
               ('src/HmiEditor.Core/Resources/Strings.en-US.json', 'UTF-8')]:
    s = open(p, encoding='utf-8').read()
    s = s.replace('"prop.detail.ascii.encoding.ascii": "ASCII"',
                  '"prop.detail.ascii.encoding.utf8": "%s"' % txt)
    open(p, 'w', encoding='utf-8').write(s)

# 4) 夹具
p = 'tests/HmiEditor.Export.Chenge.Tests/ExportFixture.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('AsciiEncoding = "ascii"', 'AsciiEncoding = "utf8"')
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.Export.Chenge.Tests/ChengeExportTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('Assert.Equal("ascii", ascii.GetProperty("encoding").GetString());',
              'Assert.Equal("utf8", ascii.GetProperty("encoding").GetString());')
open(p, 'w', encoding='utf-8').write(s)
print('ascii encoding fixed')
