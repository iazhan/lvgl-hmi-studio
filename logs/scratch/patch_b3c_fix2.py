p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()

helpers = '''    /// <summary>按行切分（去空行、trim）。</summary>
    private static IEnumerable<string> SplitLines(string? text)
        => (text ?? "").Replace("\\r", "").Split('\\n')
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

'''
anchor = '    /// <summary>滚轮的可见行数必须是奇数（选中行才在正中）。</summary>'
if 'private JsonArray ParseAssetPairs' not in s:
    s = s.replace(anchor, helpers + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('inserted:', 'private JsonArray ParseAssetPairs' in s)
