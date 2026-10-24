zh = '''  "tb.qrcode": "二维码",
  "tb.message": "消息条",
  "tb.enumtext": "枚举文本",
  "tb.roller": "滚轮选择",
  "tb.stateimage": "值换图",
  "tb.frameanim": "帧动画",
'''
en = '''  "tb.qrcode": "QR code",
  "tb.message": "Message bar",
  "tb.enumtext": "Enum text",
  "tb.roller": "Roller",
  "tb.stateimage": "State image",
  "tb.frameanim": "Frame animation",
'''
for p, block in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', zh),
                 ('src/HmiEditor.Core/Resources/Strings.en-US.json', en)]:
    s = open(p, encoding='utf-8').read()
    if 'tb.qrcode' in s:
        print('skip', p)
        continue
    anchor = '  "tb.alarmbar"'
    i = s.index(anchor)
    j = s.index('\n', i) + 1
    s = s[:j] + block + s[j:]
    open(p, 'w', encoding='utf-8').write(s)
    print('ok', p)

# 图标差异表 +6
p = 'tests/HmiEditor.App.UiTests/ToolboxIconDictionaryTests.cs'
s = open(p, encoding='utf-8').read()
a = '        [HmiControlType.AlarmBar] = "与 AlarmList 同批：工具箱用内联几何，字典里没有独立镜像键",'
b = a + '''
        [HmiControlType.QrCode] = "批次 3：工具箱用内联几何，字典里没有独立镜像键",
        [HmiControlType.Message] = "同上",
        [HmiControlType.EnumText] = "同上",
        [HmiControlType.Roller] = "同上",
        [HmiControlType.StateImage] = "同上",
        [HmiControlType.FrameAnim] = "同上",'''
if 'HmiControlType.QrCode]' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ToolboxView 引用 31 -> 37
p = 'tests/HmiEditor.App.UiTests/ControlTypeDisplayTests.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('("Views/ToolboxView.axaml.cs", 31)', '("Views/ToolboxView.axaml.cs", 37)')
s = s.replace('期望 31 处', '期望 37 处')
open(p, 'w', encoding='utf-8').write(s)
print('guards ok')
