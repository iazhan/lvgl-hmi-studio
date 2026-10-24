# ---------- 枚举 ----------
p = 'src/HmiEditor.Core/Models/HmiControlType.cs'
s = open(p, encoding='utf-8').read()
a = '    Shape,         // 通用图元（kind 在属性里选，覆盖契约 40 种）'
b = a + ''',
    Chart,         // 图表（柱/饼/环，多序列）
    XyCurve,       // X-Y 散点/连线
    HistoryTable,  // 历史数据表
    RecipeTable,   // 配方表
    AsciiDisplay   // ASCII 串显示（GBK/ASCII 解码）'''
if 'Chart,' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 模型 ----------
p = 'src/HmiEditor.Core/Models/HmiControl.cs'
s = open(p, encoding='utf-8').read()
a = '''    /// <summary>
    /// 图元种类（契约 figures 的 40 个 kind 之一，如 rect / star5 / valve_ball / arrow）。
    /// 一个控件 + 一个下拉，替代"每种图元各做一个控件类型"——契约里 kind 本来就是
    /// shape 的属性，40 个枚举成员只会让工具箱变得不可用。
    /// </summary>
    public string? ShapeKind { get; set; } = "rect";'''
b = a + '''

    // ── 批次 5：图表 / XY 曲线 / 历史表 / 配方表 / ASCII 显示 ──

    /// <summary>Chart：柱 / 饼 / 环（bar / pie / donut）。</summary>
    public string? ChartKind { get; set; } = "bar";

    /// <summary>Chart：序列，每行 <c>变量Id:标签</c>（最多 8 条，颜色按主题调色板分配）。</summary>
    public string? ChartSeries { get; set; }

    /// <summary>Chart：显示图例。</summary>
    public bool ChartShowLegend { get; set; } = true;

    /// <summary>Chart：在图上显示数值。</summary>
    public bool ChartShowValue { get; set; } = true;

    /// <summary>XyCurve：X 值的变量 Id。</summary>
    public string? XyTagX { get; set; }

    /// <summary>XyCurve：Y 值的变量 Id。</summary>
    public string? XyTagY { get; set; }

    /// <summary>XyCurve：画点。</summary>
    public bool XyDot { get; set; } = true;

    /// <summary>XyCurve：连成线。</summary>
    public bool XyLine { get; set; }

    /// <summary>HistoryTable：列定义，每行 <c>键:标签[:宽]</c>（键取 time/level/name/value/text/state/ack 或变量 Id）。</summary>
    public string? HistTableColumns { get; set; }

    /// <summary>HistoryTable：趋势组（savecfg 组名）。</summary>
    public string? HistTableGroup { get; set; }

    /// <summary>HistoryTable：行高（0 = 默认 26）。</summary>
    public int HistTableRowHeight { get; set; }

    /// <summary>HistoryTable：右侧滚动条。</summary>
    public bool HistTableScrollbar { get; set; } = true;

    /// <summary>RecipeTable：列定义，每行 <c>键:标签[:宽]</c>（键取 name/desc/成员序号）。</summary>
    public string? RecipeColumns { get; set; }

    /// <summary>RecipeTable：配方组名。</summary>
    public string? RecipeGroup { get; set; }

    /// <summary>RecipeTable：显示单位。</summary>
    public bool RecipeShowUnit { get; set; } = true;

    /// <summary>AsciiDisplay：解码方式（gbk / ascii）。</summary>
    public string? AsciiEncoding { get; set; } = "gbk";

    /// <summary>AsciiDisplay：最多显示字符数。</summary>
    public int AsciiMaxLen { get; set; } = 32;'''
if 'ChartKind' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- Lang / 显示名 ----------
p = 'src/HmiEditor.App/Resources/Lang.cs'
s = open(p, encoding='utf-8').read()
a = '    public const string TbShapeKind = "tb.shape";'
b = a + '''
    public const string TbChart = "tb.chart";
    public const string TbXyCurve = "tb.xycurve";
    public const string TbHistoryTable = "tb.historytable";
    public const string TbRecipeTable = "tb.recipetable";
    public const string TbAsciiDisplay = "tb.asciidisplay";'''
if 'TbChart' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Services/ControlTypeTextKeys.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Shape => AppLocalization.Service[Lang.TbShapeKind],'
b = a + '''
        HmiControlType.Chart => AppLocalization.Service[Lang.TbChart],
        HmiControlType.XyCurve => AppLocalization.Service[Lang.TbXyCurve],
        HmiControlType.HistoryTable => AppLocalization.Service[Lang.TbHistoryTable],
        HmiControlType.RecipeTable => AppLocalization.Service[Lang.TbRecipeTable],
        HmiControlType.AsciiDisplay => AppLocalization.Service[Lang.TbAsciiDisplay],'''
if 'HmiControlType.Chart =>' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 工具箱 ----------
p = 'src/HmiEditor.App/Views/ToolboxView.axaml.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Shape => "M12,3 L21,20 L3,20 Z",   // 通用图元：三角形'
b = a + '''
        HmiControlType.Chart => "M4,20 L4,4 M4,20 L21,20 M7,20 L7,12 M11,20 L11,8 M15,20 L15,14 M19,20 L19,6",   // 图表：柱
        HmiControlType.XyCurve => "M4,20 L4,4 M4,20 L21,20 M7,17 L11,9 L15,13 L20,5",   // XY：散点折线
        HmiControlType.HistoryTable => "M3,5 L21,5 M3,10 L21,10 M3,15 L21,15 M3,20 L21,20 M9,5 L9,20",   // 历史表：表格
        HmiControlType.RecipeTable => "M3,5 L21,5 M3,10 L21,10 M3,15 L21,15 M3,20 L21,20 M15,5 L15,20",   // 配方表
        HmiControlType.AsciiDisplay => "M4,7 L8,16 L12,7 M6,13 L10,13 M14,7 L14,16 L18,16 M14,12 L17,12",   // ASCII：字母 A'''
if 'HmiControlType.Chart =>' not in s:
    s = s.replace(a, b, 1)
a2 = '        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.Shape)), Services.ControlTypeTextKeys.Describe(HmiControlType.Shape), AppLocalization.Service[Lang.TbCatDraw], HmiControlType.Shape));'
b2 = a2 + '''
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.Chart)), Services.ControlTypeTextKeys.Describe(HmiControlType.Chart), AppLocalization.Service[Lang.TbCatChart], HmiControlType.Chart));
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.XyCurve)), Services.ControlTypeTextKeys.Describe(HmiControlType.XyCurve), AppLocalization.Service[Lang.TbCatChart], HmiControlType.XyCurve));
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.HistoryTable)), Services.ControlTypeTextKeys.Describe(HmiControlType.HistoryTable), AppLocalization.Service[Lang.TbCatChart], HmiControlType.HistoryTable));
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.RecipeTable)), Services.ControlTypeTextKeys.Describe(HmiControlType.RecipeTable), AppLocalization.Service[Lang.TbCatSpecial], HmiControlType.RecipeTable));
        _allItems.Add(new(StreamGeometry.Parse(ControlIcon(HmiControlType.AsciiDisplay)), Services.ControlTypeTextKeys.Describe(HmiControlType.AsciiDisplay), AppLocalization.Service[Lang.TbCatDisplay], HmiControlType.AsciiDisplay));'''
if 'ControlType.Chart)),' not in s:
    s = s.replace(a2, b2, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 画布 + 尺寸 ----------
p = 'src/HmiEditor.App/Controls/DesignCanvas.cs'
s = open(p, encoding='utf-8').read()
a = '''            case HmiControlType.Shape:
                RenderDataWidgetPlaceholder(context, control, rect, z, control.ShapeKind ?? "rect");
                return;'''
b = a + '''
            case HmiControlType.Chart:
                RenderDataWidgetPlaceholder(context, control, rect, z, control.ChartKind ?? "bar");
                return;
            case HmiControlType.XyCurve:
                RenderDataWidgetPlaceholder(context, control, rect, z,
                    $"{control.XyTagX} / {control.XyTagY}");
                return;
            case HmiControlType.HistoryTable:
                RenderDataWidgetPlaceholder(context, control, rect, z, control.HistTableGroup ?? "history");
                return;
            case HmiControlType.RecipeTable:
                RenderDataWidgetPlaceholder(context, control, rect, z, control.RecipeGroup ?? "recipe");
                return;
            case HmiControlType.AsciiDisplay:
                RenderDataWidgetPlaceholder(context, control, rect, z, control.AsciiEncoding ?? "gbk");
                return;'''
if 'HmiControlType.Chart:' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'src/HmiEditor.App/Controls/DesignCanvas.Editing.cs'
s = open(p, encoding='utf-8').read()
a = '        HmiControlType.Shape => (120.0, 120.0),'
b = a + '''
        HmiControlType.Chart => (320.0, 200.0),
        HmiControlType.XyCurve => (400.0, 180.0),
        HmiControlType.HistoryTable => (400.0, 180.0),
        HmiControlType.RecipeTable => (400.0, 180.0),
        HmiControlType.AsciiDisplay => (200.0, 32.0),'''
if 'HmiControlType.Chart =>' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)
print('b5a part1 ok')
