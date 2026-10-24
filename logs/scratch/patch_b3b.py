# ---------- 1) 枚举 ----------
p = 'src/HmiEditor.Core/Models/HmiControlType.cs'
s = open(p, encoding='utf-8').read()
a = '    Message,       // 消息条（info/warn/error，可自动消失）'
b = a + ''',
    EnumText,      // 枚举文本（值 → 文字/颜色，映射走绑定层）
    Roller,        // 滚轮选择'''
if 'EnumText' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 2) 模型 ----------
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
a = '''    /// <summary>Message：超宽时滚动。</summary>
    public bool MessageMarquee { get; set; }'''
b = a + '''

    // ── 批次 3：枚举文本 / 滚轮 ──

    /// <summary>EnumText：首次显示的文字（之后由绑定刷新）。</summary>
    public string? EnumTextValue { get; set; }

    /// <summary>
    /// EnumText：值 → 文字/颜色的映射，每行 <c>值:文本[:颜色]</c>。
    /// 注意：运行时从**绑定的 map** 读（<c>bind[].map</c>），不是 <c>props.map</c> ——
    /// 所以这里填了只会导出成告警提示，真正生效要在绑定的 map 里配。
    /// </summary>
    public string? EnumTextMap { get; set; }

    /// <summary>Roller：选项文字（每行一个，最多 32 项）。</summary>
    public string? RollerOptions { get; set; }

    /// <summary>Roller：可见行数（契约要求奇数 3..7）。</summary>
    public int RollerVisibleRows { get; set; } = 3;'''
if 'EnumTextValue' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 3) Lang / 显示名 ----------
p = 'src/HmiEditor.App/Resources/Lang.cs'
s = open(p, encoding='utf-8').read()
a = '    public const string TbMessage = "tb.message";'
b = a + '\n    public const string TbEnumText = "tb.enumtext";\n    public const string TbRoller = "tb.roller";'
if 'TbEnumText' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Services/ControlTypeTextKeys.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Message => AppLocalization.Service[Lang.TbMessage],'
b = a + '\n        HmiControlType.EnumText => AppLocalization.Service[Lang.TbEnumText],\n        HmiControlType.Roller => AppLocalization.Service[Lang.TbRoller],'
if 'HmiControlType.EnumText' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 4) 工具箱 ----------
p = 'src/HmiEditor.App/Views/ToolboxView.axaml.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Message => "M3,8 L21,8 L21,18 L14,18 L10,21 L10,18 L3,18 Z",   // 消息条'
b = a + '''
        HmiControlType.EnumText => "M4,7 L20,7 M4,13 L16,13 M4,19 L12,19",   // 枚举文本：几行字
        HmiControlType.Roller => "M5,5 L19,5 M5,12 L19,12 M5,19 L19,19 M8,8 L8,10 M8,15 L8,17",   // 滚轮：多条+选中线'''
if 'HmiControlType.EnumText =>' not in s:
    s = s.replace(a, b, 1)
a2 = '        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.Message)), Services.ControlTypeTextKeys.Describe(HmiControlType.Message), AppLocalization.Service[Lang.TbCatDisplay], HmiControlType.Message));'
b2 = a2 + '''
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.EnumText)), Services.ControlTypeTextKeys.Describe(HmiControlType.EnumText), AppLocalization.Service[Lang.TbCatDisplay], HmiControlType.EnumText));
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.Roller)), Services.ControlTypeTextKeys.Describe(HmiControlType.Roller), AppLocalization.Service[Lang.TbCatInput], HmiControlType.Roller));'''
if 'ControlType.EnumText)),' not in s:
    s = s.replace(a2, b2, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 5) 画布 + 尺寸 ----------
p = 'src/HmiEditor.App/Controls/DesignCanvas.cs'
s = open(p, encoding='utf-8').read()
a = '''            case HmiControlType.Message:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    $"{control.MessageLevel} · {control.MessageText}");
                return;'''
b = a + '''
            case HmiControlType.EnumText:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    control.EnumTextValue ?? control.Text ?? "enum");
                return;
            case HmiControlType.Roller:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    control.RollerOptions?.Split('\\n').FirstOrDefault() ?? "roller");
                return;'''
if 'HmiControlType.EnumText:' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Controls/DesignCanvas.Editing.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Message => (400.0, 44.0),'
b = a + '\n        HmiControlType.EnumText => (160.0, 32.0),\n        HmiControlType.Roller => (160.0, 120.0),'
if 'HmiControlType.EnumText =>' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 6) 导出 ----------
p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()
a = '            HmiControlType.Message => "message",'
b = a + '\n            HmiControlType.EnumText => "enum_text",\n            HmiControlType.Roller => "roller",'
if 'HmiControlType.EnumText => "enum_text"' not in s:
    s = s.replace(a, b, 1)

a2 = '''            case HmiControlType.Message:'''
b2 = '''            case HmiControlType.EnumText:
                props["text"] = control.EnumTextValue ?? control.Text ?? "";
                SetFont(props, control, DefaultFont(16));
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                props["align"] = Align(control.TextAlign);
                props["valign"] = VAlign(control.TextVAlign);
                // 契约里 props.map 存在，但运行时只读 **bind[].map**（ui_binding.c），
                // 写了也不生效 —— 与其静默丢弃，不如在导出结果里点出来。
                if (!string.IsNullOrWhiteSpace(control.EnumTextMap))
                    _issues.Warn("enum_text.map", where,
                        "枚举映射要配在**绑定的 map** 里：运行时不读 props.map（写了不生效）");
                break;

            case HmiControlType.Roller:
                var opts = (control.RollerOptions ?? "").Replace("\\r", "")
                    .Split('\\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim()).Where(t => t.Length > 0).Take(32).ToArray();
                if (opts.Length == 0)
                {
                    _issues.Error("roller.options", where, "滚轮没有选项（契约要求 options 1..32 项）");
                    return null;
                }
                var optArr = new JsonArray();
                foreach (var t in opts) optArr.Add(t);
                props["options"] = optArr;
                props["visible_rows"] = Odd(Clamp(control.RollerVisibleRows, 3, 7));
                SetFont(props, control, DefaultFont(16));
                _features.Add("widgets_ext");
                break;

            case HmiControlType.Message:'''
if 'case HmiControlType.EnumText:' not in s:
    s = s.replace(a2, b2, 1)

# Odd 辅助
helper = '''    /// <summary>滚轮的可见行数必须是奇数（选中行才在正中）。</summary>
    private static int Odd(int v) => (v & 1) == 1 ? v : v + 1;

'''
if 'private static int Odd(' not in s:
    s = s.replace('    /// <summary>页签位置：编辑器存 Top/Bottom/Left/Right，契约要小写 top/bottom/left/right。</summary>',
                  helper + '    /// <summary>页签位置：编辑器存 Top/Bottom/Left/Right，契约要小写 top/bottom/left/right。</summary>', 1)
open(p, 'w', encoding='utf-8').write(s)
print('b3b ok')
