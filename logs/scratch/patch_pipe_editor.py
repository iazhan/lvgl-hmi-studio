# ---------- 1) 模型：管壁厚度 / 通道色 ----------
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
a = '''    /// <summary>流向动画周期 ms（Pipe，0=静态无动画）。默认 2000。</summary>
    public int PipeFlowSpeed { get; set; } = 2000;'''
b = a + '''

    /// <summary>Pipe：管壁厚度比例（契约 wall，0.05~0.45，默认 0.2）。</summary>
    public double PipeWall { get; set; } = 0.2;

    /// <summary>Pipe：通道底色（契约 channel_color，空 = 用流动色的浅色版）。</summary>
    public string? PipeChannelColor { get; set; }'''
if 'PipeWall' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 2) 导出：pipe 是**控件**（W_PIPE），不再走 shape ----------
p = 'src/HmiEditor.Export.Chenge/ChengeWidgetMapper.cs'
s = open(p, encoding='utf-8').read()

a = '''            HmiControlType.Rectangle or HmiControlType.Ellipse or HmiControlType.Line
                or HmiControlType.Polyline or HmiControlType.Arc or HmiControlType.Tank
                or HmiControlType.Pipe => "shape",'''
b = '''            HmiControlType.Rectangle or HmiControlType.Ellipse or HmiControlType.Line
                or HmiControlType.Polyline or HmiControlType.Arc or HmiControlType.Tank => "shape",
            // pipe 在契约里是**控件**（widgets.pipe，带 direction/wall/color/channel_color/flow_ms），
            // 运行时也有 W_PIPE 分派（build_pipe 带流动动画）；早先导成 shape 的 pipe_h/v
            // 白丢了这 5 个属性（图元版只读 wall，且不支持流动）。
            HmiControlType.Pipe => "pipe",'''
if 'HmiControlType.Pipe => "pipe"' not in s:
    s = s.replace(a, b, 1)

a2 = '''            case HmiControlType.Pipe:
                kind = string.Equals(control.PipeDirection, "Vertical", StringComparison.OrdinalIgnoreCase)
                    ? "pipe_v" : "pipe_h";
                props["fill_color"] = _colors.ResolveOr(control.BgColor, _colors.Border, where);
                props["border_color"] = _colors.ResolveOr(control.BorderColor, _colors.Border, where);
                break;'''
b2 = '''            case HmiControlType.Pipe:
                props["direction"] = string.Equals(control.PipeDirection, "Vertical", StringComparison.OrdinalIgnoreCase)
                    ? "v" : "h";
                props["wall"] = Math.Round(Math.Clamp(control.PipeWall is >= 0.05 and <= 0.45 ? control.PipeWall : 0.2, 0.05, 0.45), 3);
                props["color"] = _colors.ResolveOr(control.IndicatorColor ?? control.BgColor, "#4EA1FF", where);
                if (!string.IsNullOrWhiteSpace(control.PipeChannelColor) &&
                    _colors.Resolve(control.PipeChannelColor, where) is { } chan)
                    props["channel_color"] = chan;
                // flow_ms 是"一个块走完全程"的毫秒数；0 = 静态（契约下限 40，用 0 表示不流动）
                props["flow_ms"] = control.PipeFlowSpeed > 0
                    ? Clamp(control.PipeFlowSpeed, 40, 2000)
                    : 0;
                break;'''
if 'props["channel_color"] = chan' not in s:
    s = s.replace(a2, b2, 1)

open(p, 'w', encoding='utf-8').write(s)
print('editor pipe ok')
