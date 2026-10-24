KINDS = """arc arrow arrow_thick arrow_thin ball3d callout circle cone3d container cross cube
diamond ellipse hexagon line octagon parallelogram pie pipe_elbow pipe_h pipe_joint pipe_v
plane_concave plane_convex plane_groove polygon polyline rect right_triangle round_rect star
star5 tank torus3d trapezoid triangle valve_ball valve_butterfly valve_gate wedge""".split()

# ---------- 1) 枚举 ----------
p = 'src/HmiEditor.Core/Models/HmiControlType.cs'
s = open(p, encoding='utf-8').read()
a = '    FrameAnim,     // 帧动画'
b = a + ''',
    Shape,         // 通用图元（kind 在属性里选，覆盖契约 40 种）'''
if 'Shape,' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 2) 模型 ----------
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
a = '''    /// <summary>FrameAnim：循环播放。</summary>
    public bool FrameLoop { get; set; } = true;'''
b = a + '''

    // ── 批次 4：通用图元 ──

    /// <summary>
    /// 图元种类（契约 figures 的 40 个 kind 之一，如 rect / star5 / valve_ball / arrow）。
    /// 一个控件 + 一个下拉，替代"每种图元各做一个控件类型"——契约里 kind 本来就是
    /// shape 的属性，40 个枚举成员只会让工具箱变得不可用。
    /// </summary>
    public string? ShapeKind { get; set; } = "rect";'''
if 'ShapeKind' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 3) Lang / 显示名 ----------
p = 'src/HmiEditor.App/Resources/Lang.cs'
s = open(p, encoding='utf-8').read()
a = '    public const string TbFrameAnim = "tb.frameanim";'
b = a + '\n    public const string TbShapeKind = "tb.shape";'
if 'TbShapeKind' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Services/ControlTypeTextKeys.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.FrameAnim => AppLocalization.Service[Lang.TbFrameAnim],'
b = a + '\n        HmiControlType.Shape => AppLocalization.Service[Lang.TbShapeKind],'
if 'HmiControlType.Shape =>' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 4) 工具箱 ----------
p = 'src/HmiEditor.App/Views/ToolboxView.axaml.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.FrameAnim => "M4,6 L8,6 L8,18 L4,18 Z M10,6 L14,6 L14,18 L10,18 Z M16,6 L20,6 L20,18 L16,18 Z",   // 帧动画：三格'
b = a + '\n        HmiControlType.Shape => "M12,3 L21,20 L3,20 Z",   // 通用图元：三角形'
if 'HmiControlType.Shape =>' not in s:
    s = s.replace(a, b, 1)
a2 = '        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.FrameAnim)), Services.ControlTypeTextKeys.Describe(HmiControlType.FrameAnim), AppLocalization.Service[Lang.TbCatDisplay], HmiControlType.FrameAnim));'
b2 = a2 + '\n        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.Shape)), Services.ControlTypeTextKeys.Describe(HmiControlType.Shape), AppLocalization.Service[Lang.TbCatDraw], HmiControlType.Shape));'
if 'ControlType.Shape)),' not in s:
    s = s.replace(a2, b2, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 5) 画布 + 尺寸 ----------
p = 'src/HmiEditor.App/Controls/DesignCanvas.cs'
s = open(p, encoding='utf-8').read()
a = '''            case HmiControlType.FrameAnim:
                RenderDataWidgetPlaceholder(context, control, rect, z, $"frames · {control.FrameIntervalMs}ms");
                return;'''
b = a + '''
            case HmiControlType.Shape:
                RenderDataWidgetPlaceholder(context, control, rect, z, control.ShapeKind ?? "rect");
                return;'''
if 'HmiControlType.Shape:' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Controls/DesignCanvas.Editing.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.FrameAnim => (120.0, 120.0),'
b = a + '\n        HmiControlType.Shape => (120.0, 120.0),'
if 'HmiControlType.Shape =>' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 6) 导出 ----------
p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()
a = '            HmiControlType.FrameAnim => "frame_anim",'
b = a + '\n            HmiControlType.Shape => "shape",'
if 'HmiControlType.Shape => "shape"' not in s:
    s = s.replace(a, b, 1)

a2 = '''            case HmiControlType.Message:'''
b2 = '''            case HmiControlType.Shape:
                // 40 个 kind 任一非法都拒绝整页，所以先过白名单（未知值回落 rect 并告警）
                kind = OneOf(control.ShapeKind, ShapeKinds, "rect", where, "shape.kind");
                MapShapeCommon(props, control, where);
                if (kind is "polygon" or "polyline")
                    props["points"] = PointsOf(control, where);
                break;

            case HmiControlType.Message:'''
if 'case HmiControlType.Shape:' not in s:
    s = s.replace(a2, b2, 1)

# kind 白名单（与 spec/widget-schema.json 的 figures 一致）
arr = ',\n            '.join('"%s"' % k for k in KINDS)
helper = '''    /// <summary>
    /// 契约 figures 的 40 个 kind（与 spec/widget-schema.json 一一对应）。
    /// 少一个就会让那个图元"选得到但导不出去"，多一个会被运行时拒绝——两边都难查，
    /// 所以集中在这里，配合测试里的数量断言。
    /// </summary>
    private static readonly string[] ShapeKinds =
        {
            %s
        };

''' % arr
if 'ShapeKinds' not in s:
    s = s.replace('    /// <summary>按行切分（去空行、trim）。</summary>',
                  helper + '    /// <summary>按行切分（去空行、trim）。</summary>', 1)
open(p, 'w', encoding='utf-8').write(s)
print('b4a ok, kinds =', len(KINDS))
