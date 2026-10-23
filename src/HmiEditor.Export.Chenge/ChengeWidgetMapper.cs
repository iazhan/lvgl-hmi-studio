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

    public ChengeWidgetMapper(ChengeSchema schema, ChengeIssueBag issues, ChengeColors colors,
        ChengeAssets assets, ChengeMaps maps, HashSet<string> features, int screenWidth, int screenHeight)
    {
        _schema = schema;
        _issues = issues;
        _colors = colors;
        _assets = assets;
        _maps = maps;
        _features = features;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    public JsonObject? Map(HmiControl control, string pageId, string widgetId)
    {
        var where = $"pages/{pageId}.json#{widgetId}";
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
                break;

            case HmiControlType.Value:
                props["decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                var unit = string.IsNullOrWhiteSpace(control.Unit) ? control.ValueSuffix : control.Unit;
                if (!string.IsNullOrWhiteSpace(unit)) props["unit"] = unit.Trim();
                props["align"] = Align(control.TextAlign);
                SetFont(props, control, DefaultFont(20));
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                break;

            case HmiControlType.Button:
                props["text"] = control.Text ?? "";
                SetFont(props, control, DefaultFont(16));
                props["color"] = _colors.ResolveOr(control.TextColor, "#FFFFFF", where);
                AddCommonBox(props, control, where);
                AddPressed(props, control, where);
                break;

            case HmiControlType.Switch:
                props["color_on"] = _colors.ResolveOr(control.IndicatorColor, _colors.Success, where);
                props["color_off"] = _colors.ResolveOr(control.BgColor, _colors.Border, where);
                props["text_on"] = control.Text ?? "";
                props["text_off"] = "";
                break;

            case HmiControlType.CheckBox:
                props["text"] = control.Text ?? "";
                SetFont(props, control, DefaultFont(16));
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                props["checked"] = control.IsChecked;
                break;

            case HmiControlType.Led:
                props["color"] = _colors.ResolveOr(control.IndicatorColor ?? control.TextColor, _colors.Success, where);
                props["size"] = (int)Math.Round(Math.Min(control.Width, control.Height));
                break;

            case HmiControlType.Bar:
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["direction"] = "h";
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["show_percent"] = false;
                break;

            case HmiControlType.Slider:
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["step"] = control.StepValue;
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                break;

            case HmiControlType.Gauge:
                props["style"] = control.ShowPointer == true ? "pointer" : "arc";
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["ticks"] = control.ShowTicks ? Clamp(control.MajorTickCount, 2, 11) : 0;
                props["decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                props["show_value"] = true;
                break;

            case HmiControlType.Knob:
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["step"] = control.StepValue;
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                props["show_value"] = true;
                break;

            case HmiControlType.Dropdown:
                var options = (control.Options ?? "").Replace("\r", "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();
                if (options.Length == 0)
                {
                    _issues.Error("combobox.no_options", where,
                        "下拉框没有任何选项（37 §8：options 与 options_tag 至少要有一个）");
                }
                else
                {
                    var arr = new JsonArray();
                    foreach (var o in options) arr.Add(o);
                    props["options"] = arr;
                }
                SetFont(props, control, DefaultFont(16));
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                if (control.SelectedIndex > 0)
                    _issues.Warn("combobox.selected", where,
                        "下拉框的初始选中项无法导出：运行时读 props.selected，但 spec/widget-schema.json 的 combobox 没有该属性（等价于写了会整页拒绝）。用 options_tag 绑变量来定选中项");
                break;

            case HmiControlType.TextArea:
                props["mode"] = control.PasswordMode ? "password"
                    : IsNumericBinding(control) ? "num" : "text";
                props["min"] = (int)Math.Round(control.MinValue);
                props["max"] = (int)Math.Round(control.MaxValue);
                props["decimals"] = Clamp(control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0, 0, 6);
                props["max_len"] = control.MaxLength > 0 ? Clamp(control.MaxLength, 1, 64) : 64;
                SetFont(props, control, DefaultFont(16));
                props["align"] = Align(control.TextAlign);
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
                props["tab_pos"] = "top";
                break;

            case HmiControlType.Spinner:
                props["color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["speed_ms"] = Clamp(control.SpinSpeed, 100, 5000);
                break;

            case HmiControlType.Keyboard:
                _issues.Warn("keyboard.not_exported", where,
                    "键盘控件不导出：chenge-runtime 的 input 自带模态数字/文本键盘，独立键盘控件在目标里没有对应实现");
                return null;

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
                break;

            case HmiControlType.Polyline:
                kind = "polyline";
                props["points"] = PointsOf(control, where);
                props["border_color"] = _colors.ResolveOr(control.BgColor ?? control.BorderColor, _colors.Border, where);
                props["border_width"] = Math.Max(1, control.BorderWidth);
                break;

            case HmiControlType.Arc:
                kind = "arc";
                props["angle_start"] = (int)Math.Round(control.MinValue);
                props["angle_end"] = (int)Math.Round(control.MaxValue);
                props["border_color"] = _colors.ResolveOr(control.IndicatorColor, _colors.Primary, where);
                props["border_width"] = Math.Max(1, control.BorderWidth);
                break;

            case HmiControlType.Tank:
                kind = "tank";
                MapShapeCommon(props, control, where);
                break;

            case HmiControlType.Pipe:
                kind = string.Equals(control.PipeDirection, "Vertical", StringComparison.OrdinalIgnoreCase)
                    ? "pipe_v" : "pipe_h";
                props["fill_color"] = _colors.ResolveOr(control.BgColor, _colors.Border, where);
                props["border_color"] = _colors.ResolveOr(control.BorderColor, _colors.Border, where);
                break;

            case HmiControlType.Valve:
                _issues.Error("valve.not_supported", where,
                    "阀门控件在目标契约里没有对应图元（37 的 37 种 shape.kind 里没有 valve_*）");
                return null;

            case HmiControlType.Svg:
                _issues.Error("svg.not_supported", where,
                    "SVG 控件暂不支持导出：运行时没有 SVG 解析器，多图元展平成一次性的 polygon 会丢语义（见 MAPPING-REPORT 的未支持清单）");
                return null;

            case HmiControlType.AlarmList:
                MapAlarmList(props, control, where);
                break;

            case HmiControlType.HistoryCurve:
                MapHistoryCurve(props, control, where);
                break;

            case HmiControlType.Screen:
                _issues.Warn("screen.not_exported", where, "嵌套屏幕控件不导出（页面切换用 goto_page 动作）");
                return null;

            default:
                _issues.Warn("widget.unmapped", where, $"控件类型 {control.Type} 没有导出映射，已跳过");
                return null;
        }

        AddCommon(props, control, where);

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

        CheckBounds(widget, control, where);
        AddBindings(widget, control, target, pageId, widgetId, where);
        AddEvents(widget, control, pageId, widgetId, where);
        if (!control.IsVisible) widget["props"]!["visible"] = false;
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
        props["ack_on"] = "click";
        props["blink"] = true;
        props["max_rows"] = Clamp(control.AlarmMaxRows, 10, 500);
        props["row_height"] = Clamp((int)Math.Round(control.Height / Math.Max(1, control.AlarmMaxRows)), 16, 60);
        SetFont(props, control, DefaultFont(14));
        _features.Add("alarm");
    }

    private void MapHistoryCurve(JsonObject props, HmiControl control, string where)
    {
        if (string.IsNullOrWhiteSpace(control.TrendGroup))
        {
            _issues.Error("history_curve.group", where,
                "历史曲线没有指定的采集组（37：group 必须是 trends.json 的 logger id 或 savecfg 的组名）");
            return;
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
            _issues.Error("history_curve.curves", where, "历史曲线至少要有一条通道（37：curves 1..4 条）");
            return;
        }

        props["group"] = control.TrendGroup!.Trim();
        props["range"] = new JsonObject { ["last_min"] = Clamp(control.SpanMin, 1, 1440) };
        props["refresh_ms"] = Clamp(control.RefreshMs, 0, 60000);
        props["curves"] = curves;
        props["y_min"] = (int)Math.Round(control.MinValue);
        props["y_max"] = (int)Math.Round(control.MaxValue);
        props["grid"] = true;
        props["legend"] = true;
        props["x_format"] = "clock";
        SetFont(props, control, DefaultFont(14));
        _features.Add("curves");
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
        SetFont(props, control, DefaultFont(14));

        var cells = new JsonArray();
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

    private void MapShapeCommon(JsonObject props, HmiControl control, string where)
    {
        props["fill_color"] = _colors.ResolveOr(control.BgColor, _colors.Surface, where);
        props["border_color"] = _colors.ResolveOr(control.BorderColor, _colors.Border, where);
        props["border_width"] = Math.Max(0, control.BorderWidth);
        if (control.Radius > 0) props["radius"] = control.Radius;
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

    private void AddCommon(JsonObject props, HmiControl control, string where)
    {
        var bg = _colors.Resolve(control.BgColor, where);
        if (bg != null) props["bg_color"] = bg;
        var border = _colors.Resolve(control.BorderColor, where);
        if (border != null) props["border_color"] = border;
        if (control.BorderWidth > 0) props["border_width"] = control.BorderWidth;
        if (control.Radius > 0) props["radius"] = control.Radius;
        if (control.Opacity < 100)
            props["opacity"] = Clamp((int)Math.Round(control.Opacity * 255 / 100), 0, 255);
        if (control.Rotation != 0)
            _issues.Warn("widget.rotation", where,
                $"控件的旋转 {control.Rotation}° 未导出：文本类只支持 0/90/180/270 且运行时对文本旋转有尺寸限制（37 未定义该属性），需要旋转请在目标里用图片");
        if (control.MirrorX || control.MirrorY)
            _issues.Warn("widget.mirror", where, "控件的镜像未导出（运行时没有 mirror_x/mirror_y 的实现）");
    }

    private void AddCommonBox(JsonObject props, HmiControl control, string where)
    {
        var bg = _colors.Resolve(control.BgColor, where);
        if (bg != null) props["bg_color"] = bg;
        var border = _colors.Resolve(control.BorderColor, where);
        if (border != null) props["border_color"] = border;
        if (control.BorderWidth > 0) props["border_width"] = control.BorderWidth;
        if (control.Radius > 0) props["radius"] = control.Radius;
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
                if (!string.IsNullOrEmpty(control.ValuePrefix) || !string.IsNullOrEmpty(control.ValueSuffix))
                {
                    var fmt = FmtOf(control.ValueDecimalPlaces);
                    widget["props"]!["text"] =
                        $"{control.ValuePrefix}{{tag:{tag.Value}|{fmt}}}{control.ValueSuffix}";
                    continue;
                }
                bind[prop] = new JsonObject { ["tag"] = tag.Value, ["kind"] = "fmt", ["fmt"] = FmtOf(control.ValueDecimalPlaces) };
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
        var target = type switch
        {
            HmiControlType.Label => "label",
            HmiControlType.Value => "value",
            HmiControlType.Button => "button",
            HmiControlType.Switch => "switch",
            HmiControlType.CheckBox => "checkbox",
            HmiControlType.Led => "lamp",
            HmiControlType.Bar => "bar",
            HmiControlType.Slider => "slider",
            HmiControlType.Gauge => "gauge",
            HmiControlType.Knob => "knob",
            HmiControlType.Dropdown => "combobox",
            HmiControlType.TextArea => "input",
            HmiControlType.Image => "image",
            HmiControlType.Table => "table",
            HmiControlType.TabView => "tabview",
            HmiControlType.Spinner => "spinner",
            HmiControlType.Keyboard => "keyboard",
            HmiControlType.Rectangle or HmiControlType.Ellipse or HmiControlType.Line
                or HmiControlType.Polyline or HmiControlType.Arc or HmiControlType.Tank
                or HmiControlType.Pipe => "shape",
            HmiControlType.Valve => null,
            HmiControlType.Svg => null,
            HmiControlType.AlarmList => "alarm_list",
            HmiControlType.HistoryCurve => "history_curve",
            HmiControlType.Screen => null,
            _ => null,
        };

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

    private void SetFont(JsonObject props, HmiControl control, int fallback)
    {
        var size = control.FontSize > 0 ? control.FontSize : fallback;
        props["font_size"] = Clamp(size, 10, 48);
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

    private static bool IsNumericBinding(HmiControl control)
        => control.VariableBindings.Any(b => b.PropertyName is "Value" or "value");
}

/// <summary>表达式改写期的内部错误（与语法错误区分：前者是我们的问题，后者是用户输入）。</summary>
internal sealed class ChengeExpressionException : Exception
{
    public ChengeExpressionException(string message) : base(message)
    {
    }
}
