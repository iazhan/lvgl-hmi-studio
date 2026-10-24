p = 'tests/HmiEditor.Export.Chenge.Tests/ExportFixture.cs'
s = open(p, encoding='utf-8').read()
a = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c15", Type = HmiControlType.Pipe'''
b = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c16", Type = HmiControlType.QrCode, Name = "Qr1", X = 640, Y = 300, Width = 120, Height = 120,
            QrText = "https://example.com/hmi", QrDarkColor = "#102030", QrLightColor = "#F0F0F0",
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c17", Type = HmiControlType.Message, Name = "Msg1", X = 20, Y = 350, Width = 400, Height = 44,
            MessageText = "温度偏高", MessageLevel = "warn", MessageAutoHideMs = 5000, MessageMarquee = true,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c18", Type = HmiControlType.EnumText, Name = "Enum1", X = 440, Y = 350, Width = 160, Height = 32,
            EnumTextValue = "运行中",
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c19", Type = HmiControlType.Roller, Name = "Roller1", X = 620, Y = 350, Width = 160, Height = 120,
            RollerOptions = "白班\\n中班\\n夜班", RollerVisibleRows = 4,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c15", Type = HmiControlType.Pipe'''
if 'Qr1' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.Export.Chenge.Tests/ChengeExportTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''    [Fact]
    public void Export_AllPropsAreDeclaredInSchema()'''
add = '''    /// <summary>
    /// 批次 3：二维码 / 消息条 / 枚举文本 / 滚轮的属性要落进 JSON，且取值合法。
    /// </summary>
    [Fact]
    public void Export_Batch3Widgets_ReachThePageJson()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var byType = page.GetProperty("widgets").EnumerateArray()
            .GroupBy(w => w.GetProperty("type").GetString()!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var qr = byType["qrcode"][0].GetProperty("props");
        Assert.Equal("https://example.com/hmi", qr.GetProperty("text").GetString());
        Assert.Equal("#102030", qr.GetProperty("dark_color").GetString());
        Assert.Equal("#F0F0F0", qr.GetProperty("light_color").GetString());

        var msg = byType["message"][0].GetProperty("props");
        Assert.Equal("温度偏高", msg.GetProperty("text").GetString());
        Assert.Equal("warn", msg.GetProperty("level").GetString());
        Assert.Equal(5000, msg.GetProperty("auto_hide_ms").GetInt32());
        Assert.True(msg.GetProperty("marquee").GetBoolean());

        var en = byType["enum_text"][0].GetProperty("props");
        Assert.Equal("运行中", en.GetProperty("text").GetString());

        var roller = byType["roller"][0].GetProperty("props");
        Assert.Equal(3, roller.GetProperty("options").GetArrayLength());
        Assert.Equal("中班", roller.GetProperty("options")[1].GetString());
        // visible_rows 必须是奇数：填 4 → 5
        Assert.Equal(5, roller.GetProperty("visible_rows").GetInt32());

        // 三种控件都声明了 widgets_ext（运行时对它们有 requires 检查）
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "manifest.json"))).RootElement;
        Assert.Contains("widgets_ext", manifest.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

'''
if 'Export_Batch3Widgets_ReachThePageJson' not in s:
    s = s.replace(anchor, add + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
