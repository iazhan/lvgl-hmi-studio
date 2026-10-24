p = 'tests/HmiEditor.App.UiTests/AppNumberBoxWiringCensusTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('new("PropertyPanelView.axaml", 65, "enumerated", "PropertyPanelWiringTests.cs"),',
              'new("PropertyPanelView.axaml", 66, "enumerated", "PropertyPanelWiringTests.cs"),')
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/PropertyPanelTextBoxWiringTests.cs'
s = open(p, encoding='utf-8').read()
a = '        ["AlarmNormalTextBox"] = "报警条无报警文字：同上（**XAML 属性式接线**，不在本面）",'
b = a + '\n        ["PipeChannelColorBox"] = "管道通道底色：同上（**XAML 属性式接线**，不在本面）",'
if 'PipeChannelColorBox' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.App.UiTests/AppXamlAttributeWiringFaceTests.cs'
s = open(p, encoding='utf-8').read()
a = '''        new("PropertyPanelView.axaml", "AlarmNormalTextBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：报警条无报警文字"),'''
b = a + '''
        new("PropertyPanelView.axaml", "PipeChannelColorBox", "OnPropertyChanged",
            "enumerated@PropertyPanelWiringTests.cs", "同上：管道通道底色"),'''
if 'PipeChannelColorBox' not in s:
    s = s.replace(a, b, 1)
s = s.replace('Assert.Equal(48, driven);', 'Assert.Equal(49, driven);')
s = s.replace('批次 2 +9', '批次 2 +10')
open(p, 'w', encoding='utf-8').write(s)
print('ok')
