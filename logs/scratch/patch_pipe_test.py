p = 'tests/HmiEditor.Export.Chenge.Tests/ExportFixture.cs'
s = open(p, encoding='utf-8').read()
a = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c14", Type = HmiControlType.AlarmBar'''
b = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c15", Type = HmiControlType.Pipe, Name = "Pipe1", X = 20, Y = 380, Width = 260, Height = 24,
            PipeDirection = "Vertical", PipeWall = 0.3, PipeFlowSpeed = 500,
            PipeChannelColor = "#223344", IndicatorColor = "#4EA1FF",
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c14", Type = HmiControlType.AlarmBar'''
if 'Pipe1' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.Export.Chenge.Tests/ChengeExportTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''    [Fact]
    public void Export_AllPropsAreDeclaredInSchema()'''
add = '''    /// <summary>
    /// 管道在契约里是**控件**（widgets.pipe，6 个 props），运行时也有 W_PIPE 分派。
    /// 早先导成 shape 的 pipe_h/v 只保留了 wall，direction/color/channel_color/flow_ms 全丢。
    /// </summary>
    [Fact]
    public void Export_Pipe_UsesWidgetPropsNotShape()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var pipe = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "pipe");
        var props = pipe.GetProperty("props");

        Assert.Equal("v", props.GetProperty("direction").GetString());
        Assert.Equal(0.3, props.GetProperty("wall").GetDouble(), 3);
        Assert.Equal(500, props.GetProperty("flow_ms").GetInt32());
        Assert.Equal("#223344", props.GetProperty("channel_color").GetString());
        Assert.Equal("#4EA1FF", props.GetProperty("color").GetString());
        Assert.False(props.TryGetProperty("kind", out _));   // 不再是 shape
    }

'''
if 'Export_Pipe_UsesWidgetPropsNotShape' not in s:
    s = s.replace(anchor, add + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
