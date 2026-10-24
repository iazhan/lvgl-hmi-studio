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
p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()
anchor = '    /// <summary>按行切分（去空行、trim）。</summary>'
if 'private JsonArray ChartSeriesOf' not in s:
    s = s.replace(anchor, helpers + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('inserted:', 'private JsonArray ChartSeriesOf' in s)
