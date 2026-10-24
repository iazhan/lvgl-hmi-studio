p = 'tests/HmiEditor.Export.Chenge.Tests/ExportFixture.cs'
s = open(p, encoding='utf-8').read()
a = '''            Id = "c8", Type = HmiControlType.AlarmList, Name = "AlarmList1", X = 380, Y = 80, Width = 400, Height = 180,'''
b = '''            Id = "c8", Type = HmiControlType.AlarmList, Name = "AlarmList1", X = 380, Y = 80, Width = 400, Height = 180,
            AlarmOrder = "newest_last", AlarmAckOn = "dblclick", AlarmGrid = "rows",
            AlarmRowHeight = 30, AlarmBlink = false, AlarmRefreshMs = 1000,
            AlarmDateFormat = "yyyy-MM-dd", AlarmTimeFormat = "HH:mm",'''
if 'AlarmOrder = "newest_last"' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

p = 'tests/HmiEditor.Export.Chenge.Tests/ChengeExportTests.cs'
s = open(p, encoding='utf-8').read()
anchor = '''    [Fact]
    public void Export_AllPropsAreDeclaredInSchema()'''
add = '''    /// <summary>
    /// 批次 2c：报警列表的显示细节必须落进 JSON，且取值落在运行时的白名单里
    /// （这些枚举是硬校验，写错会让**整页**建不出来）。
    /// </summary>
    [Fact]
    public void Export_AlarmListDetailProps_ReachThePageJson()
    {
        var result = ExportFixture.Export(ExportFixture.Build(), _dir);
        Assert.True(result.Success, string.Join("\\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var props = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "alarm_list")
            .GetProperty("props");

        Assert.Equal("newest_last", props.GetProperty("order").GetString());
        Assert.Equal("dblclick", props.GetProperty("ack_on").GetString());
        Assert.Equal("rows", props.GetProperty("grid").GetString());
        Assert.Equal(30, props.GetProperty("row_height").GetInt32());
        Assert.False(props.GetProperty("blink").GetBoolean());
        Assert.Equal(1000, props.GetProperty("refresh_ms").GetInt32());
        Assert.Equal("yyyy-MM-dd", props.GetProperty("date_format").GetString());
        Assert.Equal("HH:mm", props.GetProperty("time_format").GetString());
    }

'''
if 'Export_AlarmListDetailProps' not in s:
    s = s.replace(anchor, add + anchor, 1)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
