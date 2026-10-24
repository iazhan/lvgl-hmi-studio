p = 'tests/HmiEditor.Export.Chenge.Tests/ExportFixture.cs'
s = open(p, encoding='utf-8').read()
a = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c13", Type = HmiControlType.RealtimeCurve'''
b = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c14", Type = HmiControlType.AlarmBar, Name = "AlarmBar1", X = 20, Y = 420, Width = 400, Height = 32,
            AlarmGroupFilter = "温度", AlarmOrder = "newest_last", AlarmNormalText = "系统正常",
            AlarmOnlyUnacked = false, AlarmScrollMs = 2000, AlarmScrollChars = 8,
            AlarmShowTime = true, AlarmTimeFormat = "HH:mm:ss",
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c13", Type = HmiControlType.RealtimeCurve'''
if 'AlarmBar1' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.Export.Chenge.Tests/ChengeExportTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''    [Fact]
    public void Export_AllPropsAreDeclaredInSchema()'''
add = '''    /// <summary>
    /// 批次 2d：报警条的 12 个 props 必须落进 JSON；order 只有两个合法值（运行时硬校验）。
    /// </summary>
    [Fact]
    public void Export_AlarmBarDetailProps_ReachThePageJson()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var props = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "alarm_bar")
            .GetProperty("props");

        Assert.Equal("温度", props.GetProperty("source").GetProperty("alarm_group").GetString());
        Assert.Equal("newest_last", props.GetProperty("order").GetString());
        Assert.Equal("系统正常", props.GetProperty("normal_text").GetString());
        Assert.False(props.GetProperty("only_unacked").GetBoolean());
        Assert.Equal(2000, props.GetProperty("scroll_ms").GetInt32());
        Assert.Equal(8, props.GetProperty("scroll_chars").GetInt32());
        Assert.True(props.GetProperty("show_time").GetBoolean());
        Assert.Equal("HH:mm:ss", props.GetProperty("time_format").GetString());
    }

'''
if 'Export_AlarmBarDetailProps' not in s:
    s = s.replace(anchor, add + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
