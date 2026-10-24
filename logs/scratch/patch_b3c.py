# ---------- 1) 枚举 ----------
p = 'src/HmiEditor.Core/Models/HmiControlType.cs'
s = open(p, encoding='utf-8').read()
a = '    Roller,        // 滚轮选择'
b = a + ''',
    StateImage,    // 值 → 图片
    FrameAnim,     // 帧动画'''
if 'StateImage' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 2) 模型 ----------
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
a = '''    /// <summary>Roller：可见行数（契约要求奇数 3..7）。</summary>
    public int RollerVisibleRows { get; set; } = 3;'''
b = a + '''

    // ── 批次 3：值换图 / 帧动画 ──

    /// <summary>StateImage：值 → 图片的映射，每行 <c>值:资源Id</c>（1..16 项，值不可重复）。</summary>
    public string? StateImages { get; set; }

    /// <summary>FrameAnim：帧图片资源 Id（每行一个，按顺序播放）。</summary>
    public string? FrameImages { get; set; }

    /// <summary>FrameAnim：每帧时长毫秒（契约默认 120）。</summary>
    public int FrameIntervalMs { get; set; } = 120;

    /// <summary>FrameAnim：循环播放。</summary>
    public bool FrameLoop { get; set; } = true;'''
if 'StateImages' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 3) Lang / 显示名 ----------
p = 'src/HmiEditor.App/Resources/Lang.cs'
s = open(p, encoding='utf-8').read()
a = '    public const string TbRoller = "tb.roller";'
b = a + '\n    public const string TbStateImage = "tb.stateimage";\n    public const string TbFrameAnim = "tb.frameanim";'
if 'TbStateImage' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Services/ControlTypeTextKeys.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Roller => AppLocalization.Service[Lang.TbRoller],'
b = a + '\n        HmiControlType.StateImage => AppLocalization.Service[Lang.TbStateImage],\n        HmiControlType.FrameAnim => AppLocalization.Service[Lang.TbFrameAnim],'
if 'HmiControlType.StateImage' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 4) 工具箱 ----------
p = 'src/HmiEditor.App/Views/ToolboxView.axaml.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Roller => "M5,5 L19,5 M5,12 L19,12 M5,19 L19,19 M8,8 L8,10 M8,15 L8,17",   // 滚轮：多条+选中线'
b = a + '''
        HmiControlType.StateImage => "M3,4 L21,4 L21,20 L3,20 Z M3,16 L9,10 L13,14 L17,8 L21,12",   // 值换图：图片框+山
        HmiControlType.FrameAnim => "M4,6 L8,6 L8,18 L4,18 Z M10,6 L14,6 L14,18 L10,18 Z M16,6 L20,6 L20,18 L16,18 Z",   // 帧动画：三格'''
if 'HmiControlType.StateImage =>' not in s:
    s = s.replace(a, b, 1)
a2 = '        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.Roller)), Services.ControlTypeTextKeys.Describe(HmiControlType.Roller), AppLocalization.Service[Lang.TbCatInput], HmiControlType.Roller));'
b2 = a2 + '''
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.StateImage)), Services.ControlTypeTextKeys.Describe(HmiControlType.StateImage), AppLocalization.Service[Lang.TbCatDisplay], HmiControlType.StateImage));
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.FrameAnim)), Services.ControlTypeTextKeys.Describe(HmiControlType.FrameAnim), AppLocalization.Service[Lang.TbCatDisplay], HmiControlType.FrameAnim));'''
if 'ControlType.StateImage)),' not in s:
    s = s.replace(a2, b2, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 5) 画布 + 尺寸 ----------
p = 'src/HmiEditor.App/Controls/DesignCanvas.cs'
s = open(p, encoding='utf-8').read()
a = '''            case HmiControlType.Roller:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    control.RollerOptions?.Split('\\n').FirstOrDefault() ?? "roller");
                return;'''
b = a + '''
            case HmiControlType.StateImage:
                RenderDataWidgetPlaceholder(context, control, rect, z, "state");
                return;
            case HmiControlType.FrameAnim:
                RenderDataWidgetPlaceholder(context, control, rect, z, $"frames · {control.FrameIntervalMs}ms");
                return;'''
if 'HmiControlType.StateImage:' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Controls/DesignCanvas.Editing.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Roller => (160.0, 120.0),'
b = a + '\n        HmiControlType.StateImage => (120.0, 120.0),\n        HmiControlType.FrameAnim => (120.0, 120.0),'
if 'HmiControlType.StateImage =>' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 6) 导出 ----------
p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()
a = '            HmiControlType.Roller => "roller",'
b = a + '\n            HmiControlType.StateImage => "state_image",\n            HmiControlType.FrameAnim => "frame_anim",'
if 'HmiControlType.StateImage => "state_image"' not in s:
    s = s.replace(a, b, 1)

a2 = '''            case HmiControlType.Message:'''
b2 = '''            case HmiControlType.StateImage:
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
                    var src = _assets.SrcFor(assetId, where);
                    if (src == null)
                    {
                        _issues.Warn("frame_anim.frame", where, $"帧动画的图片 {assetId} 导不出来，已跳过这一帧");
                        continue;
                    }
                    frames.Add(src);
                }
                if (frames.Count == 0)
                {
                    _issues.Error("frame_anim.frames", where, "帧动画没有可用的帧图片（先导入图片资产）");
                    return null;
                }
                props["frames"] = frames;
                props["interval_ms"] = Clamp(control.FrameIntervalMs, 20, 5000);
                props["loop"] = control.FrameLoop;
                _features.Add("widgets_ext");
                break;

            case HmiControlType.Message:'''
if 'case HmiControlType.StateImage:' not in s:
    s = s.replace(a2, b2, 1)

# 两个辅助：按行切分 / 解析 "值:资源Id"
helpers = '''    /// <summary>按行切分（去空行、trim）。</summary>
    private static IEnumerable<string> SplitLines(string? text)
        => (text ?? "").Replace("\\r", "").Split('\\n')
            .Select(s => s.Trim()).Where(s => s.Length > 0);

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
            var src = _assets.SrcFor(line[(idx + 1)..].Trim(), where);
            if (src == null)
            {
                _issues.Warn(code + ".asset", where, $"「{line}」的图片导不出来，已跳过");
                continue;
            }
            arr.Add(new JsonObject { ["v"] = v, ["src"] = src });
        }
        return arr;
    }

'''
if 'ParseAssetPairs' not in s:
    s = s.replace('    /// <summary>滚轮的可见行数必须是奇数（选中行才在正中）。</summary>',
                  helpers + '    /// <summary>滚轮的可见行数必须是奇数（选中行才在正中）。</summary>', 1)
open(p, 'w', encoding='utf-8').write(s)
print('b3c ok')
