p = 'tests/HmiEditor.Export.Chenge.Tests/ExportFixture.cs'
s = open(p, encoding='utf-8').read()
a = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c16", Type = HmiControlType.QrCode'''
b = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c20", Type = HmiControlType.Shape, Name = "Shape1", X = 640, Y = 430, Width = 100, Height = 100,
            ShapeKind = "star5", BgColor = "#38C172", BorderColor = "#1F7A45", BorderWidth = 2,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c16", Type = HmiControlType.QrCode'''
if 'Shape1' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.Export.Chenge.Tests/ChengeExportTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''    [Fact]
    public void Export_AllPropsAreDeclaredInSchema()'''
add = '''    /// <summary>
    /// 批次 4：通用图元把 40 种 kind 收成"一个控件 + 一个下拉"。
    /// 数量必须与契约 figures 对齐（少一个 = 那个图元选得到但导不出去；多一个 = 运行时拒绝整页）。
    /// </summary>
    [Fact]
    public void Export_ShapeCoverAllFigureKinds()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var shape = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "shape");
        Assert.Equal("star5", shape.GetProperty("kind").GetString());
        Assert.Equal("#38C172", shape.GetProperty("props").GetProperty("fill_color").GetString());
        Assert.Equal(2, shape.GetProperty("props").GetProperty("border_width").GetInt32());

        // 白名单与契约 figures 逐项一致（反射读私有静态字段，避免再维护一份清单）
        var field = typeof(ChengeExporter).Assembly
            .GetType("HmiEditor.Export.Chenge.ChengeWidgetMapper")!
            .GetField("ShapeKinds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var kinds = (string[])field.GetValue(null)!;
        Assert.Equal(40, kinds.Length);
        Assert.Contains("valve_butterfly", kinds);
        Assert.Contains("plane_groove", kinds);
        Assert.Contains("torus3d", kinds);
    }

'''
if 'Export_ShapeCoverAllFigureKinds' not in s:
    s = s.replace(anchor, add + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
