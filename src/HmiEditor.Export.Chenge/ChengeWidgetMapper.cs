using System.Globalization;
using System.Text.Json.Nodes;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 一个编辑器控件 → 一个 37 页面控件。
///
/// <para><b>只写 schema 里有的键</b>：构造完 props 后统一过 <see cref="ChengeSchema.AllowedProps"/>，
/// 不在白名单的键被摘掉并记 warning。这条是导出器唯一的"契约防线"——运行时对未知属性
/// 是**整页拒绝**（`schema_check_props`），静默丢掉比整页黑屏好，但也不能不说。</para>
/// </summary>
internal sealed class ChengeWidgetMapper
{
    private readonly ChengeSchema _schema;
    private readonly ChengeIssueBag _issues;
    private readonly ChengeColors _colors;
    private readonly ChengeAssets _assets;
    private readonly ChengeMaps _maps;
    private readonly HashSet<string> _features;
    private readonly int _screenWidth;
    private readonly int _screenHeight;
    private readonly Dictionary<string, string> _widgetIds;

    /// <summary>
    /// 键盘控件 → 目标控件 Id。键盘可能排在目标前面导出（画布顺序即控件顺序），
    /// 那时目标还没有 widget id，只能建完本页再回填（<see cref="ApplyPendingKeyboardTargets"/>）。
    /// </summary>
    private readonly List<(JsonObject Props, string TargetControlId, string Where)> _pendingKeyboardTargets = new();

    public ChengeWidgetMapper(ChengeSchema schema, ChengeIssueBag issues, ChengeColors colors,
        ChengeAssets assets, ChengeMaps maps, HashSet<string> features, int screenWidth, int screenHeight,
        Dictionary<string, string> widgetIds)
    {
        _schema = schema;
        _issues = issues;
        _colors = colors;
        _assets = assets;
        _maps = maps;
        _features = features;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _widgetIds = widgetIds;
    }

    /// <summary>
    /// 回填本页所有"目标排在键盘之后"的 target。必须在整页控件都 Map 完之后调用。
    /// 目标不存在时报 Error（运行时 link_keyboards 找不到 target 会拒绝整页）。
    /// </summary>
    public void ApplyPendingKeyboardTargets()
    {
        if (_pendingKeyboardTargets.Count == 0) return;

        foreach (var (props, targetControlId, where) in _pendingKeyboardTargets)
        {
            if (_widgetIds.TryGetValue(targetControlId, out var mapped))
                props["target"] = mapped;
            else
                _issues.Error("keyboard.target_missing", where,
                    $"键盘的目标控件 {targetControlId} 不在本页：运行时找不到 target 会拒绝整页");
        }
        _pendingKeyboardTargets.Clear();
    }

    public JsonObject? Map(HmiControl control, string pageId, string widgetId)    {
        var where = $"pages/{pageId}.json#{widgetId}";
        // 设计画布的几何补丁按导出后的 widget id 寻址（顶层与子控件都从这里过）
        if (!string.IsNullOrEmpty(control.Id)) _widgetIds[control.Id] = widgetId;
        var target = TargetType(control.Type, where);
        if (target == null) return null;

        var props = new JsonObject();
        var kind = string.Empty;
        switch (control.Type)
        {
            case HmiControlType.Label:
                SetText(props, control, where);
                SetFont(props, control, DefaultFont(16));
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                props["align"] = Align(control.TextAlign);
                props["valign"] = VAlign(control.TextVAlign);
                // 字形/字距/行距：normal 与 0 都是默认值，不写（保持产物最小）
                if (!string.IsNullOrWhiteSpace(control.LabelFontStyle) &&
                    !string.Equals(control.LabelFontStyle, "normal", StringComparison.OrdinalIgnoreCase))
                    props["font_style"] = control.LabelFontStyle!.Trim().ToLowerInvariant();
                if (control.LabelLetterSpacing > 0) props["letter_spacing"] = Clamp(control.LabelLetterSpacing, 1, 32);
                if (control.LabelLineSpacing > 0) props["line_spacing"] = Clamp(control.LabelLineSpacing, 1, 64);
                // 超长文字的排法（契约 label.long_mode）：面板那五档一一对应运行时的
                // LV_LABEL_LONG_*。wrap 是运行时缺省，不写。
                if (!string.IsNullOrWhiteSpace(control.LabelLongMode) &&
                    LongModeOf(control.LabelLongMode) is { } longMode)
                    props["long_mode"] = longMode;
                // 底色/描边/圆角：运行时 apply_box_style 会读（默认透明，配了才画）。
                // 不导出的话，编辑器里设的底色只在 C# 画布可见，位图里永远是透明。
                AddCommonBox(props, control, where);
                break;

            case HmiControlType.Value:
                props["decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                // 单位 / 前缀 / 后缀是三样东西，各有各的键：运行时的拼法是
                // 「前缀 + 数值 +（空格）单位 + 后缀」。以前把「后缀」当单位兜底，
                // 面板上填「%」得到的是「60 %」而不是「60%」。
                if (!string.IsNullOrWhiteSpace(control.Unit)) props["unit"] = control.Unit.Trim();
                if (!string.IsNullOrWhiteSpace(control.ValuePrefix))
                    props["prefix"] = control.ValuePrefix.Trim();
                if (!string.IsNullOrWhiteSpace(control.ValueSuffix))
                    props["suffix"] = control.ValueSuffix.Trim();
                props["align"] = Align(control.TextAlign);
                // 垂直对齐（widgets_common.c apply_text_valign 读 valign）：契约 value.valign
                // 一直开着，但这里漏写 —— 面板上改垂直对齐在板子上没反应。
                props["valign"] = VAlign(control.TextVAlign);
                SetFont(props, control, DefaultFont(20));
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                break;

            case HmiControlType.Button:
                props["text"] = control.Text ?? "";
                SetFont(props, control, DefaultFont(16));
                props["color"] = _colors.ResolveOr(control.TextColor, "#FFFFFF", where);
                AddCommonBox(props, control, where);
                AddPressed(props, control, where);
                AddButtonBehavior(props, control, where);
                break;

            case HmiControlType.Switch:
                props["color_on"] = _colors.ResolveOr(
                    string.IsNullOrWhiteSpace(control.SwitchColorOn) ? control.IndicatorColor : control.SwitchColorOn,
                    _colors.Success, where);
                props["color_off"] = _colors.ResolveOr(
                    string.IsNullOrWhiteSpace(control.SwitchColorOff) ? control.BgColor : control.SwitchColorOff,
                    _colors.Border, where);
                props["text_on"] = control.Text ?? "";
                props["text_off"] = control.SwitchTextOff ?? "";
                if (!string.IsNullOrWhiteSpace(control.SwitchKnobColor) &&
                    _colors.Resolve(control.SwitchKnobColor, where) is { } knob)
                    props["knob_color"] = knob;
                break;

            case HmiControlType.CheckBox:
                props["text"] = control.Text ?? "";
                SetFont(props, control, DefaultFont(16));
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                props["checked"] = control.IsChecked;
                // 勾选框颜色（运行时 build_checkbox 读 box_color）；空 = 不写，用运行时缺省
                if (_colors.Resolve(control.BoxColor, where) is { } boxColor) props["box_color"] = boxColor;
                break;

            case HmiControlType.Led:
                props["color"] = _colors.ResolveOr(control.IndicatorColor ?? control.TextColor, _colors.Success, where);
                // size 0 = 按控件宽高取小者（契约语义），所以 0 不写
                props["size"] = control.LampSize > 0
                    ? Clamp(control.LampSize, 4, 400)
                    : (int)Math.Round(Math.Min(control.Width, control.Height));
                // 面板的「亮度」必须写出去：缺键时运行时按 255 画，面板显示 128 就对不上
                props["brightness"] = Clamp(control.Brightness, 0, 255);
                if (ParseRanges(control.LampRanges, where) is { } lampRanges) props["ranges"] = lampRanges;
                break;

            case HmiControlType.Bar:
                RequireRange(control, where, "bar.range");
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["direction"] = string.Equals(control.BarDirection, "Vertical", StringComparison.OrdinalIgnoreCase)
                    ? "v" : "h";
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["show_percent"] = control.BarShowPercent;
                if (ParseRanges(control.BarRanges, where) is { } barRanges) props["ranges"] = barRanges;
                break;

            case HmiControlType.Slider:
                RequireRange(control, where, "slider.range");
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["step"] = control.StepValue;
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                break;

            case HmiControlType.Gauge:
                RequireRange(control, where, "gauge.range");
                props["style"] = control.ShowPointer == true ? "pointer" : "arc";
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["ticks"] = control.ShowTicks ? Clamp(control.MajorTickCount, 2, 11) : 0;
                props["decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                props["show_value"] = control.GaugeShowValue;
                // 两端数值位置（运行时 build_gauge 读 label_pos，缺省 inside）
                if (!string.IsNullOrWhiteSpace(control.GaugeLabelPos) &&
                    !string.Equals(control.GaugeLabelPos.Trim(), "inside", StringComparison.OrdinalIgnoreCase))
                    props["label_pos"] = OneOf(control.GaugeLabelPos, "outside", "none", "inside", where, "gauge.label_pos");
                // 下面几项 0 都表示"按控件尺寸自动"，所以 0 不写，交给运行时默认
                if (control.GaugeArcWidth > 0) props["arc_width"] = Clamp(control.GaugeArcWidth, 1, 64);
                if (control.GaugeTickLen > 0) props["tick_len"] = Clamp(control.GaugeTickLen, 1, 32);
                if (control.GaugeNeedleWidth > 0) props["needle_width"] = Clamp(control.GaugeNeedleWidth, 1, 32);
                if (control.GaugeCenterRadius > 0) props["center_radius"] = Clamp(control.GaugeCenterRadius, 0, 64);
                if (control.GaugeNeedleLen > 0)
                    props["needle_len"] = Math.Round(Math.Clamp(control.GaugeNeedleLen, 0.2, 1.0), 2);
                if (ParseRanges(control.GaugeRanges, where) is { } gaugeRanges) props["ranges"] = gaugeRanges;
                break;

            case HmiControlType.Knob:
                RequireRange(control, where, "knob.range");
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["step"] = control.StepValue;
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                props["show_value"] = control.KnobShowValue;
                if (control.KnobArcWidth is > 0 and not 10) props["arc_width"] = Clamp(control.KnobArcWidth, 1, 32);
                break;

            case HmiControlType.Dropdown:
                var options = (control.Options ?? "").Replace("\r", "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();
                if (options.Length == 0)
                {
                    // 必须 return null：没有 options 的 combobox 写出去，运行时会以
                    // "options 不是数组" 拒绝**整页**（build_combobox 里 hmi_json_type 判失败即 -1）
                    _issues.Error("combobox.no_options", where,
                        "下拉框没有任何选项（37 §8：options 与 options_tag 至少要有一个）");
                    return null;
                }
                var optionArr = new JsonArray();
                foreach (var o in options) optionArr.Add(o);
                props["options"] = optionArr;
                SetFont(props, control, DefaultFont(16));
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                // 初始选中项（契约 combobox.selected）：0 是缺省，不写；越界钳到选项数
                if (control.SelectedIndex > 0)
                    props["selected"] = Clamp(control.SelectedIndex, 0, options.Length - 1);
                break;

            case HmiControlType.TextArea:
                // 运行时 input_click_cb 的第一句是 `if (bind_tag < 0) return;` —— 没绑定变量的
                // 输入框点了**不会弹键盘，也不报错**。导出期必须先说清楚，否则用户只能猜。
                // 编辑器里输入框绑的是 Text（契约 bind 名也叫 text），Value 只是兼容写法。
                if (!control.VariableBindings.Any(b => b.PropertyName is "Text" or "text" or "Value" or "value"))
                {
                    _issues.Warn("input.no_bind", where,
                        "输入框没有绑定变量：运行时点了不会弹键盘（input 靠 bind.text 提交值）");
                }
                // num/text 由**绑定变量的类型**决定，不是由用户填的哪个属性名决定：
                // 绑 f32 却导成 text 模式，运行时就会弹文本键盘、提交时再转数值。
                var inputNumeric = !control.PasswordMode && IsNumericBinding(control);
                props["mode"] = control.PasswordMode ? "password" : inputNumeric ? "num" : "text";
                // 量程/小数位只对数值型输入有意义：文本/密码输入（绑 s16/str 变量）提交的是
                // 字符串，运行时也不解析数值。写进去只会让"输入 ABC"看起来受量程限制。
                if (inputNumeric)
                {
                    props["min"] = (int)Math.Round(control.MinValue);
                    props["max"] = (int)Math.Round(control.MaxValue);
                    props["decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                }
                // 契约 input.max_len 缺省 12、上限 24（ui_keypad.c 把请求钳到 1..24）。
                // 0 在模型里是"不限"，导出成 24 = 键盘能给到的上限。
                props["max_len"] = control.MaxLength > 0 ? Clamp(control.MaxLength, 1, 24) : 24;
                SetFont(props, control, DefaultFont(16));
                props["align"] = Align(control.TextAlign);
                // 写回时机：ok 是默认值不写；blur = 失焦即写
                if (!string.IsNullOrWhiteSpace(control.InputCommit) &&
                    !string.Equals(control.InputCommit, "ok", StringComparison.OrdinalIgnoreCase))
                    props["commit"] = control.InputCommit!.Trim().ToLowerInvariant();
                break;

            case HmiControlType.Image:
                var src = _assets.SrcFor(control.ImageAssetId, where);
                if (src == null)
                {
                    _issues.Error("image.no_src", where, "图片控件没有可导出的图片资产（运行时只认 assets/img/*.bin）");
                    return null;
                }
                props["src"] = src;
                props["scale"] = control.StretchMode == "None" ? "actual" : "fit";
                break;

            case HmiControlType.Table:
                MapTable(props, control, pageId, widgetId, where);
                break;

            case HmiControlType.TabView:
                var tabs = (control.TabTitles ?? "").Split(new[] { ',', '，', '\n' },
                    StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (tabs.Length == 0)
                {
                    _issues.Warn("tabview.no_tabs", where, "页签控件没有页签名，已跳过");
                    return null;
                }
                var tabArr = new JsonArray();
                foreach (var t in tabs) tabArr.Add(new JsonObject { ["text"] = t });
                props["tabs"] = tabArr;
                props["tab_pos"] = TabPosOf(control.TabPos);
                if (control.TabHeight is > 0 and not 40) props["tab_h"] = Clamp(control.TabHeight, 16, 200);
                break;

            case HmiControlType.Spinner:
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["speed_ms"] = Clamp(control.SpinSpeed, 100, 5000);
                if (control.SpinnerArcLen is > 0 and not 200) props["arc_len"] = Clamp(control.SpinnerArcLen, 10, 360);
                if (control.SpinnerArcWidth is > 0 and not 4) props["arc_width"] = Clamp(control.SpinnerArcWidth, 1, 32);
                break;

            case HmiControlType.Keyboard:
                // 运行时 build_keyboard + link_keyboards 完整实现了"常驻内联键盘"：
                // props.target 必填，键位按 target 的绑定类型自动选数字/文本。
                // 契约里还有 title，但运行时根本没读它（build_keyboard 直接 (void)props），
                // 所以只写 target，别伪造一个改了没反应的键。
                if (string.IsNullOrWhiteSpace(control.KeyboardTarget))
                {
                    _issues.Error("keyboard.no_target", where,
                        "键盘控件要指定目标输入控件（运行时缺 props.target 会拒绝整页）");
                    return null;
                }
                {
                    var targetControlId = control.KeyboardTarget!.Trim();
                    if (_widgetIds.TryGetValue(targetControlId, out var mappedTarget))
                        props["target"] = mappedTarget;
                    else
                        _pendingKeyboardTargets.Add((props, targetControlId, where));
                }
                _features.Add("widgets_ext");
                break;

            case HmiControlType.Rectangle:
                kind = control.Radius > 0 ? "round_rect" : "rect";
                MapShapeCommon(props, control, where);
                break;

            case HmiControlType.Ellipse:
                kind = Math.Abs(control.Width - control.Height) < 1 ? "circle" : "ellipse";
                MapShapeCommon(props, control, where);
                break;

            case HmiControlType.Line:
                kind = "line";
                props["fill_color"] = _colors.ResolveOr(control.BgColor ?? control.BorderColor, _colors.Border, where);
                // 按点画出来的直线带两端点：运行时按 points 画真线段；没有点则仍是窄矩形（兼容旧工程）。
                if (control.Points is { Count: >= 2 })
                {
                    props["points"] = PointsOf(control, where);
                    props["border_color"] = _colors.ResolveOr(control.BorderColor ?? control.BgColor, _colors.Border, where);
                    props["border_width"] = Math.Max(1, control.BorderWidth);
                }
                break;

            case HmiControlType.Polyline:
                kind = "polyline";
                props["points"] = PointsOf(control, where);
                props["border_color"] = _colors.ResolveOr(control.BgColor ?? control.BorderColor, _colors.Border, where);
                props["border_width"] = Math.Max(1, control.BorderWidth);
                break;

            case HmiControlType.Arc:
                kind = "arc";
                // 几何角度与进度量程是两件事：角度走 ArcAngleStart/End（缺省 0/360），
                // 量程走 MinValue/MaxValue（缺省 0/100，由 MapFigureExtras 写 min/max）。
                // 早先两者共用 MinValue/MaxValue —— 改量程会把弧的形状一起改掉。
                props["angle_start"] = Clamp(control.ArcAngleStart ?? 0, -360, 720);
                props["angle_end"] = Clamp(control.ArcAngleEnd ?? 360, -360, 720);
                props["border_color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["border_width"] = Math.Max(1, control.BorderWidth);
                break;

            case HmiControlType.Tank:
                kind = "tank";
                MapShapeCommon(props, control, where);
                break;

            case HmiControlType.Pipe:
                props["direction"] = string.Equals(control.PipeDirection, "Vertical", StringComparison.OrdinalIgnoreCase)
                    ? "v" : "h";
                // 流动方向（契约 pipe.flow_dir）：forward 是缺省，不写
                if (string.Equals(control.PipeFlowDirection, "Reverse", StringComparison.OrdinalIgnoreCase))
                    props["flow_dir"] = "reverse";
                props["wall"] = Math.Round(Math.Clamp(control.PipeWall is >= 0.05 and <= 0.45 ? control.PipeWall : 0.2, 0.05, 0.45), 3);
                // 流动色 = 面板「指示器颜色」，**不再回退到背景色**：
                // 管道是自己画几何的控件，背景色对它没有意义；以前两处共用一条回退链，
                // 用户一改背景色，管道的流动色和由它派生的通道色就跟着一起变了。
                props["color"] = _colors.ResolveOr(control.IndicatorColor, "#4EA1FF", where);
                if (!string.IsNullOrWhiteSpace(control.PipeChannelColor) &&
                    _colors.Resolve(control.PipeChannelColor, where) is { } chan)
                    props["channel_color"] = chan;
                // 管壁颜色（运行时 build_pipe 读 wall_color）；空 = 运行时缺省
                if (!string.IsNullOrWhiteSpace(control.PipeWallColor) &&
                    _colors.Resolve(control.PipeWallColor, where) is { } pipeWall)
                    props["wall_color"] = pipeWall;
                // flow_ms 是"一个块走完全程"的毫秒数；0 = 静态（契约下限 40，用 0 表示不流动）
                props["flow_ms"] = control.PipeFlowSpeed > 0
                    ? Clamp(control.PipeFlowSpeed, 40, 2000)
                    : 0;
                // 多点管道：整条路径（直管两点、弯管三点、多段 N 点）都由 points 给出，
                // 流向动画沿这条路径按弧长走，拐点处不重置相位（运行时 pipe 控件的 path 模式）。
                if (control.Points is { Count: >= 2 })
                {
                    props["points"] = PointsOf(control, where);
                    // 管径只对"按点画"的管道有意义：路径的包围盒里没有粗细信息。
                    // 没有 points 的老工程仍按 direction 画，框的短边就是管径 —— 不写这个键。
                    props["width"] = Clamp((int)Math.Round(control.PipeWidth), 4, 200);
                }
                _features.Add("pipe");      // 运行时要求 manifest.features 里声明（37 §8）
                break;

            case HmiControlType.Valve:
                // 契约 figures 有 valve_ball / valve_butterfly / valve_gate，运行时的
                // shape_gen_valve 三个分支都实现了；编辑器的 ValveType 只决定用哪个 kind。
                // 开度走运行时缺省（0.5 半开），编辑器没有对应属性，所以不写 open。
                kind = ValveKindOf(control.ValveType, where);
                MapShapeCommon(props, control, where);
                break;

            case HmiControlType.Svg:
                _issues.Error("svg.not_supported", where,
                    "SVG 控件暂不支持导出：运行时没有 SVG 解析器，多图元展平成一次性的 polygon 会丢语义（见 MAPPING-REPORT 的未支持清单）");
                return null;

            case HmiControlType.AlarmList:
                MapAlarmList(props, control, where);
                break;

            case HmiControlType.HistoryCurve:
                if (!MapHistoryCurve(props, control, where)) return null;
                break;

            case HmiControlType.RealtimeCurve:
                if (!MapRealtimeCurve(props, control, where)) return null;
                break;

            case HmiControlType.AlarmBar:
                MapAlarmBar(props, control, where);
                break;

            case HmiControlType.QrCode:
                if (!string.IsNullOrWhiteSpace(control.QrText)) props["text"] = control.QrText!.Trim();
                if (_colors.Resolve(control.QrDarkColor ?? "", where) is { } qd) props["dark_color"] = qd;
                if (_colors.Resolve(control.QrLightColor ?? "", where) is { } ql) props["light_color"] = ql;
                _features.Add("widgets_ext");
                break;

            case HmiControlType.EnumText:
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
                var opts = (control.RollerOptions ?? "").Replace("\r", "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
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

            case HmiControlType.StateImage:
                var states = ParseAssetPairs(control.StateImages, where, "state_image");
                if (states.Count == 0)
                {
                    _issues.Error("state_image.images", where, "值换图至少要有一项（契约 images 1..16 项）");
                    return null;
                }
                props["images"] = states;
                _features.Add("widgets_ext");
                break;

            case HmiControlType.FrameAnim:
                var frames = new JsonArray();
                foreach (var assetId in SplitLines(control.FrameImages).Take(60))
                {
                    var frameSrc = _assets.SrcFor(assetId, where);
                    if (frameSrc == null)
                    {
                        _issues.Warn("frame_anim.frame", where, $"帧动画的图片 {assetId} 导不出来，已跳过这一帧");
                        continue;
                    }
                    frames.Add(frameSrc);
                }
                // 运行时要求 2..32 帧（build_frame_anim 里 n < 2 直接 -1），只挡 0 帧
                // 会让"配了一帧"的工程在板上整页建不起来
                if (frames.Count < 2)
                {
                    _issues.Error("frame_anim.frames", where,
                        "帧动画至少要 2 帧可用图片（运行时对少于 2 帧拒绝整页；先导入图片资产）");
                    return null;
                }
                props["frames"] = frames;
                props["interval_ms"] = Clamp(control.FrameIntervalMs, 20, 5000);
                props["loop"] = control.FrameLoop;
                _features.Add("widgets_ext");
                break;

            case HmiControlType.Shape:
                // 40 个 kind 任一非法都拒绝整页，所以先过白名单（未知值回落 rect 并告警）
                kind = OneOf(control.ShapeKind, ShapeKinds, "rect", where, "shape.kind");
                MapShapeCommon(props, control, where);
                if (kind is "polygon" or "polyline")
                    props["points"] = PointsOf(control, where);
                else if (kind == "line" && control.Points is { Count: >= 2 })
                    props["points"] = PointsOf(control, where);
                // 弧/扇的两个端点就是起止角：与 Arc 控件共用 ArcAngleStart/ArcAngleEnd，
                // 否则从「通用图元」进来画的弧导出后是缺省 0..360（整圈）。
                // 量程 min/max 另走 MinValue/MaxValue（MapFigureExtras）。
                if (kind is "arc" or "pie")
                {
                    props["angle_start"] = Clamp(control.ArcAngleStart ?? 0, -360, 720);
                    props["angle_end"] = Clamp(control.ArcAngleEnd ?? 360, -360, 720);
                }
                // 容器：子控件递归导进 props.children（坐标相对容器，运行时 flatten_widgets 展开）
                if (string.Equals(kind, "container", StringComparison.Ordinal))
                    MapContainerChildren(props, control, pageId, widgetId);
                break;

            case HmiControlType.Chart:
                props["kind"] = OneOf(control.ChartKind, "bar", "pie", "donut", "bar", where, "chart.kind");
                var series = ChartSeriesOf(control, where);
                if (series.Count == 0)
                {
                    /* 与曲线同一口径：占位序列（expr="0"）让页面能建起来，不然运行时会
                     * 以"series 必须 1..8 项"拒绝整页，画布直接白。 */
                    series.Add(new JsonObject
                    {
                        ["label"] = "—",
                        ["value"] = new JsonObject { ["expr"] = "0" },
                        ["color"] = _colors.Primary,
                    });
                    _issues.Warn("chart.series", where,
                        "图表没有绑定变量：先按 0 画一条占位序列（在系列里填 变量Id:标签 后自动替换）");
                }
                props["series"] = series;
                props["show_legend"] = control.ChartShowLegend;
                props["show_value"] = control.ChartShowValue;
                RequireRange(control, where, "chart.range");
                props["y_min"] = (int)Math.Round(control.MinValue);
                props["y_max"] = (int)Math.Round(control.MaxValue);
                props["y_decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                _features.Add("widgets_ext");
                break;

            case HmiControlType.XyCurve:
                var tagX = ResolveChartTag(control.XyTagX, where, "xy_curve.x");
                var tagY = ResolveChartTag(control.XyTagY, where, "xy_curve.y");
                if (tagX == null || tagY == null)
                {
                    /* 同曲线：两轴都缺时给占位（运行时要 x/y 各一个 {tag|expr}，缺了拒绝整页）。
                     * 只缺一个也走占位 —— 半张图纸比白屏好排查。 */
                    props["x"] = new JsonObject { ["expr"] = "0" };
                    props["y"] = new JsonObject { ["expr"] = "0" };
                    _issues.Warn("xy_curve.tags", where,
                        "X-Y 曲线没有指定 X/Y 变量：先按原点画占位（两个都填上变量后自动替换）");
                }
                else
                {
                    props["x"] = new JsonObject { ["tag"] = tagX.Value };
                    props["y"] = new JsonObject { ["tag"] = tagY.Value };
                }
                props["dot"] = control.XyDot;
                props["line"] = control.XyLine;
                props["sample_ms"] = Clamp(control.SampleMs is >= 100 and <= 60000 ? control.SampleMs : 1000, 100, 60000);
                props["span_s"] = SpanSeconds(control, where);
                RequireRange(control, where, "xy_curve.range");
                // X/Y 各自一对量程：模型里 X 用 XMinValue/XMaxValue，Y 用 MinValue/MaxValue。
                // 早先四个键都取同一对字段，X 轴永远等于 Y 轴。
                props["x_min"] = (int)Math.Round(control.XMinValue);
                props["x_max"] = (int)Math.Round(control.XMaxValue);
                props["y_min"] = (int)Math.Round(control.MinValue);
                props["y_max"] = (int)Math.Round(control.MaxValue);
                props["x_div"] = Clamp(control.CurveXDiv, CurveDivMin, CurveDivMax);
                props["y_div"] = Clamp(control.CurveYDiv, CurveDivMin, CurveDivMax);
                props["x_minor"] = Clamp(control.CurveXMinor, CurveMinorMin, CurveMinorMax);
                props["y_minor"] = Clamp(control.CurveYMinor, CurveMinorMin, CurveMinorMax);
                props["x_decimals"] = Clamp(control.CurveXDecimals, 0, 6);
                props["y_decimals"] = Clamp(control.CurveYDecimals, 0, 6);
                props["grid"] = control.CurveGrid;
                if (_colors.ResolveOr(control.CurveAxisColor, "#4A5A6A", where) is { } xa) props["axis_color"] = xa;
                if (_colors.ResolveOr(control.CurveGridColor, "#2F3D4C", where) is { } xg) props["grid_color"] = xg;
                if (_colors.ResolveOr(control.CurveTickColor, "#9AA7B5", where) is { } xt) props["tick_color"] = xt;
                if (_colors.ResolveOr(control.CurveLabelColor, "#C8D2DC", where) is { } xl) props["label_color"] = xl;
                SetFont(props, control, DefaultFont(14));
                _features.Add("curves");
                break;

            case HmiControlType.HistoryTable:
                if (string.IsNullOrWhiteSpace(control.HistTableGroup))
                {
                    _issues.Error("history_table.group", where, "历史表要指定趋势组（savecfg 的组名）");
                    return null;
                }
                var hcols = TableColumnsOf(control.HistTableColumns, where, "history_table", true);
                if (hcols == null)
                {
                    _issues.Error("history_table.columns", where, "历史表至少要有一列");
                    return null;
                }
                props["columns"] = hcols;
                props["source"] = new JsonObject { ["group"] = control.HistTableGroup!.Trim() };
                // 历史表用自己的行数（契约缺省 500）。复用报警列表的 AlarmMaxRows（10）
                // 会让历史表恒只显示 10 行，而面板没有这一行可改。
                props["max_rows"] = Clamp(control.HistTableMaxRows, 10, 2000);
                props["refresh_ms"] = Clamp(control.HistTableRefreshMs, 0, 60000);
                props["row_height"] = control.HistTableRowHeight > 0 ? Clamp(control.HistTableRowHeight, 16, 60) : 26;
                props["scrollbar"] = control.HistTableScrollbar;
                SetFont(props, control, DefaultFont(14));
                _features.Add("table");
                break;

            case HmiControlType.RecipeTable:
                if (string.IsNullOrWhiteSpace(control.RecipeGroup))
                {
                    _issues.Error("recipe_table.group", where, "配方表要指定配方组");
                    return null;
                }
                var rcols = TableColumnsOf(control.RecipeColumns, where, "recipe_table", false);
                if (rcols == null)
                {
                    _issues.Error("recipe_table.columns", where, "配方表至少要有一列");
                    return null;
                }
                props["columns"] = rcols;
                props["source"] = new JsonObject { ["recipe_group"] = control.RecipeGroup!.Trim() };
                props["row_height"] = control.RecipeRowHeight > 0 ? Clamp(control.RecipeRowHeight, 16, 60) : 26;
                props["scrollbar"] = control.RecipeScrollbar;
                props["show_unit"] = control.RecipeShowUnit;
                SetFont(props, control, DefaultFont(14));
                _features.Add("recipe");
                break;

            case HmiControlType.AsciiDisplay:
                // 运行时只认 gbk / utf8（build_ascii_display 里是硬校验）；契约写的 "ascii" 会让整页建不出来
                props["encoding"] = OneOf(control.AsciiEncoding, "gbk", "utf8", "gbk", where, "ascii_display.encoding");
                props["max_len"] = Clamp(control.AsciiMaxLen, 1, 64);
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                props["align"] = Align(control.TextAlign);
                props["valign"] = VAlign(control.TextVAlign);
                SetFont(props, control, DefaultFont(16));
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
                break;

            case HmiControlType.Screen:
                _issues.Warn("screen.not_exported", where, "嵌套屏幕控件不导出（页面切换用 goto_page 动作）");
                return null;

            default:
                _issues.Warn("widget.unmapped", where, $"控件类型 {control.Type} 没有导出映射，已跳过");
                return null;
        }

        // 图元 kind 级专属参数（skew / top_w / spikes / open / light …）与 figure_common 的
        // fill_opa / 角度：唯一登记表是 FigurePropertyCatalog，面板行与这张表同源。
        if (!string.IsNullOrEmpty(kind)) MapFigureExtras(props, control, kind, where);

        AddCommon(props, control, target, kind, where);

        var finalProps = FilterProps(target, kind, props, where);
        var widget = new JsonObject
        {
            ["id"] = widgetId,
            ["type"] = target,
            ["x"] = (int)Math.Round(control.X),
            ["y"] = (int)Math.Round(control.Y),
            ["w"] = (int)Math.Round(Math.Max(1, control.Width)),
            ["h"] = (int)Math.Round(Math.Max(1, control.Height)),
            ["props"] = finalProps,
        };
        /* 图元的 kind 写在 **props 里**（契约 figures 用 props.kind 区分 40 种）。
         * 早先只把 kind 当局部变量用于白名单过滤，**没有写进 JSON** —— 导出的图元
         * 全靠运行时默认值，等于"选什么形状都画成同一个"。放在 FilterProps 之后写，
         * 避免它被当成"契约外属性"丢掉。 */
        if (!string.IsNullOrEmpty(kind)) finalProps["kind"] = kind;

        CheckBounds(widget, control, where);
        AddBindings(widget, control, target, pageId, widgetId, where);
        AddEvents(widget, control, pageId, widgetId, where);
        if (!control.IsVisible) finalProps["visible"] = false;
        if (!control.IsEnabled) finalProps["enabled"] = false;
        if (!string.IsNullOrWhiteSpace(control.Tooltip)) finalProps["tooltip"] = control.Tooltip.Trim();
        if (control.LayerIndex != 0) finalProps["z"] = control.LayerIndex;

        return widget;
    }

    // ── 各控件细节 ──

    private void MapAlarmList(JsonObject props, HmiControl control, string where)
    {
        var mode = string.IsNullOrWhiteSpace(control.AlarmMode) ? "active" : control.AlarmMode!.Trim();
        if (mode != "active" && mode != "history")
        {
            _issues.Error("alarm_list.mode", where, $"报警列表 mode={mode} 非法（active/history）");
            mode = "active";
        }
        var source = new JsonObject { ["alarm_group"] = control.AlarmGroupFilter ?? "" };
        props["source"] = source;
        props["mode"] = mode;
        if (!string.IsNullOrWhiteSpace(control.AlarmLevelFilter))
            props["filter"] = new JsonObject { ["level"] = control.AlarmLevelFilter!.Trim() };

        var keys = (control.AlarmColumns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 0).Distinct().ToArray();
        var valid = new[] { "time", "level", "name", "value", "text", "state", "ack" };
        var cols = new JsonArray();
        foreach (var k in keys)
        {
            if (!valid.Contains(k))
            {
                _issues.Warn("alarm_list.column", where, $"报警列表列 {k} 不在契约枚举里，已跳过");
                continue;
            }
            cols.Add(new JsonObject { ["key"] = k });
        }
        if (cols.Count == 0)
        {
            _issues.Error("alarm_list.columns", where, "报警列表至少要有一列（37：columns 1..10 项）");
            cols.Add(new JsonObject { ["key"] = "time" });
        }
        props["columns"] = cols;
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
            : Clamp((int)Math.Round(control.Height / Math.Max(1, control.AlarmMaxRows)), 16, 60);
        SetFont(props, control, DefaultFont(14));
        _features.Add("alarm");
    }

    /// <summary>
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
        // 轮换方式（运行时 build_alarm_bar 读 scroll，auto 是缺省；只在 none 时写键）
        if (string.Equals(control.AlarmScroll?.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            props["scroll"] = "none";
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

    private bool MapHistoryCurve(JsonObject props, HmiControl control, string where)
    {
        if (string.IsNullOrWhiteSpace(control.TrendGroup))
        {
            _issues.Error("history_curve.group", where,
                "历史曲线没有指定的采集组（37：group 必须是 trends.json 的 logger id 或 savecfg 的组名）");
            return false;
        }

        var curves = new JsonArray();
        var ids = (control.CurveVariableIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().Take(4).ToArray();
        var palette = new[] { _colors.Primary, _colors.Success, _colors.Warning, _colors.Danger };
        var i = 0;
        foreach (var varRef in ids)
        {
            var tag = _maps.ResolveTag(varRef);
            if (tag == null)
            {
                _issues.Error("history_curve.tag", where, $"历史曲线通道引用了不存在的变量 {varRef}");
                continue;
            }
            curves.Add(new JsonObject
            {
                ["tag"] = tag.Value,
                ["color"] = palette[i % palette.Length],
                ["label"] = _maps.TagName(varRef) ?? "",
                ["axis"] = "left",
            });
            i++;
        }
        if (curves.Count == 0)
        {
            /* 同实时曲线：占位通道让页面能建起来（trend 里没数据时就是一条 0 线）。
             * group 缺了仍然报 Error —— 那个键运行时要拿去 trends.json / savecfg 里对名字，
             * 编不出来。 */
            curves.Add(PlaceholderCurve(palette[0]));
            _issues.Warn("history_curve.curves", where,
                "历史曲线没有绑定变量：先按 0 画一条占位线（绑上变量后自动替换）");
        }

        props["group"] = control.TrendGroup!.Trim();
        var lastMin = control.CurveRangeLastMin > 0 ? control.CurveRangeLastMin : (int)control.SpanMin;
        props["range"] = new JsonObject { ["last_min"] = Clamp(lastMin, 1, 1440) };
        props["refresh_ms"] = Clamp(control.RefreshMs, 0, 60000);
        props["curves"] = curves;
        MapCurveCommon(props, control, where);
        _features.Add("curves");
        return true;
    }

    /// <summary>
    /// 实时曲线：数据来自 sim 采样（`sample_ms` 一条），不读 trends.json，
    /// 所以没有 group / range / refresh_ms，取而代之的是 sample_ms 与 history_s。
    ///
    /// <para><b>返回 false = 这个控件不能导出</b>。必须让调用方跳过整个控件：只写一条
    /// Error 而继续产出缺 <c>curves</c> 的 props，运行时会以"缺 props.curves 数组"
    /// 拒绝整页。</para>
    /// </summary>
    private bool MapRealtimeCurve(JsonObject props, HmiControl control, string where)
    {
        var curves = new JsonArray();
        var ids = (control.CurveVariableIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().Take(4).ToArray();
        var palette = new[] { _colors.Primary, _colors.Success, _colors.Warning, _colors.Danger };
        var i = 0;
        foreach (var varRef in ids)
        {
            var tag = _maps.ResolveTag(varRef);
            if (tag == null)
            {
                _issues.Error("realtime_curve.tag", where, $"实时曲线通道引用了不存在的变量 {varRef}");
                continue;
            }
            curves.Add(new JsonObject
            {
                ["tag"] = tag.Value,
                ["color"] = palette[i % palette.Length],
                ["label"] = _maps.TagName(varRef) ?? "",
                ["axis"] = "left",
            });
            i++;
        }
        if (curves.Count == 0)
        {
            /* 没绑定变量时不拒绝导出：运行时要求 curves 至少 1 条，缺了会**拒绝整页**，
             * 画布跟着变空白 —— 用户只是想先画个框。这里放一条 expr="0" 的占位通道，
             * 画面是一条 0 线（网格、坐标轴都在），绑上变量后自动替换。
             * 不替用户挑一个"真实变量"：那会把工程悄悄绑到别的量上。 */
            curves.Add(PlaceholderCurve(palette[0]));
            _issues.Warn("realtime_curve.curves", where,
                "实时曲线没有绑定变量：先按 0 画一条占位线（画布与板端都能加载，绑上变量后自动替换）");
        }

        props["curves"] = curves;
        props["sample_ms"] = Clamp(control.SampleMs is >= 100 and <= 60000 ? control.SampleMs : 1000, 100, 60000);
        if (control.HistoryS > 0) props["history_s"] = Clamp(control.HistoryS, 1, 86400);
        props["span_s"] = SpanSeconds(control, where);
        // 窗口 × 采样决定图点数（上限 300，widget_curve.c），而 X 轴的跨度是按
        // "实际点数 × 采样周期"标的 —— 窗口超预算时面板说的时长板上根本画不出来，
        // 用户会以为曲线卡住了。这里点出来，别让人对着面板猜。
        {
            var sampleMs = props["sample_ms"]!.GetValue<int>();
            var budgetS = 300L * sampleMs / 1000;
            if (props["span_s"]!.GetValue<int>() > budgetS)
                _issues.Warn("realtime_curve.span", where,
                    $"窗口 {props["span_s"]}s 超出 300 点预算（采样 {sampleMs}ms → 最多 {budgetS}s），" +
                    $"板上只画最近 {budgetS}s");
        }
        MapCurveCommon(props, control, where);
        _features.Add("curves");
        return true;
    }

    /// <summary>
    /// 两条曲线共用的外观与刻度（契约里 20 余项，运行时 build_curve 全读）。
    /// 抽出来的目的只有一个：history 与 realtime 不能各写一份而慢慢漂移。
    /// </summary>
    private void MapCurveCommon(JsonObject props, HmiControl control, string where)
    {
        RequireRange(control, where, "curve.range");
        props["y_min"] = (int)Math.Round(control.MinValue);
        props["y_max"] = (int)Math.Round(control.MaxValue);
        props["grid"] = control.CurveGrid;
        props["legend"] = control.CurveLegend;
        props["show_axis"] = control.CurveShowAxis;
        props["show_ticks"] = control.ShowTicks;
        props["show_labels"] = control.CurveShowLabels;
        // 运行时只实现 lr（rl 会以"暂未实现"拒绝整页），所以 rl 一律回落并告警
        if (string.Equals(control.CurveDirection, "rl", StringComparison.OrdinalIgnoreCase))
            _issues.Warn("curve.direction", where,
                "拖动回看方向 rl 运行时未实现（只支持 lr），已按 lr 导出");
        props["direction"] = "lr";
        props["x_format"] = string.Equals(control.CurveXFormat, "rel", StringComparison.OrdinalIgnoreCase) ? "rel" : "clock";
        props["x_div"] = Clamp(control.CurveXDiv, CurveDivMin, CurveDivMax);
        props["x_minor"] = Clamp(control.CurveXMinor, CurveMinorMin, CurveMinorMax);
        props["x_label_step"] = Clamp(control.CurveXLabelStep, 0, 12);
        props["y_div"] = Clamp(control.CurveYDiv, CurveDivMin, CurveDivMax);
        props["y_minor"] = Clamp(control.CurveYMinor, CurveMinorMin, CurveMinorMax);
        props["y_decimals"] = Clamp(control.CurveYDecimals, 0, 6);
        props["y_digits"] = Clamp(control.CurveYDigits, 0, 12);
        props["y2_min"] = Clamp(control.CurveY2Min, -1000000, 1000000);
        props["y2_max"] = Clamp(control.CurveY2Max, -1000000, 1000000);
        props["y2_decimals"] = Clamp(control.CurveY2Decimals, 0, 6);
        props["y2_digits"] = Clamp(control.CurveY2Digits, 0, 12);
        if (_colors.ResolveOr(control.CurveAxisColor, "#4A5A6A", where) is { } axisC) props["axis_color"] = axisC;
        if (_colors.ResolveOr(control.CurveBgColor, "#141C26", where) is { } bgC) props["bg_color"] = bgC;
        if (_colors.ResolveOr(control.CurveBorderColor, "#3A4A5A", where) is { } bdC) props["border_color"] = bdC;
        if (_colors.ResolveOr(control.CurveGridColor, "#2F3D4C", where) is { } grC) props["grid_color"] = grC;
        if (_colors.ResolveOr(control.CurveTickColor, "#9AA7B5", where) is { } tkC) props["tick_color"] = tkC;
        if (_colors.ResolveOr(control.CurveLabelColor, "#C8D2DC", where) is { } lbC) props["label_color"] = lbC;
        SetFont(props, control, DefaultFont(14), 32);
    }

    private void MapTable(JsonObject props, HmiControl control, string pageId, string widgetId, string where)
    {
        var rows = Clamp(control.RowCount, 1, 10);
        var cols = Clamp(control.ColumnCount, 1, 8);
        if (rows * cols > 80)
        {
            _issues.Error("table.too_large", where, $"表格 {rows}×{cols} 超过 80 格上限（37 §8）");
            return;
        }
        props["rows"] = rows;
        props["cols"] = cols;
        props["header"] = !string.IsNullOrWhiteSpace(control.HeaderTexts);
        if (control.RowHeight > 0) props["row_h"] = (int)Math.Round(control.RowHeight);
        var colWidths = (control.ColumnWidths ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (colWidths.Length == cols)
        {
            var arr = new JsonArray();
            foreach (var w in colWidths)
            {
                if (int.TryParse(w.Trim(), out var v)) arr.Add(v);
                else arr.Add((int)Math.Round(control.Width / cols));
            }
            props["col_w"] = arr;
        }
        SetFont(props, control, DefaultFont(14), 32);

        var cells = new JsonArray();
        // 表头文字（面板「表头」那行，逗号分隔）写成第 0 行的静态单元格。以前只写
        // 布尔 header，表头文字整段丢掉 —— 表头有底色、有分隔线，但一个字都没有。
        // 先写表头、后写用户单元格，同格以用户在单元格编辑器里填的为准。
        if (!string.IsNullOrWhiteSpace(control.HeaderTexts))
        {
            var headers = control.HeaderTexts.Split(',', StringSplitOptions.TrimEntries);
            for (var c = 0; c < headers.Length && c < cols; c++)
            {
                if (headers[c].Length == 0) continue;
                cells.Add(new JsonObject { ["r"] = 0, ["c"] = c, ["text"] = headers[c] });
            }
        }
        foreach (var cell in control.Cells ?? new List<TableCellData>())
        {
            if (cell.Row < 0 || cell.Row >= rows || cell.Col < 0 || cell.Col >= cols)
            {
                _issues.Warn("table.cell_out_of_range", where, $"单元格 ({cell.Row},{cell.Col}) 超出 {rows}×{cols}，已跳过");
                continue;
            }
            if (cell.IsBound)
            {
                var tag = _maps.ResolveTag(cell.VariableId);
                if (tag == null)
                {
                    _issues.Warn("table.cell_tag", where, $"单元格 ({cell.Row},{cell.Col}) 的变量 {cell.VariableId} 不存在，已跳过");
                    continue;
                }
                cells.Add(new JsonObject
                {
                    ["r"] = cell.Row,
                    ["c"] = cell.Col,
                    ["bind"] = new JsonObject { ["tag"] = tag.Value, ["kind"] = "fmt", ["fmt"] = FmtOf(cell.DecimalPlaces) },
                });
            }
            else if (!string.IsNullOrEmpty(cell.Text))
            {
                cells.Add(new JsonObject { ["r"] = cell.Row, ["c"] = cell.Col, ["text"] = cell.Text });
            }
        }
        if (cells.Count > 0) props["cells"] = cells;
        _features.Add("table");
        _features.Add("widgets_ext");
    }

    /// <summary>
    /// 图元专属属性 → props。只写用户设过的（模型字段可空，null = 用运行时缺省），
    /// 避免把一个 kind 的缺省值安到另一个 kind 上。键名/范围来自
    /// <see cref="FigurePropertyCatalog"/>（与面板动态行、覆盖矩阵同源）。
    /// </summary>
    private void MapFigureExtras(JsonObject props, HmiControl control, string kind, string where)
    {
        foreach (var prop in FigurePropertyCatalog.For(kind))
        {
            if (!prop.HasUserValue(control)) continue;
            var raw = prop.Get(control);
            switch (prop.Type)
            {
                case FigurePropType.Int:
                    props[prop.Key] = Clamp(FigurePropertyCatalog.ToInt(raw), (int)prop.Min, (int)prop.Max);
                    break;
                case FigurePropType.Float:
                    props[prop.Key] = Math.Round(
                        Math.Clamp(FigurePropertyCatalog.ToDouble(raw), prop.Min, prop.Max), 3);
                    break;
                case FigurePropType.Bool:
                    props[prop.Key] = FigurePropertyCatalog.ToBool(raw);
                    break;
                default:
                    props[prop.Key] = OneOf(raw as string, prop.Options.ToArray(), (string)prop.Default,
                        where, $"figure.{kind}.{prop.Key}");
                    break;
            }
        }

        if (control.FigureFillOpa is { } fillOpa) props["fill_opa"] = Clamp(fillOpa, 0, 255);
        if (control.ArcAngleStart is { } start) props["angle_start"] = Clamp(start, -360, 720);
        if (control.ArcAngleEnd is { } end) props["angle_end"] = Clamp(end, -360, 720);
    }

    private void MapShapeCommon(JsonObject props, HmiControl control, string where)
    {
        props["fill_color"] = _colors.ResolveOr(control.BgColor, _colors.Surface, where);
        props["border_color"] = _colors.ResolveOr(control.BorderColor, _colors.Border, where);
        props["border_width"] = Math.Max(0, control.BorderWidth);
        // 图元的圆角是几何参数（ui_figure 读），没配就不写、让图元用自己的缺省
        if (control.Radius > 0) props["radius"] = control.Radius;
    }

    /// <summary>
    /// 容器的子控件 → <c>props.children</c>（37 §6.4：数组，坐标<b>相对容器</b>）。
    /// 子控件节点与屏幕内控件走同一个 <see cref="Map"/>（同格式、同绑定/事件），
    /// 坐标直接用模型里的相对值，不叠加任何原点。
    ///
    /// <para><b>不写 <c>layout</c></b>：契约枚举只有 <c>free</c> 一个值且是默认值，
    /// 写了只会多一个键（v1 没有自动布局，别引入）。</para>
    /// </summary>
    private void MapContainerChildren(JsonObject props, HmiControl control, string pageId, string widgetId)
    {
        if (control.Children.Count == 0) return;   // 空容器不写 children（契约默认 null）
        var children = new JsonArray();
        var seq = 0;
        foreach (var child in control.Children)
        {
            seq++;
            var childWidget = Map(child, pageId, $"{widgetId}_{seq}");
            if (childWidget == null) continue;     // 跳过时 issues 里已有原因
            children.Add(childWidget);
        }
        if (children.Count > 0) props["children"] = children;
    }

    private JsonArray PointsOf(HmiControl control, string where)
    {
        var pts = control.Points;
        var arr = new JsonArray();
        if (pts == null || pts.Count < 2)
        {
            _issues.Warn("shape.points", where, "折线没有顶点（至少 2 个），已退化成一条对角线");
            arr.Add(new JsonArray(0, 0));
            arr.Add(new JsonArray((int)Math.Round(control.Width), (int)Math.Round(control.Height)));
            return arr;
        }
        foreach (var p in pts.Take(64))
            arr.Add(new JsonArray((int)Math.Round(p.X), (int)Math.Round(p.Y)));
        return arr;
    }

    private void AddCommon(JsonObject props, HmiControl control, string target, string kind, string where)
    {
        ApplyBoxKeys(props, control, where);
        if (control.Opacity < 100)
            props["opacity"] = Clamp((int)Math.Round(control.Opacity * 255 / 100), 0, 255);
        ApplyTransform(props, control, target, kind, where);
    }

    /// <summary>
    /// 通用框样式四键（<c>bg_color</c> / <c>border_color</c> / <c>border_width</c> / <c>radius</c>）。
    ///
    /// <para><b>有框的类型一律写，含 0</b>：运行时只在键存在时覆盖外观
    /// （<c>apply_common_style</c>），缺键等于"吃控件主题"（按钮 8、滑块/进度条圆头）。
    /// 而面板上这些行显示的永远是模型值（新控件是 0）—— 只在 <c>&gt; 0</c> 时写键，
    /// 就成了"面板说 0、板子给圆角"，把圆角从 8 改回 0 也毫无变化。
    /// 面板与导出器共用 <see cref="AppearanceSupport.HasBoxBorder"/> 这一张表。</para>
    /// </summary>
    private void ApplyBoxKeys(JsonObject props, HmiControl control, string where)
    {
        // 背景色只在"面板上有这一行"的类型上写（判据表与面板共用 AppearanceSupport）：
        // 管道这类自己画几何的控件没有底色，写进去的键只会被运行时忽略或误用
        if (AppearanceSupport.HasBgColor(control.Type))
        {
            var bg = _colors.Resolve(control.BgColor, where);
            if (bg != null) props["bg_color"] = bg;
        }
        var border = _colors.Resolve(control.BorderColor, where);
        if (border != null) props["border_color"] = border;
        if (AppearanceSupport.HasBoxBorder(control.Type))
        {
            props["border_width"] = control.BorderWidth;
            props["radius"] = control.Radius;
        }
        else
        {
            if (control.BorderWidth > 0) props["border_width"] = control.BorderWidth;
            if (control.Radius > 0) props["radius"] = control.Radius;
        }
    }

    /// <summary>
    /// 旋转 / 镜像（ADR-0019）：写 <c>rotation</c> / <c>mirror_x</c> / <c>mirror_y</c>。
    ///
    /// <para>运行时按类型分三档（<c>widgets.c</c> 的 apply_transform），**越档直接
    /// `return -1` ⇒ 整页建不起来**。所以这里先按同一张表挡一遍：能表达的照写，
    /// 不能表达的报 Error 并让人去改，绝不把"看起来设了、板上没转"的工程发出去。</para>
    /// </summary>
    private void ApplyTransform(JsonObject props, HmiControl control, string target, string kind, string where)
    {
        var angle = NormalizeAngle(control.EffectiveRotation());
        var mx = control.MirrorX;
        var my = control.MirrorY;
        if (angle == 0 && !mx && !my) return;

        if (IsContainerTarget(target, kind))
        {
            _issues.Error("widget.transform", where,
                $"{target}{(string.IsNullOrEmpty(kind) ? "" : "/" + kind)} 是容器类控件，不支持旋转/镜像（运行时建页会拒绝）");
            return;
        }

        if (IsTextClass(target))
        {
            if (mx || my)
            {
                _issues.Error("widget.transform", where,
                    $"{target} 是文本类控件，不支持镜像（LVGL 9.5 无负缩放），请改用图片或图元");
                return;
            }
            if (angle % 90 != 0)
            {
                _issues.Error("widget.transform", where,
                    $"{target} 是文本类控件，旋转只能是 0/90/180/270 度（当前 {angle}°）");
                return;
            }
            if (control.Width > 256 || control.Height > 256)
            {
                _issues.Error("widget.transform", where,
                    $"{target} 旋转需要离屏图层，控件不得超过 256×256（当前 {control.Width}×{control.Height}）");
                return;
            }
        }
        else if (target == "image")
        {
            /* 图片：任意角 + 镜像都支持（镜像直接翻像素） */
        }
        else if (target == "shape")
        {
            /* 多边形族把旋转/镜像烘进顶点；rect/round_rect/circle 只有样式变换，
             * 没有顶点可翻 ⇒ 不支持镜像（与运行时同一条判据） */
            if ((mx || my) && kind is "rect" or "round_rect" or "circle")
            {
                _issues.Error("widget.transform", where,
                    $"图元 {kind} 没有顶点可翻，不支持镜像（改用 polygon / 星形等顶点图元）");
                return;
            }
        }
        else
        {
            _issues.Error("widget.transform", where,
                $"{target} 不支持旋转/镜像（ADR-0019 只放开图片、图元与文本类）");
            return;
        }

        props["rotation"] = angle;
        if (mx) props["mirror_x"] = true;
        if (my) props["mirror_y"] = true;
    }

    /// <summary>角度归一到 0..359（顺时针为正，逆时针由 RotationDirection 给负号）。</summary>
    private static int NormalizeAngle(double angle)
    {
        var a = (int)Math.Round(angle) % 360;
        return a < 0 ? a + 360 : a;
    }

    /// <summary>运行时 is_text_class 的镜像表：这些类型只允许 90° 倍数且禁镜像。</summary>
    private static bool IsTextClass(string target) => target is
        "label" or "value" or "enum_text" or "input" or "combobox" or "button"
        or "checkbox" or "ascii_display" or "message" or "alarm_bar" or "roller";

    /// <summary>容器类：tabview / keyboard / 带子控件的图元，运行时一律拒绝变换。</summary>
    private static bool IsContainerTarget(string target, string kind)
        => target is "tabview" or "keyboard" || (target == "shape" && kind == "container");

    private void AddCommonBox(JsonObject props, HmiControl control, string where)
        => ApplyBoxKeys(props, control, where);

    /// <summary>
    /// 按钮的交互属性：长按门槛 / 长按阈值 / 二次确认 / 禁用表现。
    ///
    /// <para><b>越界会拒绝整页</b>：运行时的 button 建页对 <c>hold_ms</c>（0..5000）与
    /// <c>long_press_ms</c>（300..5000）是**硬校验**，超范围直接 `return -1`。所以这里
    /// 一律钳到合法区间并告警，绝不让一个数字把整页带死。</para>
    /// </summary>
    private void AddButtonBehavior(JsonObject props, HmiControl control, string where)
    {
        if (control.ButtonHoldMs > 0)
            props["hold_ms"] = Clamp(control.ButtonHoldMs, 0, 5000);

        if (control.ButtonLongPressMs is > 0 and not 800)
        {
            if (control.ButtonLongPressMs is < 300 or > 5000)
                _issues.Warn("button.long_press_range", where,
                    $"长按阈值 {control.ButtonLongPressMs}ms 超出 300..5000，已钳到边界（运行时会对越界值拒绝整页）");
            props["long_press_ms"] = Clamp(control.ButtonLongPressMs, 300, 5000);
        }

        var offStyle = (control.ButtonOffStyle ?? "").Trim().ToLowerInvariant();
        if (offStyle == "hide") props["off_style"] = "hide";      // dim 是运行时默认，不写
        else if (offStyle.Length > 0 && offStyle != "dim")
            _issues.Warn("button.off_style", where,
                $"禁用表现 {control.ButtonOffStyle} 非法（dim/hide），已按 dim 处理");

        var confirmText = control.ButtonConfirmText?.Trim();
        if (!string.IsNullOrEmpty(confirmText))
        {
            var cf = new JsonObject { ["text"] = confirmText };
            if (!string.IsNullOrWhiteSpace(control.ButtonConfirmYes)) cf["yes"] = control.ButtonConfirmYes!.Trim();
            if (!string.IsNullOrWhiteSpace(control.ButtonConfirmNo)) cf["no"] = control.ButtonConfirmNo!.Trim();
            if (control.ButtonConfirmWaitMs > 0) cf["wait_ms"] = Clamp(control.ButtonConfirmWaitMs, 0, 600_000);
            props["confirm"] = cf;
        }
    }

    /// <summary>
    /// 把 <c>50:#38C172,80:#F5A524,:#E5484D</c> 解析成契约的 ranges 数组
    /// （<c>[{lt:50,color},{lt:80,color},{color}]</c>；最后一段省略 lt 作兜底）。
    /// 写坏的段只告警跳过 —— 一个错颜色不该让整页导不出去。
    /// </summary>
    /// <summary>
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

    /// <summary>
    /// 契约 figures 的 40 个 kind（与 spec/widget-schema.json 一一对应）。
    /// 少一个会让那个图元"选得到但导不出去"，多一个会被运行时拒绝 —— 两边都难查，
    /// 所以集中在这里，配合测试里的数量断言。
    /// </summary>
    private static readonly string[] ShapeKinds =
        {
            "arc",
            "arrow",
            "arrow_thick",
            "arrow_thin",
            "ball3d",
            "callout",
            "circle",
            "cone3d",
            "container",
            "cross",
            "cube",
            "diamond",
            "ellipse",
            "hexagon",
            "line",
            "octagon",
            "parallelogram",
            "pie",
            "pipe_elbow",
            "pipe_h",
            "pipe_joint",
            "pipe_v",
            "plane_concave",
            "plane_convex",
            "plane_groove",
            "polygon",
            "polyline",
            "rect",
            "right_triangle",
            "round_rect",
            "star",
            "star5",
            "tank",
            "torus3d",
            "trapezoid",
            "triangle",
            "valve_ball",
            "valve_butterfly",
            "valve_gate",
            "wedge"
        };

    /// <summary>图表序列：每行 <c>变量Id:标签</c> → <c>[{label,value:{tag},color}]</c>（最多 8 条）。</summary>
    /// <summary>
    /// 占位通道：<c>{"expr":"0"}</c>。曲线类控件一条通道都没有时用它 ——
    /// 运行时要 <c>curves</c> 至少 1 条、图表 <c>series</c> 至少 1 项，缺了**拒绝整页**，
    /// 设计画布跟着变空白。给一条 0 线比白屏好排查，也比替用户挑一个真实变量安全。
    /// </summary>
    private static JsonObject PlaceholderCurve(string color) => new()
    {
        ["expr"] = "0",
        ["color"] = color,
        ["label"] = "",
        ["axis"] = "left",
    };

    private JsonArray ChartSeriesOf(HmiControl control, string where)
    {
        var arr = new JsonArray();
        var palette = new[] { _colors.Primary, _colors.Success, _colors.Warning, _colors.Danger };
        var i = 0;

        foreach (var line in SplitLines(control.ChartSeries).Take(8))
        {
            var idx = line.IndexOf(':');
            var varRef = idx > 0 ? line[..idx].Trim() : line;
            var label = idx > 0 ? line[(idx + 1)..].Trim() : varRef;
            var tag = ResolveChartTag(varRef, where, "chart.series");
            if (tag == null) continue;

            arr.Add(new JsonObject
            {
                ["label"] = label.Length > 0 ? label : varRef,
                ["value"] = new JsonObject { ["tag"] = tag.Value },
                ["color"] = palette[i % palette.Length],
            });
            i++;
        }
        return arr;
    }

    private int? ResolveChartTag(string? varRef, string where, string code)
    {
        if (string.IsNullOrWhiteSpace(varRef)) return null;
        var tag = _maps.ResolveTag(varRef.Trim());
        if (tag == null)
        {
            _issues.Error(code, where, $"引用了不存在的变量 {varRef}");
            return null;
        }
        return tag;
    }

    /// <summary>
    /// 表列：每行 <c>键:标签[:宽]</c> → <c>[{key,label,w}]</c>。
    /// <paramref name="history"/> 时键限定在历史表的枚举 + 变量 Id；配方表则允许 name/desc/序号。
    /// </summary>
    private JsonArray? TableColumnsOf(string? text, string where, string code, bool history)
    {
        /* 运行时（0.49.0）对 history_table 的列 key **只认 time 和变量 tag**：
         * level/name/value/text/state/ack 会直接拒绝整页（build_curve 里的校验）。
         * 契约写的是"可取 time/level/name/... 或变量 tag"——比实现宽，按实现收紧。 */
        var valid = history ? new[] { "time" } : new[] { "name", "desc" };
        var arr = new JsonArray();

        foreach (var line in SplitLines(text).Take(10))
        {
            var parts = line.Split(':', StringSplitOptions.TrimEntries);
            var key = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            var label = parts.Length > 1 ? parts[1] : key;
            var isEnumKey = valid.Contains(key);
            var isVarOrIndex = !isEnumKey && (history
                ? _maps.ResolveTag(key) != null
                : int.TryParse(key, out _));

            if (!isEnumKey && !isVarOrIndex)
            {
                _issues.Warn(code + ".column", where, $"列「{key}」不是合法键，已跳过");
                continue;
            }
            /* 历史表的变量列：**key 直接是数值 tag id**（运行时 `hmi_json_int(key, -1)`），
             * 不是 key="tag" + tag=N —— 写成后者会被判成"key 不合法"并拒绝整页。 */
            var col = new JsonObject();
            if (isEnumKey) col["key"] = key;
            else if (history) col["key"] = _maps.ResolveTag(key)!.Value;
            else col["key"] = key;
            if (label.Length > 0 && label != key) col["label"] = label;
            if (parts.Length > 2 && int.TryParse(parts[2], out var w)) col["w"] = Clamp(w, 20, 400);
            arr.Add(col);
        }
        return arr.Count > 0 ? arr : null;
    }

    /// <summary>按行切分（去空行、trim）。</summary>
    private static IEnumerable<string> SplitLines(string? text)
        => (text ?? "").Replace("\r", "").Split('\n')
            .Select(x => x.Trim()).Where(x => x.Length > 0);

    /// <summary>
    /// 解析 <c>值:资源Id</c> 多行 → <c>[{v, src}]</c>。
    /// 值重复、资源导不出来都只告警跳过（一个错行不该让整页导不出去）。
    /// </summary>
    private JsonArray ParseAssetPairs(string? text, string where, string code)
    {
        var arr = new JsonArray();
        var seen = new HashSet<double>();

        foreach (var line in SplitLines(text).Take(16))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                _issues.Warn(code + ".pair", where, $"「{line}」不是 值:资源Id 的写法，已跳过");
                continue;
            }
            if (!double.TryParse(line[..idx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                _issues.Warn(code + ".value", where, $"「{line}」的值不是数字，已跳过");
                continue;
            }
            if (!seen.Add(v))
            {
                _issues.Warn(code + ".dup", where, $"值 {v} 重复，已跳过后一项");
                continue;
            }
            var assetSrc = _assets.SrcFor(line[(idx + 1)..].Trim(), where);
            if (assetSrc == null)
            {
                _issues.Warn(code + ".asset", where, $"「{line}」的图片导不出来，已跳过");
                continue;
            }
            arr.Add(new JsonObject { ["v"] = v, ["src"] = assetSrc });
        }
        return arr;
    }

    /// <summary>滚轮的可见行数必须是奇数（选中行才在正中）。</summary>
    private static int Odd(int v) => (v & 1) == 1 ? v : v + 1;

    /// <summary>页签位置：编辑器存 Top/Bottom/Left/Right，契约要小写 top/bottom/left/right。</summary>
    private static string TabPosOf(string? pos) => (pos ?? "").Trim().ToLowerInvariant() switch
    {
        "bottom" => "bottom",
        "left" => "left",
        "right" => "right",
        _ => "top",
    };

    /* 曲线刻度的取值域：运行时 build_curve 是硬校验（y_div/x_div 1..10、
     * y_minor/x_minor 0..5），越界直接拒绝整页，所以导出期就钳在这里。 */
    private const int CurveDivMin = 1;
    private const int CurveDivMax = 10;
    private const int CurveMinorMin = 0;
    private const int CurveMinorMax = 5;

    /// <summary>
    /// 量程下限必须小于上限。运行时的 bar / slider / knob / gauge / chart / 曲线
    /// 建页时对 <c>max &lt;= min</c> 一律 `return -1` ⇒ **整页建不起来**，
    /// 而这种工程在导出报告里是干净的（导出器原本不校验），所以必须在这里拦。
    /// </summary>
    private void RequireRange(HmiControl control, string where, string code)
    {
        if (control.MaxValue > control.MinValue) return;
        _issues.Error(code, where,
            $"量程下限 {control.MinValue} 不小于上限 {control.MaxValue}：" +
            "运行时会对这种量程拒绝整页（建页失败），请改成上限大于下限");
    }

    /// <summary>
    /// 曲线窗口秒数。运行时只接受 1..3600（&gt;1 小时拒绝整页），编辑器填的是分钟，
    /// 所以超过 60 分钟一律按 60 导出并告警，绝不把越界值写出去。
    /// </summary>
    private int SpanSeconds(HmiControl control, string where)
    {
        var minutes = control.SpanMin;
        if (minutes < 1) minutes = 1;
        if (minutes > 60)
        {
            _issues.Warn("curve.span", where,
                $"窗口 {minutes} 分钟超出运行时上限 60 分钟（span_s ≤ 3600），已按 60 分钟导出");
            minutes = 60;
        }
        return minutes * 60;
    }

    /// <summary>阀门类型 → 契约 figure kind（只有球/蝶/闸三种，Globe 回落球阀）。</summary>
    private string ValveKindOf(string? valveType, string where)
    {
        var v = (valveType ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "gate" => "valve_gate",
            "butterfly" => "valve_butterfly",
            "ball" or "" => "valve_ball",
            _ => ValveFallback(valveType, where),
        };
    }

    private string ValveFallback(string? valveType, string where)
    {
        _issues.Warn("valve.kind", where,
            $"阀门类型 {valveType} 在目标图元里没有对应形状（只有球阀/蝶阀/闸阀），已按球阀导出");
        return "valve_ball";
    }

    private JsonArray? ParseRanges(string? text, string where)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var arr = new JsonArray();
        foreach (var raw in text.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Trim();
            if (seg.Length == 0) continue;

            // `:#E5484D` 这种**省略阈值**的兜底段，冒号在下标 0 —— 早先写成 idx > 0，
            // 于是 ":#E5484D" 整段被当成颜色，回退成主题色（兜底段永远不生效）。
            var idx = seg.IndexOf(':');
            var ltPart = idx > 0 ? seg[..idx].Trim() : "";
            var colorPart = idx >= 0 ? seg[(idx + 1)..].Trim() : seg;
            if (colorPart.Length == 0) continue;
            if (colorPart.Length == 0) continue;

            var color = _colors.Resolve(colorPart, where);
            if (color == null)
            {
                _issues.Warn("widget.ranges.color", where, $"色段「{seg}」的颜色不是 #RRGGBB，已跳过");
                continue;
            }

            var obj = new JsonObject();
            if (ltPart.Length > 0)
            {
                if (!double.TryParse(ltPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var lt))
                {
                    _issues.Warn("widget.ranges.lt", where, $"色段「{seg}」的阈值不是数字，已跳过");
                    continue;
                }
                obj["lt"] = lt;
            }
            obj["color"] = color;
            arr.Add(obj);
        }
        return arr.Count > 0 ? arr : null;
    }

    private void AddPressed(JsonObject props, HmiControl control, string where)
    {
        var pressed = control.VisualStates.FirstOrDefault(s => s.State == VisualState.Pressed);
        if (pressed == null || pressed.IsEmpty) return;
        var obj = new JsonObject();
        var bg = _colors.Resolve(pressed.BgColor, where);
        if (bg != null) obj["bg_color"] = bg;
        var border = _colors.Resolve(pressed.BorderColor, where);
        if (border != null) obj["border_color"] = border;
        var fg = _colors.Resolve(pressed.TextColor, where);
        if (fg != null) obj["text_color"] = fg;
        if (!string.IsNullOrEmpty(pressed.Text)) obj["text"] = pressed.Text;
        if (obj.Count > 0) props["pressed"] = obj;

        foreach (var state in control.VisualStates.Where(s => s.State != VisualState.Pressed && !s.IsEmpty))
            _issues.Warn("widget.visual_state", where,
                $"控件状态样式 {state.State} 未导出：目标只有 button.pressed 与按变量条件的 states[]，编辑器这套 LVGL 状态位没有等价表达");
    }

    // ── 绑定 ──

    private void AddBindings(JsonObject widget, HmiControl control, string target, string pageId, string widgetId, string where)
    {
        var bind = new JsonObject();
        foreach (var b in control.VariableBindings)
        {
            var prop = MapBindProperty(b.PropertyName, control.Type);
            if (prop == null)
            {
                _issues.Warn("bind.property", where, $"绑定属性 {b.PropertyName} 在目标里没有对应可绑定属性，已跳过");
                continue;
            }
            if (!_schema.CanBind(target, prop))
            {
                _issues.Warn("bind.not_bindable", where,
                    $"{target} 不支持绑定 {prop}（契约 bindable: {string.Join('/', BindableOf(target))}），已跳过");
                continue;
            }

            if (b.Kind == BindingSourceKind.Expression)
            {
                var expr = RewriteExpression(b.Expression, where);
                if (expr == null) continue;
                bind[prop] = new JsonObject { ["kind"] = "expr", ["expr"] = expr };
                _features.Add("expr");
                continue;
            }

            var tag = _maps.ResolveTag(b.VariableId);
            if (tag == null)
            {
                _issues.Error("bind.tag_missing", where, $"绑定引用的变量 {b.VariableId} 不存在");
                continue;
            }

            if (prop == "text")
            {
                // 带前后缀的文本用占位符（运行时按变量值替换），不再单发 bind——
                // 两者同时存在会让 bind 的结果把前后缀整段盖掉。
                // value 控件例外：它自己的 prefix/suffix/unit 键已经在 props 里，
                // 运行时会拼「前缀 + 数值 + 单位 + 后缀」，再折成占位符会拼两遍。
                if (control.Type != HmiControlType.Value &&
                    (!string.IsNullOrEmpty(control.ValuePrefix) || !string.IsNullOrEmpty(control.ValueSuffix)))
                {
                    var fmt = FmtForTag(tag.Value, control.ValueDecimalPlaces);
                    widget["props"]!["text"] =
                        $"{control.ValuePrefix}{{tag:{tag.Value}|{fmt}}}{control.ValueSuffix}";
                    continue;
                }
                // 字符串变量直接搬，不能套数值格式：bind.kind=fmt 配 %.2f 会把
                // 字符串变量按数值渲染（结果是 0.00），输入框里就看不到真正的文本。
                if (_maps.TypeByTag.TryGetValue(tag.Value, out var valueType) && valueType == "string")
                {
                    bind[prop] = new JsonObject { ["tag"] = tag.Value };
                    continue;
                }
                bind[prop] = new JsonObject { ["tag"] = tag.Value, ["kind"] = "fmt", ["fmt"] = FmtForTag(tag.Value, control.ValueDecimalPlaces) };
                continue;
            }

            if (prop is "color" or "bg_color")
            {
                _issues.Warn("bind.color", where,
                    "颜色绑定需要区间表（kind:ranges），编辑器没有区间模型，已跳过——请在目标里用 states[] 或直接给固定颜色");
                continue;
            }

            bind[prop] = new JsonObject { ["tag"] = tag.Value };
        }

        if (bind.Count > 0) widget["bind"] = bind;
    }

    private string? RewriteExpression(string? expression, string where)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            _issues.Error("bind.expr_empty", where, "表达式绑定为空");
            return null;
        }
        try
        {
            var ast = ExpressionEngine.Compile(expression);
            var rewritten = ast.ToCExpression(name =>
            {
                var tag = _maps.ResolveTag(name);
                if (tag == null)
                    throw new ChengeExpressionException($"表达式引用的变量 {name} 不存在");
                return $"tag({tag.Value})";
            });
            return rewritten;
        }
        catch (ChengeExpressionException ex)
        {
            _issues.Error("bind.expr_var", where, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _issues.Error("bind.expr_syntax", where, $"表达式 {expression} 无法编译：{ex.Message}");
            return null;
        }
    }

    // ── 事件与动作 ──

    private void AddEvents(JsonObject widget, HmiControl control, string pageId, string widgetId, string where)
    {
        var actions = control.Actions.Where(a => a.ActionType != "None" && !string.IsNullOrWhiteSpace(a.ActionType)).ToList();
        if (actions.Count == 0) return;

        var byEvent = new Dictionary<string, List<(HmiAction Action, string? Script)>>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var ev = MapEventName(action);
            if (ev == null)
            {
                _issues.Warn("event.unsupported", where, $"动作 {action.ActionType} 没有可挂的事件，已跳过");
                continue;
            }
            if (!byEvent.TryGetValue(ev, out var list))
            {
                list = new List<(HmiAction, string?)>();
                byEvent[ev] = list;
            }
            string? script = null;
            if (action.ActionType == "ExecuteScript")
            {
                if (action.Target != null && _maps.ScriptById.TryGetValue(action.Target, out var s)) script = s.Code;
                else
                {
                    _issues.Error("script.missing", where, $"动作引用的脚本 {action.Target} 不存在");
                    continue;
                }
            }
            list.Add((action, script));
        }

        if (byEvent.Count == 0) return;

        var events = new JsonObject();
        foreach (var (ev, list) in byEvent)
        {
            var scripted = list.Where(x => x.Script != null).ToList();
            var declarative = list.Where(x => x.Script == null).ToList();

            if (scripted.Count > 0 && declarative.Count > 0)
            {
                _issues.Error("event.action_script_conflict", where,
                    $"事件 {ev} 同时挂了动作与脚本（37 §6.2 二选一）");
                continue;
            }
            if (scripted.Count > 1)
            {
                _issues.Error("event.multi_script", where, $"事件 {ev} 挂了 {scripted.Count} 段脚本；一个事件只能有一段");
                continue;
            }
            if (scripted.Count == 1)
            {
                events[ev] = new JsonObject { ["script"] = scripted[0].Script };
                _features.Add("lua");
                continue;
            }
            if (declarative.Count > 1)
            {
                _issues.Error("event.multi_action", where,
                    $"事件 {ev} 挂了 {declarative.Count} 个动作；37 §6.2 规定一个事件只能有 1 个动作，多步请改用 Lua 脚本");
                continue;
            }

            var (act, _) = declarative[0];
            var mapped = MapAction(act, where);
            if (mapped == null) continue;
            events[ev] = new JsonObject { ["actions"] = new JsonArray(mapped) };
        }

        if (events.Count > 0) widget["events"] = events;
    }

    private static string? MapEventName(HmiAction action) => action.ActionType switch
    {
        "MomentaryOn" or "MomentaryOff" => action.ActionType == "MomentaryOn" ? "press" : "release",
        "SwitchScreen" or "ExecuteScript" or "Login" or "Logout" => "click",
        "SetVariable" or "SetBit" or "ResetBit" or "ToggleBit" => "click",
        _ => null,
    };

    private JsonObject? MapAction(HmiAction action, string where)
    {
        switch (action.ActionType)
        {
            case "SetVariable":
            {
                var tag = RequireTag(action.Target, where, action.ActionType);
                if (tag == null) return null;
                var obj = new JsonObject { ["do"] = "write_tag", ["tag"] = tag.Value };
                if (double.TryParse(action.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    obj["value"] = WriteValue(tag.Value, v);
                else if (bool.TryParse(action.Value, out var b))
                    obj["value"] = b;
                else if (string.IsNullOrWhiteSpace(action.Value))
                {
                    _issues.Error("action.no_value", where, "写变量动作没有给值");
                    return null;
                }
                else obj["value"] = action.Value;
                return obj;
            }
            case "SetBit":
            case "ResetBit":
            case "MomentaryOn":
            case "MomentaryOff":
            {
                var tag = RequireTag(action.Target, where, action.ActionType);
                if (tag == null) return null;
                var on = action.ActionType is "SetBit" or "MomentaryOn";
                return new JsonObject { ["do"] = "write_tag", ["tag"] = tag.Value, ["value"] = WriteValue(tag.Value, on ? 1 : 0) };
            }
            case "ToggleBit":
            {
                var tag = RequireTag(action.Target, where, action.ActionType);
                return tag == null ? null : new JsonObject { ["do"] = "toggle_tag", ["tag"] = tag.Value };
            }
            case "SwitchScreen":
            {
                var page = _maps.PageIdOf(action.Target ?? "");
                if (page == null)
                {
                    _issues.Error("action.page_missing", where, $"跳页动作的目标页面 {action.Target} 不存在");
                    return null;
                }
                return new JsonObject { ["do"] = "goto_page", ["page"] = page };
            }
            case "Login":
                return new JsonObject { ["do"] = "login" };
            case "Logout":
                return new JsonObject { ["do"] = "logout" };
            default:
                _issues.Warn("action.unmapped", where, $"动作 {action.ActionType} 没有导出映射，已跳过");
                return null;
        }
    }

    /// <summary>
    /// 写动作的值：**bool 变量必须发 true/false**。发 1/0 会被运行时按"数值写布尔"拒绝
    /// （日志：`tagdb: tagN 是布尔，数值写被拒绝`），按钮点了没反应就是这么来的。
    /// </summary>
    private JsonNode WriteValue(int tag, double numeric) =>
        _maps.IsBoolTag(tag) ? JsonValue.Create(numeric != 0) : JsonValue.Create(numeric);

    private int? RequireTag(string? variableRef, string where, string actionType)
    {
        var tag = _maps.ResolveTag(variableRef);
        if (tag == null)
            _issues.Error("action.tag_missing", where, $"{actionType} 的目标变量 {variableRef} 不存在");
        return tag;
    }

    // ── 收尾 ──

    private string? TargetType(HmiControlType type, string where)
    {
        var target = ChengeContractTypes.Of(type);

        if (target == null)
        {
            _issues.Warn("widget.no_target", where, $"控件类型 {type} 在目标契约里没有对应类型，已跳过");
            return null;
        }
        if (!_schema.HasWidget(target))
        {
            _issues.Error("widget.not_in_schema", where, $"目标类型 {target} 不在 spec/widget-schema.json 里");
            return null;
        }
        return target;
    }

    private JsonObject FilterProps(string type, string kind, JsonObject props, string where)
    {
        var allowed = _schema.AllowedProps(type, string.IsNullOrEmpty(kind) ? null : kind);
        foreach (var key in props.Select(kv => kv.Key).ToList())
        {
            if (allowed.Contains(key)) continue;
            props.Remove(key);
            _issues.Warn("prop.not_in_schema", where,
                $"属性 {key} 不在 {type}{(string.IsNullOrEmpty(kind) ? "" : $"/{kind}")} 的契约里，已丢弃（37 §8：未知属性会让运行时拒绝整页）");
        }
        return props;
    }

    private void CheckBounds(JsonObject widget, HmiControl control, string where)
    {
        var x = (int)widget["x"]!;
        var y = (int)widget["y"]!;
        var w = (int)widget["w"]!;
        var h = (int)widget["h"]!;
        if (x < 0 || y < 0 || x + w > _screenWidth || y + h > _screenHeight)
            _issues.Error("widget.out_of_screen", where,
                $"控件 {control.Name} 超出屏幕 {_screenWidth}×{_screenHeight}（{x},{y},{w},{h}）；运行时建页会拒绝");
    }

    private void SetText(JsonObject props, HmiControl control, string where)
    {
        var text = control.Text ?? "";
        foreach (var bad in new[] { "✗", "✔", "☑" })
        {
            if (text.Contains(bad, StringComparison.Ordinal))
            {
                _issues.Warn("text.glyph_missing", where,
                    $"文本里的 {bad} 在板端字体里没有字形，请换成 × / ✓");
                break;
            }
        }
        props["text"] = text;
    }

    /// <summary>
    /// 字号。面板允许 6..72，但运行时的 table / curve 对 <c>font_size</c> 是 10..32 的
    /// 硬校验（越界直接拒绝整页），所以那两类必须传 <paramref name="max"/>=32。
    /// </summary>
    private void SetFont(JsonObject props, HmiControl control, int fallback, int max = 48)
    {
        var size = control.FontSize > 0 ? control.FontSize : fallback;
        props["font_size"] = Clamp(size, 10, max);
    }

    private static int DefaultFont(int size) => size;

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    private static string Align(TextAlignMode align) => align switch
    {
        TextAlignMode.Left => "left",
        TextAlignMode.Right => "right",
        _ => "center",
    };

    private static string VAlign(TextVAlignMode align) => align switch
    {
        TextVAlignMode.Top => "top",
        TextVAlignMode.Bottom => "bottom",
        _ => "middle",
    };

    /// <summary>面板的长文模式 → 契约 label.long_mode；wrap 是运行时缺省，返回 null 不写键。</summary>
    private static string? LongModeOf(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "dots" => "dots",
        "clip" => "clip",
        "scroll" => "scroll",
        "scrollcircular" or "scroll_circular" => "scroll_circular",
        _ => null,
    };

    private static string? MapBindProperty(string propertyName, HmiControlType type)
    {
        var p = propertyName.Trim().ToLowerInvariant();
        return p switch
        {
            "text" => "text",
            "value" => "value",
            "ischecked" or "checked" => "checked",
            "bgcolor" => "bg_color",
            "textcolor" or "color" => "color",
            "isvisible" or "visible" => "visible",
            "isenabled" or "enabled" => "enabled",
            _ => null,
        };
    }

    private IEnumerable<string> BindableOf(string type)
    {
        if (!_schema.HasWidget(type)) return Array.Empty<string>();
        var node = _schema.Widget(type);
        if (!node.TryGetProperty("bind", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0);
    }

    private static string FmtOf(int decimals) => decimals < 0 ? "%g" : decimals == 0 ? "%d" : "%." + decimals + "f";

    /// <summary>
    /// 按变量类型选格式化串。运行时 build 绑定时会拒绝「fmt 含浮点转换、但变量是布尔/
    /// 窄整数」的绑定（整页建页失败），而面板的小数位默认是 2 —— 值显示控件绑到 i16
    /// 变量上导出就是 <c>%.2f</c>，板上表现为停在前一页/黑屏。
    /// </summary>
    private string FmtForTag(int tag, int decimals)
    {
        if (_maps.TypeByTag.TryGetValue(tag, out var type) && type is "i16" or "u16" or "i32" or "u32" or "i64" or "bool")
            return "%d";
        return FmtOf(decimals);
    }

    /// <summary>
    /// 输入框该用数字键盘还是文本键盘：看**绑定变量的类型**。
    /// 早先按属性名（"Value"）判，而编辑器里输入框绑的是 "Text" ⇒ 永远判成 text，
    /// 数字框导出后弹的是文本键盘（运行时的 KEYPAD_NUM 分支根本没走到）。
    /// </summary>
    private bool IsNumericBinding(HmiControl control)
    {
        var b = control.VariableBindings.FirstOrDefault(x =>
            x.PropertyName is "Text" or "text" or "Value" or "value");
        if (b == null) return false;
        if (!_maps.TagByVariableId.TryGetValue(b.VariableId, out var tag)) return false;
        return _maps.TypeByTag.TryGetValue(tag, out var type) && type is not ("string" or "bool");
    }
}

/// <summary>表达式改写期的内部错误（与语法错误区分：前者是我们的问题，后者是用户输入）。</summary>
internal sealed class ChengeExpressionException : Exception
{
    public ChengeExpressionException(string message) : base(message)
    {
    }
}
