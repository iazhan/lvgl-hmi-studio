import re

# 1) 枚举
p = 'src/HmiEditor.Core/Models/HmiControlType.cs'
s = open(p, encoding='utf-8').read()
a = '    HistoryCurve // 历史曲线（数据来自 trends.json / savecfg.json 的存盘组）'
b = a + ',\n    RealtimeCurve // 实时曲线（数据来自 sim 采样，按 sample_ms 滚动）'
if 'RealtimeCurve' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 2) Lang
p = 'src/HmiEditor.App/Resources/Lang.cs'
s = open(p, encoding='utf-8').read()
a = '    public const string TbHistoryCurve = "tb.historycurve";'
b = a + '\n    public const string TbRealtimeCurve = "tb.realtimecurve";'
if 'TbRealtimeCurve' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 3) 显示名
p = 'src/HmiEditor.App/Services/ControlTypeTextKeys.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.HistoryCurve => AppLocalization.Service[Lang.TbHistoryCurve],'
b = a + '\n        HmiControlType.RealtimeCurve => AppLocalization.Service[Lang.TbRealtimeCurve],'
if 'RealtimeCurve' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 4) 工具箱：图标 + 条目
p = 'src/HmiEditor.App/Views/ToolboxView.axaml.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.HistoryCurve => "M4,19 L4,5 M4,19 L21,19 M5,16 L9,10 L13,14 L19,7",    // 曲线：坐标轴+折线'
b = a + '\n        HmiControlType.RealtimeCurve => "M4,19 L4,5 M4,19 L21,19 M5,15 L9,9 L13,13 L19,6 M20,4 L20,8 M18,6 L22,6",  // 实时曲线：折线+时钟'
if 'RealtimeCurve =>' not in s:
    s = s.replace(a, b, 1)
a2 = '        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.HistoryCurve)), Services.ControlTypeTextKeys.Describe(HmiControlType.HistoryCurve), AppLocalization.Service[Lang.TbCatChart], HmiControlType.HistoryCurve));'
b2 = a2 + '\n        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.RealtimeCurve)), Services.ControlTypeTextKeys.Describe(HmiControlType.RealtimeCurve), AppLocalization.Service[Lang.TbCatChart], HmiControlType.RealtimeCurve));'
if 'ControlType.RealtimeCurve)),' not in s:
    s = s.replace(a2, b2, 1)
open(p, 'w', encoding='utf-8').write(s)

# 5) 画布占位
p = 'src/HmiEditor.App/Controls/DesignCanvas.cs'
s = open(p, encoding='utf-8').read()
a = '''            case HmiControlType.HistoryCurve:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    $"{control.TrendGroup} · {control.SpanMin}min");
                return;'''
b = a + '''
            case HmiControlType.RealtimeCurve:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    $"{control.SampleMs}ms · {control.SpanMin}min");
                return;'''
if 'RealtimeCurve' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 6) 默认尺寸
p = 'src/HmiEditor.App/Controls/DesignCanvas.Editing.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.HistoryCurve => (400.0, 180.0),'
b = a + '\n        HmiControlType.RealtimeCurve => (400.0, 180.0),'
if 'RealtimeCurve' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# 7) 模型：采样周期 / 历史秒数
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
a = '''    /// <summary>报警列表排序（newest_first / newest_last）。</summary>
    public string? AlarmOrder { get; set; }'''
b = '''    /// <summary>实时曲线：采样周期毫秒（契约 sample_ms，默认 1000）。</summary>
    public int SampleMs { get; set; } = 1000;

    /// <summary>实时曲线：内存里保留多少秒历史（契约 history_s，0 = 只保留窗口内的）。</summary>
    public int HistoryS { get; set; }

''' + a
if 'SampleMs' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)
print('part1 ok')
