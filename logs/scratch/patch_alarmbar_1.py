# 1) 枚举
p = 'src/HmiEditor.Core/Models/HmiControlType.cs'
s = open(p, encoding='utf-8').read()
a = '    RealtimeCurve, // 实时曲线（数据来自 sim 采样，按 sample_ms 滚动）'
b = a + '\n    AlarmBar,      // 报警条（单行滚动显示最新一条报警）'
if 'AlarmBar' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 2) Lang + 显示名
p = 'src/HmiEditor.App/Resources/Lang.cs'
s = open(p, encoding='utf-8').read()
a = '    public const string TbRealtimeCurve = "tb.realtimecurve";'
b = a + '\n    public const string TbAlarmBar = "tb.alarmbar";'
if 'TbAlarmBar' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Services/ControlTypeTextKeys.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.RealtimeCurve => AppLocalization.Service[Lang.TbRealtimeCurve],'
b = a + '\n        HmiControlType.AlarmBar => AppLocalization.Service[Lang.TbAlarmBar],'
if 'AlarmBar' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 3) 工具箱：图标 + 条目
p = 'src/HmiEditor.App/Views/ToolboxView.axaml.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.RealtimeCurve => "M4,19 L4,5 M4,19 L21,19 M5,15 L9,9 L13,13 L19,6 M20,4 L20,8 M18,6 L22,6",  // 实时曲线：折线+时钟'
b = a + '\n        HmiControlType.AlarmBar => "M3,10 L21,10 M3,14 L17,14 M3,18 L20,18 M3,6 L8,6",   // 报警条：单行滚动'
if 'HmiControlType.AlarmBar =>' not in s:
    s = s.replace(a, b, 1)
a2 = '        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.RealtimeCurve)), Services.ControlTypeTextKeys.Describe(HmiControlType.RealtimeCurve), AppLocalization.Service[Lang.TbCatChart], HmiControlType.RealtimeCurve));'
b2 = a2 + '\n        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.AlarmBar)), Services.ControlTypeTextKeys.Describe(HmiControlType.AlarmBar), AppLocalization.Service[Lang.TbCatSpecial], HmiControlType.AlarmBar));'
if 'ControlType.AlarmBar)),' not in s:
    s = s.replace(a2, b2, 1)
open(p, 'w', encoding='utf-8').write(s)

# 4) 画布占位 + 默认尺寸
p = 'src/HmiEditor.App/Controls/DesignCanvas.cs'
s = open(p, encoding='utf-8').read()
a = '''            case HmiControlType.RealtimeCurve:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    $"{control.SampleMs}ms · {control.SpanMin}min");
                return;'''
b = a + '''
            case HmiControlType.AlarmBar:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    $"{control.AlarmGroupFilter} · {control.AlarmOrder}");
                return;'''
if 'AlarmBar' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Controls/DesignCanvas.Editing.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.RealtimeCurve => (400.0, 180.0),'
b = a + '\n        HmiControlType.AlarmBar => (400.0, 32.0),'
if 'AlarmBar' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 5) 模型：报警条专属字段
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
a = '''    /// <summary>实时曲线：采样周期毫秒（契约 sample_ms，默认 1000）。</summary>
    public int SampleMs { get; set; } = 1000;'''
b = '''    /// <summary>报警条：没有报警时显示的文字（契约 normal_text）。</summary>
    public string? AlarmNormalText { get; set; }

    /// <summary>报警条：只显示未确认的报警。</summary>
    public bool AlarmOnlyUnacked { get; set; } = true;

    /// <summary>报警条：滚动一条的间隔毫秒（契约 scroll_ms，默认 3000）。</summary>
    public int AlarmScrollMs { get; set; } = 3000;

    /// <summary>报警条：滚动步长字符数（0 = 不滚动，直接截断）。</summary>
    public int AlarmScrollChars { get; set; }

    /// <summary>报警条：显示日期。</summary>
    public bool AlarmShowDate { get; set; }

    /// <summary>报警条：显示时间。</summary>
    public bool AlarmShowTime { get; set; }

''' + a
if 'AlarmNormalText' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)
print('part1 ok')
