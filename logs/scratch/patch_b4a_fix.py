KINDS = """arc arrow arrow_thick arrow_thin ball3d callout circle cone3d container cross cube
diamond ellipse hexagon line octagon parallelogram pie pipe_elbow pipe_h pipe_joint pipe_v
plane_concave plane_convex plane_groove polygon polyline rect right_triangle round_rect star
star5 tank torus3d trapezoid triangle valve_ball valve_butterfly valve_gate wedge""".split()

p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()
arr = ',\n            '.join('"%s"' % k for k in KINDS)
helper = '''    /// <summary>
    /// 契约 figures 的 40 个 kind（与 spec/widget-schema.json 一一对应）。
    /// 少一个会让那个图元"选得到但导不出去"，多一个会被运行时拒绝 —— 两边都难查，
    /// 所以集中在这里，配合测试里的数量断言。
    /// </summary>
    private static readonly string[] ShapeKinds =
        {
            %s
        };

''' % arr

anchor = '    /// <summary>按行切分（去空行、trim）。</summary>'
if 'private static readonly string[] ShapeKinds' not in s:
    s = s.replace(anchor, helper + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('inserted:', 'private static readonly string[] ShapeKinds' in s)
