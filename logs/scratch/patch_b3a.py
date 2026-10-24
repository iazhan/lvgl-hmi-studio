import re

# ---------- 1) 枚举 ----------
p = 'src/HmiEditor.Core/Models/HmiControlType.cs'
s = open(p, encoding='utf-8').read()
a = '    AlarmBar,      // 报警条（单行滚动显示最新一条报警）'
b = a + ''',
    QrCode,        // 二维码（静态内容或绑字符串变量）
    Message,       // 消息条（info/warn/error，可自动消失）'''
if 'QrCode' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 2) 模型字段 ----------
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
a = '''    /// <summary>Pipe：通道底色（契约 channel_color，空 = 用流动色的浅色版）。</summary>
    public string? PipeChannelColor { get; set; }'''
b = a + '''

    // ── 批次 3：二维码 / 消息条 ──

    /// <summary>QrCode：静态内容（绑字符串变量时可为空）。</summary>
    public string? QrText { get; set; }

    /// <summary>QrCode：深色模块颜色（空 = #000000）。</summary>
    public string? QrDarkColor { get; set; }

    /// <summary>QrCode：浅色底色（空 = #FFFFFF）。</summary>
    public string? QrLightColor { get; set; }

    /// <summary>Message：显示的文字（可绑字符串变量）。</summary>
    public string? MessageText { get; set; }

    /// <summary>Message：级别 info / warn / error（决定底色与图标）。</summary>
    public string? MessageLevel { get; set; }

    /// <summary>Message：到点自动清空的毫秒数（0 = 常驻）。</summary>
    public int MessageAutoHideMs { get; set; }

    /// <summary>Message：超宽时滚动。</summary>
    public bool MessageMarquee { get; set; }'''
if 'QrText' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 3) Lang / 显示名 ----------
p = 'src/HmiEditor.App/Resources/Lang.cs'
s = open(p, encoding='utf-8').read()
a = '    public const string TbAlarmBar = "tb.alarmbar";'
b = a + '\n    public const string TbQrCode = "tb.qrcode";\n    public const string TbMessage = "tb.message";'
if 'TbQrCode' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Services/ControlTypeTextKeys.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.AlarmBar => AppLocalization.Service[Lang.TbAlarmBar],'
b = a + '\n        HmiControlType.QrCode => AppLocalization.Service[Lang.TbQrCode],\n        HmiControlType.Message => AppLocalization.Service[Lang.TbMessage],'
if 'HmiControlType.QrCode' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 4) 工具箱 ----------
p = 'src/HmiEditor.App/Views/ToolboxView.axaml.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.AlarmBar => "M3,10 L21,10 M3,14 L17,14 M3,18 L20,18 M3,6 L8,6",   // 报警条：单行滚动'
b = a + '''
        HmiControlType.QrCode => "M4,4 L9,4 L9,9 L4,9 Z M15,4 L20,4 L20,9 L15,9 Z M4,15 L9,15 L9,20 L4,20 Z M13,13 L20,13 L20,20 L13,20",  // 二维码
        HmiControlType.Message => "M3,8 L21,8 L21,18 L14,18 L10,21 L10,18 L3,18 Z",   // 消息条'''
if 'HmiControlType.QrCode =>' not in s:
    s = s.replace(a, b, 1)
a2 = '        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.AlarmBar)), Services.ControlTypeTextKeys.Describe(HmiControlType.AlarmBar), AppLocalization.Service[Lang.TbCatSpecial], HmiControlType.AlarmBar));'
b2 = a2 + '''
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.QrCode)), Services.ControlTypeTextKeys.Describe(HmiControlType.QrCode), AppLocalization.Service[Lang.TbCatDisplay], HmiControlType.QrCode));
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.Message)), Services.ControlTypeTextKeys.Describe(HmiControlType.Message), AppLocalization.Service[Lang.TbCatDisplay], HmiControlType.Message));'''
if 'ControlType.QrCode)),' not in s:
    s = s.replace(a2, b2, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 5) 画布 + 尺寸 ----------
p = 'src/HmiEditor.App/Controls/DesignCanvas.cs'
s = open(p, encoding='utf-8').read()
a = '''            case HmiControlType.AlarmBar:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    $"{control.AlarmGroupFilter} · {control.AlarmOrder}");
                return;'''
b = a + '''
            case HmiControlType.QrCode:
                RenderDataWidgetPlaceholder(context, control, rect, z, "QR");
                return;
            case HmiControlType.Message:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    $"{control.MessageLevel} · {control.MessageText}");
                return;'''
if 'HmiControlType.QrCode:' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Controls/DesignCanvas.Editing.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.AlarmBar => (400.0, 32.0),'
b = a + '\n        HmiControlType.QrCode => (120.0, 120.0),\n        HmiControlType.Message => (400.0, 44.0),'
if 'HmiControlType.QrCode =>' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 6) 导出 ----------
p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()
a = '            HmiControlType.AlarmBar => "alarm_bar",'
b = a + '\n            HmiControlType.QrCode => "qrcode",\n            HmiControlType.Message => "message",'
if 'HmiControlType.QrCode => "qrcode"' not in s:
    s = s.replace(a, b, 1)

a2 = '''            case HmiControlType.AlarmBar:
                MapAlarmBar(props, control, where);
                break;'''
b2 = a2 + '''

            case HmiControlType.QrCode:
                if (!string.IsNullOrWhiteSpace(control.QrText)) props["text"] = control.QrText!.Trim();
                if (_colors.Resolve(control.QrDarkColor ?? "", where) is { } qd) props["dark_color"] = qd;
                if (_colors.Resolve(control.QrLightColor ?? "", where) is { } ql) props["light_color"] = ql;
                _features.Add("widgets_ext");
                break;

            case HmiControlType.Message:
                props["text"] = control.MessageText ?? "";
                props["level"] = OneOf(control.MessageLevel, "info", "warn", "error", "info", where, "message.level");
                props["align"] = Align(control.TextAlign);
                props["marquee"] = control.MessageMarquee;
                if (control.MessageAutoHideMs > 0)
                    props["auto_hide_ms"] = Clamp(control.MessageAutoHideMs, 100, 600_000);
                _features.Add("widgets_ext");
                break;'''
if 'case HmiControlType.QrCode:' not in s:
    s = s.replace(a2, b2, 1)
open(p, 'w', encoding='utf-8').write(s)
print('b3a ok')
