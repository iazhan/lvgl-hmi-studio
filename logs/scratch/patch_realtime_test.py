p = 'tests/HmiEditor.Export.Chenge.Tests/ExportFixture.cs'
s = open(p, encoding='utf-8').read()
a = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c9", Type = HmiControlType.HistoryCurve'''
b = '''        screen.Controls.Add(new HmiControl
        {
            Id = "c13", Type = HmiControlType.RealtimeCurve, Name = "RealtimeCurve1", X = 380, Y = 300, Width = 400, Height = 160,
            MinValue = 0, MaxValue = 100, SpanMin = 5, SampleMs = 500, HistoryS = 60,
            CurveVariableIds = "v_temp",
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c9", Type = HmiControlType.HistoryCurve'''
if 'RealtimeCurve1' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.Export.Chenge.Tests/ChengeExportTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''    [Fact]
    public void Export_AllPropsAreDeclaredInSchema()'''
add = '''    /// <summary>
    /// 批次 2b：实时曲线与历史曲线共用外观面板，但数据源不同 —— 它写 sample_ms/history_s/span_s，
    /// **不写** group / range / refresh_ms（那三个是 trends.json 存盘组的东西）。
    /// </summary>
    [Fact]
    public void Export_RealtimeCurve_UsesSamplePropsNotGroup()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var props = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "realtime_curve")
            .GetProperty("props");

        Assert.Equal(500, props.GetProperty("sample_ms").GetInt32());
        Assert.Equal(60, props.GetProperty("history_s").GetInt32());
        Assert.Equal(300, props.GetProperty("span_s").GetInt32());
        Assert.Equal(1, props.GetProperty("curves").GetArrayLength());
        Assert.False(props.TryGetProperty("group", out _));
        Assert.False(props.TryGetProperty("refresh_ms", out _));
    }

'''
if 'Export_RealtimeCurve_UsesSamplePropsNotGroup' not in s:
    s = s.replace(anchor, add + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
