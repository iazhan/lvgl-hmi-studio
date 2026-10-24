p = 'tests/HmiEditor.Export.Chenge.Tests/ExportFixture.cs'
s = open(p, encoding='utf-8').read()
a = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c20", Type = HmiControlType.Shape'''
b = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c21", Type = HmiControlType.Chart, Name = "Chart1", X = 20, Y = 440, Width = 240, Height = 140,
            ChartKind = "pie", ChartSeries = "v_temp:炉温", ChartShowLegend = false,
            MinValue = 0, MaxValue = 100,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c22", Type = HmiControlType.XyCurve, Name = "XyCurve1", X = 270, Y = 440, Width = 240, Height = 140,
            XyTagX = "v_set", XyTagY = "v_temp", XyLine = true, MinValue = 0, MaxValue = 100,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c23", Type = HmiControlType.HistoryTable, Name = "HistTable1", X = 520, Y = 20, Width = 260, Height = 140,
            HistTableColumns = "time:时间,name:名称", HistTableGroup = "temp", HistTableRowHeight = 30,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c24", Type = HmiControlType.RecipeTable, Name = "RecipeTable1", X = 520, Y = 170, Width = 260, Height = 140,
            RecipeColumns = "name:配方,0:温度", RecipeGroup = "配方A", RecipeShowUnit = false,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c25", Type = HmiControlType.AsciiDisplay, Name = "Ascii1", X = 20, Y = 400, Width = 200, Height = 32,
            AsciiEncoding = "ascii", AsciiMaxLen = 16,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c20", Type = HmiControlType.Shape'''
if 'Chart1' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.Export.Chenge.Tests/ChengeExportTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''    [Fact]
    public void Export_AllPropsAreDeclaredInSchema()'''
add = '''    /// <summary>
    /// 批次 5：图表 / XY 曲线 / 历史表 / 配方表 / ASCII 显示的核心属性要落进 JSON。
    /// </summary>
    [Fact]
    public void Export_Batch5Widgets_ReachThePageJson()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var byType = page.GetProperty("widgets").EnumerateArray()
            .GroupBy(w => w.GetProperty("type").GetString()!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var chart = byType["chart"][0].GetProperty("props");
        Assert.Equal("pie", chart.GetProperty("kind").GetString());
        Assert.Equal(1, chart.GetProperty("series").GetArrayLength());
        Assert.Equal("炉温", chart.GetProperty("series")[0].GetProperty("label").GetString());
        Assert.False(chart.GetProperty("show_legend").GetBoolean());

        var xy = byType["xy_curve"][0].GetProperty("props");
        Assert.True(xy.GetProperty("line").GetBoolean());
        Assert.True(xy.GetProperty("x").TryGetProperty("tag", out _));
        Assert.True(xy.GetProperty("y").TryGetProperty("tag", out _));

        var hist = byType["history_table"][0].GetProperty("props");
        Assert.Equal("temp", hist.GetProperty("source").GetProperty("group").GetString());
        Assert.Equal(2, hist.GetProperty("columns").GetArrayLength());
        Assert.Equal(30, hist.GetProperty("row_height").GetInt32());

        var recipe = byType["recipe_table"][0].GetProperty("props");
        Assert.Equal("配方A", recipe.GetProperty("source").GetProperty("recipe_group").GetString());
        Assert.False(recipe.GetProperty("show_unit").GetBoolean());

        var ascii = byType["ascii_display"][0].GetProperty("props");
        Assert.Equal("ascii", ascii.GetProperty("encoding").GetString());
        Assert.Equal(16, ascii.GetProperty("max_len").GetInt32());
    }

'''
if 'Export_Batch5Widgets_ReachThePageJson' not in s:
    s = s.replace(anchor, add + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
