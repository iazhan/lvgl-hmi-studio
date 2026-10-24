import re

# ---------- 1) 模型 ----------
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
anchor = '''    /// <summary>右轴下限。</summary>
    public int CurveY2Min { get; set; }'''
add = anchor + '''

    // ── 批次 2c：报警列表的显示细节（契约 alarm_list 的其余 props）──

    /// <summary>报警列表排序（newest_first / newest_last）。</summary>
    public string? AlarmOrder { get; set; }

    /// <summary>确认方式（none / click / dblclick）。</summary>
    public string? AlarmAckOn { get; set; }

    /// <summary>网格样式（all / rows / border / none）。</summary>
    public string? AlarmGrid { get; set; }

    /// <summary>行高（0 = 按控件高度自动算）。</summary>
    public int AlarmRowHeight { get; set; }

    /// <summary>显示滚动条。</summary>
    public bool AlarmScrollbar { get; set; } = true;

    /// <summary>刷新周期毫秒（0 = 不自动刷新）。</summary>
    public int AlarmRefreshMs { get; set; } = 500;

    /// <summary>未确认报警闪烁。</summary>
    public bool AlarmBlink { get; set; } = true;

    /// <summary>闪烁周期毫秒。</summary>
    public int AlarmBlinkMs { get; set; } = 600;

    /// <summary>日期格式（如 yyyy/MM/dd）。</summary>
    public string? AlarmDateFormat { get; set; }

    /// <summary>时间格式（如 hh:mm:ss）。</summary>
    public string? AlarmTimeFormat { get; set; }'''
if 'AlarmOrder' not in s:
    s = s.replace(anchor, add, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 2) 导出 ----------
p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()
a = '''        props["columns"] = cols;
        props["ack_on"] = "click";
        props["blink"] = true;
        props["max_rows"] = Clamp(control.AlarmMaxRows, 10, 500);
        props["row_height"] = Clamp((int)Math.Round(control.Height / Math.Max(1, control.AlarmMaxRows)), 16, 60);'''
b = '''        props["columns"] = cols;
        props["max_rows"] = Clamp(control.AlarmMaxRows, 10, 500);
        // 以下取值域都按运行时校验白名单收紧：写错会让**整页**建不出来
        props["order"] = OneOf(control.AlarmOrder, "newest_first", "newest_last", "newest_first", where, "alarm_list.order");
        props["ack_on"] = OneOf(control.AlarmAckOn, "none", "click", "dblclick", "click", where, "alarm_list.ack_on");
        props["grid"] = OneOf(control.AlarmGrid, "all", "rows", "border", "none", "all", where, "alarm_list.grid");
        props["blink"] = control.AlarmBlink;
        props["blink_ms"] = Clamp(control.AlarmBlinkMs, 100, 5000);
        props["scrollbar"] = control.AlarmScrollbar;
        props["refresh_ms"] = Clamp(control.AlarmRefreshMs, 0, 60000);
        if (!string.IsNullOrWhiteSpace(control.AlarmDateFormat)) props["date_format"] = control.AlarmDateFormat!.Trim();
        if (!string.IsNullOrWhiteSpace(control.AlarmTimeFormat)) props["time_format"] = control.AlarmTimeFormat!.Trim();
        props["row_height"] = control.AlarmRowHeight > 0
            ? Clamp(control.AlarmRowHeight, 16, 60)
            : Clamp((int)Math.Round(control.Height / Math.Max(1, control.AlarmMaxRows)), 16, 60);'''
if 'alarm_list.ack_on' not in s:
    s = s.replace(a, b, 1)

# OneOf 辅助（放在 TabPosOf 之前）
helper = '''    /// <summary>
    /// 取白名单里的一个值：空 = 默认值；不在白名单 = 告警并回落默认。
    /// 运行时的这些枚举是**硬校验**（非法值直接拒绝建页），所以绝不能原样透传。
    /// </summary>
    private string OneOf(string? value, string a, string b, string fallback, string where, string code)
        => OneOf(value, new[] { a, b }, fallback, where, code);

    private string OneOf(string? value, string a, string b, string c, string fallback, string where, string code)
        => OneOf(value, new[] { a, b, c }, fallback, where, code);

    private string OneOf(string? value, string a, string b, string c, string d, string fallback,
        string where, string code)
        => OneOf(value, new[] { a, b, c, d }, fallback, where, code);

    private string OneOf(string? value, string[] allowed, string fallback, string where, string code)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0) return fallback;
        if (allowed.Contains(v)) return v;

        _issues.Warn(code, where, $"{code} 的取值 {value} 非法（{string.Join('/', allowed)}），已按 {fallback} 处理");
        return fallback;
    }

'''
if 'private string OneOf(' not in s:
    s = s.replace('    /// <summary>页签位置：编辑器存 Top/Bottom/Left/Right，契约要小写 top/bottom/left/right。</summary>',
                  helper + '    /// <summary>页签位置：编辑器存 Top/Bottom/Left/Right，契约要小写 top/bottom/left/right。</summary>', 1)
open(p, 'w', encoding='utf-8').write(s)
print('model+export ok')
