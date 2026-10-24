p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()

# 1) TargetType
a = '            HmiControlType.HistoryCurve => "history_curve",'
b = a + '\n            HmiControlType.RealtimeCurve => "realtime_curve",'
if 'RealtimeCurve => "realtime_curve"' not in s:
    s = s.replace(a, b, 1)

# 2) MapControl 分派
a = '''            case HmiControlType.HistoryCurve:
                MapHistoryCurve(props, control, where);
                break;'''
b = a + '''

            case HmiControlType.RealtimeCurve:
                MapRealtimeCurve(props, control, where);
                break;'''
if 'MapRealtimeCurve' not in s:
    s = s.replace(a, b, 1)

# 3) 把 MapHistoryCurve 的外观段抽成 MapCurveCommon，并新增 MapRealtimeCurve
old_start = s.index('        props["group"] = control.TrendGroup!.Trim();')
old_end = s.index('        _features.Add("curves");\n    }', old_start) + len('        _features.Add("curves");\n    }')
old_block = s[old_start:old_end]

new_block = '''        props["group"] = control.TrendGroup!.Trim();
        var lastMin = control.CurveRangeLastMin > 0 ? control.CurveRangeLastMin : (int)control.SpanMin;
        props["range"] = new JsonObject { ["last_min"] = Clamp(lastMin, 1, 1440) };
        props["refresh_ms"] = Clamp(control.RefreshMs, 0, 60000);
        props["curves"] = curves;
        MapCurveCommon(props, control, where);
        _features.Add("curves");
    }

    /// <summary>
    /// 实时曲线：数据来自 sim 采样（`sample_ms` 一条），不读 trends.json，
    /// 所以没有 group / range / refresh_ms，取而代之的是 sample_ms 与 history_s。
    /// </summary>
    private void MapRealtimeCurve(JsonObject props, HmiControl control, string where)
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
            _issues.Error("realtime_curve.curves", where, "实时曲线至少要有一条通道（37：curves 1..4 条）");
            return;
        }

        props["curves"] = curves;
        props["sample_ms"] = Clamp(control.SampleMs is >= 100 and <= 60000 ? control.SampleMs : 1000, 100, 60000);
        if (control.HistoryS > 0) props["history_s"] = Clamp(control.HistoryS, 1, 86400);
        props["span_s"] = Clamp((int)control.SpanMin * 60, 10, 86400);
        MapCurveCommon(props, control, where);
        _features.Add("curves");
    }

    /// <summary>
    /// 两条曲线共用的外观与刻度（契约里 20 余项，运行时 build_curve 全读）。
    /// 抽出来的目的只有一个：history 与 realtime 不能各写一份而慢慢漂移。
    /// </summary>
    private void MapCurveCommon(JsonObject props, HmiControl control, string where)
    {
        props["y_min"] = (int)Math.Round(control.MinValue);
        props["y_max"] = (int)Math.Round(control.MaxValue);
        props["grid"] = control.CurveGrid;
        props["legend"] = control.CurveLegend;
        props["show_axis"] = control.CurveShowAxis;
        props["show_ticks"] = control.ShowTicks;
        props["show_labels"] = control.CurveShowLabels;
        props["direction"] = string.Equals(control.CurveDirection, "rl", StringComparison.OrdinalIgnoreCase) ? "rl" : "lr";
        props["x_format"] = string.Equals(control.CurveXFormat, "rel", StringComparison.OrdinalIgnoreCase) ? "rel" : "clock";
        props["x_div"] = Clamp(control.CurveXDiv, 1, 12);
        props["x_minor"] = Clamp(control.CurveXMinor, 0, 12);
        props["x_label_step"] = Clamp(control.CurveXLabelStep, 0, 12);
        props["y_div"] = Clamp(control.CurveYDiv, 1, 12);
        props["y_minor"] = Clamp(control.CurveYMinor, 0, 12);
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
        SetFont(props, control, DefaultFont(14));
    }'''

s = s[:old_start] + new_block + s[old_end:]
open(p, 'w', encoding='utf-8').write(s)
print('export ok')
