# ---------- Changes.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Changes.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''            if (ShapeKindCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ShapeKind), GetComboTag(ShapeKindCombo), v => _currentControl.ShapeKind = v);'''
b = a + '''
            // 批次 5
            if (ChartKindCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ChartKind), GetComboTag(ChartKindCombo), v => _currentControl.ChartKind = v);
            if (ChartSeriesBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ChartSeries), string.IsNullOrWhiteSpace(ChartSeriesBox.Text) ? null : ChartSeriesBox.Text.Trim(), v => _currentControl.ChartSeries = v);
            if (ChartLegendCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ChartShowLegend), ChartLegendCheck.IsChecked == true, v => _currentControl.ChartShowLegend = v);
            if (ChartValueCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ChartShowValue), ChartValueCheck.IsChecked == true, v => _currentControl.ChartShowValue = v);
            if (XyTagXBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.XyTagX), string.IsNullOrWhiteSpace(XyTagXBox.Text) ? null : XyTagXBox.Text.Trim(), v => _currentControl.XyTagX = v);
            if (XyTagYBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.XyTagY), string.IsNullOrWhiteSpace(XyTagYBox.Text) ? null : XyTagYBox.Text.Trim(), v => _currentControl.XyTagY = v);
            if (XyDotCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.XyDot), XyDotCheck.IsChecked == true, v => _currentControl.XyDot = v);
            if (XyLineCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.XyLine), XyLineCheck.IsChecked == true, v => _currentControl.XyLine = v);
            if (HistColumnsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.HistTableColumns), string.IsNullOrWhiteSpace(HistColumnsBox.Text) ? null : HistColumnsBox.Text.Trim(), v => _currentControl.HistTableColumns = v);
            if (HistGroupBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.HistTableGroup), string.IsNullOrWhiteSpace(HistGroupBox.Text) ? null : HistGroupBox.Text.Trim(), v => _currentControl.HistTableGroup = v);
            if (HistRowHeightBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.HistTableRowHeight), (int)HistRowHeightBox.Value, v => _currentControl.HistTableRowHeight = v);
            if (HistScrollbarCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.HistTableScrollbar), HistScrollbarCheck.IsChecked == true, v => _currentControl.HistTableScrollbar = v);
            if (RecipeColumnsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.RecipeColumns), string.IsNullOrWhiteSpace(RecipeColumnsBox.Text) ? null : RecipeColumnsBox.Text.Trim(), v => _currentControl.RecipeColumns = v);
            if (RecipeGroupBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.RecipeGroup), string.IsNullOrWhiteSpace(RecipeGroupBox.Text) ? null : RecipeGroupBox.Text.Trim(), v => _currentControl.RecipeGroup = v);
            if (RecipeUnitCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.RecipeShowUnit), RecipeUnitCheck.IsChecked == true, v => _currentControl.RecipeShowUnit = v);
            if (AsciiEncodingCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AsciiEncoding), GetComboTag(AsciiEncodingCombo), v => _currentControl.AsciiEncoding = v);
            if (AsciiMaxLenBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AsciiMaxLen), (int)AsciiMaxLenBox.Value, v => _currentControl.AsciiMaxLen = v);'''
if 'ChartKindCombo.IsVisible' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 挂接 ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '        PipeWallBox.ValueChanged += OnNumberChanged;'
b = a + '\n        HistRowHeightBox.ValueChanged += OnNumberChanged;\n        AsciiMaxLenBox.ValueChanged += OnNumberChanged;'
if 'HistRowHeightBox.ValueChanged' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 字典 ----------
zh = '''  "tb.chart": "图表",
  "tb.xycurve": "XY 曲线",
  "tb.historytable": "历史数据表",
  "tb.recipetable": "配方表",
  "tb.asciidisplay": "ASCII 显示",
  "prop.detail.chart.kind": "类型",
  "prop.detail.chart.kind.bar": "柱状",
  "prop.detail.chart.kind.pie": "饼图",
  "prop.detail.chart.kind.donut": "环形",
  "prop.detail.chart.series": "系列（变量Id:标签，每行一条）",
  "prop.detail.chart.legend": "显示图例",
  "prop.detail.chart.value": "显示数值",
  "prop.detail.xy.tagx": "X 变量",
  "prop.detail.xy.tagy": "Y 变量",
  "prop.detail.xy.dot": "画点",
  "prop.detail.xy.line": "连成线",
  "prop.detail.histtable.columns": "列（键:标签[:宽]，每行一列）",
  "prop.detail.histtable.group": "趋势组",
  "prop.detail.histtable.rowheight": "行高（0=默认）",
  "prop.detail.histtable.scrollbar": "显示滚动条",
  "prop.detail.recipe.columns": "列（键:标签[:宽]，每行一列）",
  "prop.detail.recipe.group": "配方组",
  "prop.detail.recipe.unit": "显示单位",
  "prop.detail.ascii.encoding": "解码方式",
  "prop.detail.ascii.encoding.gbk": "GBK",
  "prop.detail.ascii.encoding.ascii": "ASCII",
  "prop.detail.ascii.maxlen": "最多字符数",
'''
en = '''  "tb.chart": "Chart",
  "tb.xycurve": "XY curve",
  "tb.historytable": "History table",
  "tb.recipetable": "Recipe table",
  "tb.asciidisplay": "ASCII display",
  "prop.detail.chart.kind": "Type",
  "prop.detail.chart.kind.bar": "Bar",
  "prop.detail.chart.kind.pie": "Pie",
  "prop.detail.chart.kind.donut": "Donut",
  "prop.detail.chart.series": "Series (tagId:label, one per line)",
  "prop.detail.chart.legend": "Show legend",
  "prop.detail.chart.value": "Show values",
  "prop.detail.xy.tagx": "X tag",
  "prop.detail.xy.tagy": "Y tag",
  "prop.detail.xy.dot": "Draw points",
  "prop.detail.xy.line": "Connect with lines",
  "prop.detail.histtable.columns": "Columns (key:label[:w], one per line)",
  "prop.detail.histtable.group": "Trend group",
  "prop.detail.histtable.rowheight": "Row height (0 = default)",
  "prop.detail.histtable.scrollbar": "Show scrollbar",
  "prop.detail.recipe.columns": "Columns (key:label[:w], one per line)",
  "prop.detail.recipe.group": "Recipe group",
  "prop.detail.recipe.unit": "Show unit",
  "prop.detail.ascii.encoding": "Encoding",
  "prop.detail.ascii.encoding.gbk": "GBK",
  "prop.detail.ascii.encoding.ascii": "ASCII",
  "prop.detail.ascii.maxlen": "Max characters",
'''
for p, block in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', zh),
                 ('src/HmiEditor.Core/Resources/Strings.en-US.json', en)]:
    s = open(p, encoding='utf-8').read()
    if 'tb.chart' in s:
        continue
    anchor = '  "tb.shape"'
    i = s.index(anchor)
    j = s.index('\n', i) + 1
    s = s[:j] + block + s[j:]
    open(p, 'w', encoding='utf-8').write(s)
print('b5c ok')
