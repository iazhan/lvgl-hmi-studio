p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()

# TargetType
a = '            HmiControlType.RealtimeCurve => "realtime_curve",'
b = a + '\n            HmiControlType.AlarmBar => "alarm_bar",'
if 'AlarmBar => "alarm_bar"' not in s:
    s = s.replace(a, b, 1)

# 分派
a2 = '''            case HmiControlType.RealtimeCurve:
                MapRealtimeCurve(props, control, where);
                break;'''
b2 = a2 + '''

            case HmiControlType.AlarmBar:
                MapAlarmBar(props, control, where);
                break;'''
if 'MapAlarmBar' not in s:
    s = s.replace(a2, b2, 1)

# 实现（放在 MapAlarmList 之后）
anchor = '''    private void MapHistoryCurve(JsonObject props, HmiControl control, string where)'''
impl = '''    /// <summary>
    /// 报警条：单行滚动显示最新一条报警。14 个 props 里运行时全部读；
    /// order 只有 newest_first / newest_last 两个值，其它会**拒绝建页**。
    /// </summary>
    private void MapAlarmBar(JsonObject props, HmiControl control, string where)
    {
        props["source"] = new JsonObject { ["alarm_group"] = control.AlarmGroupFilter ?? "" };
        props["order"] = OneOf(control.AlarmOrder, "newest_first", "newest_last", "newest_first", where, "alarm_bar.order");
        props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
        props["blink"] = control.AlarmBlink;
        props["blink_ms"] = Clamp(control.AlarmBlinkMs, 100, 5000);
        props["only_unacked"] = control.AlarmOnlyUnacked;
        props["scroll_ms"] = Clamp(control.AlarmScrollMs, 500, 60000);
        props["scroll_chars"] = Clamp(control.AlarmScrollChars, 0, 64);
        props["show_date"] = control.AlarmShowDate;
        props["show_time"] = control.AlarmShowTime;
        if (!string.IsNullOrWhiteSpace(control.AlarmNormalText)) props["normal_text"] = control.AlarmNormalText!.Trim();
        if (!string.IsNullOrWhiteSpace(control.AlarmDateFormat)) props["date_format"] = control.AlarmDateFormat!.Trim();
        if (!string.IsNullOrWhiteSpace(control.AlarmTimeFormat)) props["time_format"] = control.AlarmTimeFormat!.Trim();
        SetFont(props, control, DefaultFont(14));
        _features.Add("alarm");
    }

'''
if 'private void MapAlarmBar' not in s:
    s = s.replace(anchor, impl + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('export ok')
