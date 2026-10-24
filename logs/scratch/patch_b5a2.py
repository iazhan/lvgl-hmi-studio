p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()

# ---------- TargetType ----------
a = '            HmiControlType.Shape => "shape",'
b = a + '''
            HmiControlType.Chart => "chart",
            HmiControlType.XyCurve => "xy_curve",
            HmiControlType.HistoryTable => "history_table",
            HmiControlType.RecipeTable => "recipe_table",
            HmiControlType.AsciiDisplay => "ascii_display",'''
if 'HmiControlType.Chart => "chart"' not in s:
    s = s.replace(a, b, 1)

# ---------- 分派 + 实现 ----------
a2 = '''            case HmiControlType.Message:'''
b2 = '''            case HmiControlType.Chart:
                props["kind"] = OneOf(control.ChartKind, "bar", "pie", "donut", "bar", where, "chart.kind");
                var series = ChartSeriesOf(control, where);
                if (series.Count == 0)
                {
                    _issues.Error("chart.series", where, "图表至少要有一条序列（先在系列里填 变量Id:标签）");
                    return null;
                }
                props["series"] = series;
                props["show_legend"] = control.ChartShowLegend;
                props["show_value"] = control.ChartShowValue;
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
                    _issues.Error("xy_curve.tags", where, "X-Y 曲线要同时指定 X 与 Y 的变量");
                    return null;
                }
                props["x"] = new JsonObject { ["tag"] = tagX.Value };
                props["y"] = new JsonObject { ["tag"] = tagY.Value };
                props["dot"] = control.XyDot;
                props["line"] = control.XyLine;
                props["sample_ms"] = Clamp(control.SampleMs is >= 100 and <= 60000 ? control.SampleMs : 1000, 100, 60000);
                props["span_s"] = Clamp((int)control.SpanMin * 60, 10, 86400);
                props["x_min"] = (int)Math.Round(control.MinValue);
                props["x_max"] = (int)Math.Round(control.MaxValue);
                props["y_min"] = (int)Math.Round(control.MinValue);
                props["y_max"] = (int)Math.Round(control.MaxValue);
                props["x_div"] = Clamp(control.CurveXDiv, 1, 12);
                props["y_div"] = Clamp(control.CurveYDiv, 1, 12);
                props["x_minor"] = Clamp(control.CurveXMinor, 0, 12);
                props["y_minor"] = Clamp(control.CurveYMinor, 0, 12);
                props["x_decimals"] = Clamp(control.CurveXLabelStep is >= 0 ? control.CurveYDecimals : 0, 0, 6);
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
                props["max_rows"] = Clamp(control.AlarmMaxRows, 10, 500);
                props["refresh_ms"] = Clamp(control.RefreshMs, 0, 60000);
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
                props["row_height"] = control.HistTableRowHeight > 0 ? Clamp(control.HistTableRowHeight, 16, 60) : 26;
                props["scrollbar"] = control.HistTableScrollbar;
                props["show_unit"] = control.RecipeShowUnit;
                SetFont(props, control, DefaultFont(14));
                _features.Add("recipe");
                break;

            case HmiControlType.AsciiDisplay:
                props["encoding"] = OneOf(control.AsciiEncoding, "gbk", "ascii", "gbk", where, "ascii_display.encoding");
                props["max_len"] = Clamp(control.AsciiMaxLen, 1, 64);
                props["color"] = _colors.ResolveOr(control.TextColor, _colors.TextDefault, where);
                props["align"] = Align(control.TextAlign);
                props["valign"] = VAlign(control.TextVAlign);
                SetFont(props, control, DefaultFont(16));
                _features.Add("widgets_ext");
                break;

            case HmiControlType.Message:'''
if 'case HmiControlType.Chart:' not in s:
    s = s.replace(a2, b2, 1)

# ---------- 三个辅助 ----------
helpers = '''    /// <summary>图表序列：每行 <c>变量Id:标签</c> → <c>[{label,value:{tag},color}]</c>（最多 8 条）。</summary>
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
        var valid = history
            ? new[] { "time", "level", "name", "value", "text", "state", "ack" }
            : new[] { "name", "desc" };
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
            var col = new JsonObject { ["key"] = isEnumKey ? key : (history ? "tag" : key), ["label"] = label };
            if (!isEnumKey && history) col["tag"] = _maps.ResolveTag(key)!.Value;
            if (parts.Length > 2 && int.TryParse(parts[2], out var w)) col["w"] = Clamp(w, 20, 400);
            arr.Add(col);
        }
        return arr.Count > 0 ? arr : null;
    }

'''
if 'ChartSeriesOf' not in s:
    s = s.replace('    /// <summary>按行切分（去空行、trim）。</summary>', helpers + '    /// <summary>按行切分（去空行、trim）。</summary>', 1)
open(p, 'w', encoding='utf-8').write(s)
print('b5a part2 ok')
