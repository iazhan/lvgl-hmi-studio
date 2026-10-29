using System.Globalization;
using System.Text.Json;
using HmiEditor.Core.Coverage;
using HmiEditor.Core.Models;
using HmiEditor.Core.Models.Alarm;
using HmiEditor.Core.Models.Trend;
using Xunit;
using Xunit.Abstractions;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 导出侧的覆盖矩阵：对登记表里每个"闭环"属性，构造一个把该字段设成非缺省值的控件，
/// 导出后核对 <c>pages/*.json</c> 里 <b>对应的键存在且值等于模型值</b>。
///
/// <para>登记表与面板侧用例同源（<see cref="WidgetPropertyCoverage"/>）：面板能改、导出不写，
/// 或导出的键/值对不上，都会在这里红。依赖资产库的类型（图片/值换图/帧动画）单独排除，
/// 理由见 <see cref="AssetBound"/>。</para>
/// </summary>
public class CoverageMatrixExportTests
{
    private readonly ITestOutputHelper _out;

    public CoverageMatrixExportTests(ITestOutputHelper output) => _out = output;

    /// <summary>需要图片资产的类型：资产由资产库提供，本用例不注入（由 AssetExportTests 覆盖）。</summary>
    private static readonly HashSet<HmiControlType> AssetBound = new()
    {
        HmiControlType.Image, HmiControlType.StateImage, HmiControlType.FrameAnim,
        HmiControlType.Svg, HmiControlType.Screen,
    };

    /// <summary>复合结构：键必须存在，值与模型不是一一对应（由各自的专项用例核对内容）。</summary>
    private static readonly HashSet<string> Composite = new(StringComparer.Ordinal)
    {
        "points", "series", "tabs", "images", "frames", "columns", "curves", "cells",
        "children", "pressed", "confirm", "source", "filter", "options",
        "col_w", "header",   // 由列宽/表头文本派生成数组与 bool，不是字段的直写
        "range", "x", "y",   // range={last_min}、xy 的 x/y={tag}
    };

    [Fact]
    public void ClosedProperties_ReachPageJsonWithTheModelValue()
    {
        var problems = new List<string>();
        var checkedCount = 0;
        var compositeCount = 0;

        foreach (var (name, type) in WidgetTypes())
        {
            if (AssetBound.Contains(type)) continue;
            var regs = WidgetPropertyCoverage.ForType(name).Where(r => r.IsClosed).ToList();
            if (regs.Count == 0) continue;

            var output = TestTempDir.NewDir("coverage-matrix");
            try
            {
                var project = BuildProject(type, regs, out var control);
                var result = ExportFixture.Export(project, output);
                if (!result.Success)
                {
                    problems.Add($"{name}: 导出失败 —— " + string.Join(" | ", result.Issues.Items.Take(3)));
                    continue;
                }

                var pageId = result.PageIds[project.Screens[0].Id];
                using var document = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(output, "pages", $"{pageId}.json")));
                var widget = document.RootElement.GetProperty("widgets")[0];
                var props = widget.GetProperty("props");

                foreach (var reg in regs)
                {
                    var key = reg.ExportKey!;
                    var modelValue = typeof(HmiControl).GetProperty(reg.Model!)?.GetValue(control);

                    // 几何四键写在控件节点根上（不是 props）。注意 xy_curve 的 x/y 是变量轴，
                    // 模型字段是 XyTagX/XyTagY —— 这里按**模型字段**判断，别按键名。
                    if (reg.Model is "X" or "Y" or "Width" or "Height")
                    {
                        var expectedRoot = (int)Math.Round(Convert.ToDouble(modelValue));
                        if (key is "w" or "h") expectedRoot = Math.Max(1, expectedRoot);
                        var actualRoot = widget.GetProperty(key).GetInt32();
                        checkedCount++;
                        if (actualRoot != expectedRoot)
                            problems.Add($"{name}.{reg.Property}: 根字段 {key} 期望 {expectedRoot}，实际 {actualRoot}");
                        continue;
                    }

                    if (Composite.Contains(key))
                    {
                        if (!props.TryGetProperty(key, out _))
                            problems.Add($"{name}.{reg.Property}: 复合键 {key} 没写进 props");
                        compositeCount++;
                        continue;
                    }

                    if (!props.TryGetProperty(key, out var actual))
                    {
                        problems.Add($"{name}.{reg.Property}: 键 {key} 没写进 props（模型 {reg.Model}）");
                        continue;
                    }

                    checkedCount++;
                    // 键盘的 target 写的是导出后的控件 id（不是模型里记的控件 Id）
                    if (name == "keyboard" && key == "target")
                    {
                        var targetId = actual.GetString();
                        if (string.IsNullOrEmpty(targetId))
                            problems.Add("keyboard.target: 没有回填目标控件 id");
                        continue;
                    }

                    var model = modelValue;
                    var expected = Expected(key, model);
                    if (expected == null) continue;
                    if (!Matches(expected, actual))
                        problems.Add($"{name}.{reg.Property}: {key} 期望 {expected}，实际 {actual.GetRawText()}"
                                     + $"（模型 {model}）");
                }
            }
            finally
            {
                TestTempDir.DeleteDir(output);
            }
        }

        _out.WriteLine($"逐键核对 {checkedCount} 个，复合键存在性 {compositeCount} 个");
        Assert.True(problems.Count == 0,
            "导出与登记不一致：" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(40)));
    }

    /// <summary>
    /// 40 个图元 kind 的专属参数：逐个 kind 建一个 shape，把每条登记过的 kind 级属性设成
    /// 非缺省值，导出后核对键与值；外加 figure_common 的 fill_opa 与起止角。
    /// </summary>
    [Fact]
    public void FigureProperties_ReachPageJsonWithTheModelValue()
    {
        var problems = new List<string>();
        var checkedCount = 0;

        foreach (var kind in FigurePropertyCatalog.Kinds)
        {
            var props = FigurePropertyCatalog.For(kind);
            var output = TestTempDir.NewDir("coverage-figure");
            try
            {
                var project = new HmiProject { Name = "fig", HardwareTargetId = "f1c100s-kinseal" };
                var screen = new HmiScreen { Id = "s1", Name = "Main", Width = 800, Height = 480 };
                var control = new HmiControl
                {
                    Id = "c1", Type = HmiControlType.Shape, ShapeKind = kind, Name = "n1",
                    X = 10, Y = 12, Width = 160, Height = 120,
                    BgColor = "#0EA5E9", BorderColor = "#334155", BorderWidth = 2,
                    ArcAngleStart = 15, ArcAngleEnd = 200, FigureFillOpa = 128,
                };
                foreach (var prop in props)
                {
                    object value = prop.Type switch
                    {
                        FigurePropType.Bool => !FigurePropertyCatalog.ToBool(prop.Effective(control)),
                        FigurePropType.Int => Math.Min((int)prop.Max,
                            Math.Max((int)prop.Min, FigurePropertyCatalog.ToInt(prop.Effective(control)) + 2)),
                        FigurePropType.Float => Math.Round(Math.Clamp(
                            FigurePropertyCatalog.ToDouble(prop.Effective(control)) + 0.07, prop.Min, prop.Max), 3),
                        _ => prop.Options.Count > 1 ? prop.Options[^1] : prop.Options[0],
                    };
                    prop.Set(control, value);
                }
                screen.Controls.Add(control);
                project.Screens.Add(screen);

                var result = ExportFixture.Export(project, output);
                if (!result.Success)
                {
                    problems.Add($"{kind}: 导出失败 —— " + string.Join(" | ", result.Issues.Items.Take(2)));
                    continue;
                }

                var pageId = result.PageIds[screen.Id];
                using var document = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(output, "pages", $"{pageId}.json")));
                var json = document.RootElement.GetProperty("widgets")[0].GetProperty("props");

                foreach (var prop in props)
                {
                    checkedCount++;
                    if (!json.TryGetProperty(prop.Key, out var actual))
                    {
                        problems.Add($"figure:{kind}.{prop.Key}: 键没写进 props");
                        continue;
                    }
                    var expected = ExpectedFigure(prop, prop.Get(control));
                    if (expected != null && !Matches(expected, actual))
                        problems.Add($"figure:{kind}.{prop.Key}: 期望 {expected}，实际 {actual.GetRawText()}");
                }

                foreach (var (key, expected) in new (string, object)[]
                         {
                             ("fill_opa", 128), ("angle_start", 15), ("angle_end", 200),
                         })
                {
                    checkedCount++;
                    if (!json.TryGetProperty(key, out var actual))
                        problems.Add($"figure:{kind}.{key}: 键没写进 props");
                    else if (!Matches(expected, actual))
                        problems.Add($"figure:{kind}.{key}: 期望 {expected}，实际 {actual.GetRawText()}");
                }
            }
            finally
            {
                TestTempDir.DeleteDir(output);
            }
        }

        _out.WriteLine($"图元逐键核对 {checkedCount} 个");
        Assert.True(problems.Count == 0,
            "图元导出与登记不一致：" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(30)));
    }

    /// <summary>图元属性的期望值：Float 保 3 位小数，Int 取整，Bool 原样，Enum 小写。</summary>
    private static object? ExpectedFigure(FigureProperty prop, object? value)
    {
        if (value == null) return null;
        return prop.Type switch
        {
            FigurePropType.Bool => FigurePropertyCatalog.ToBool(value),
            FigurePropType.Int => FigurePropertyCatalog.ToInt(value),
            FigurePropType.Float => Math.Round(FigurePropertyCatalog.ToDouble(value), 3),
            _ => (value as string ?? "").ToLowerInvariant(),
        };
    }

    private static IEnumerable<(string Name, HmiControlType Type)> WidgetTypes() =>
        WidgetPropertyCoverage.WidgetTypes
            .Select(n => (Name: n, Ok: WidgetPropertyCoverage.TryWidgetType(n, out var t), Type: t))
            .Where(x => x.Ok)
            .Select(x => (x.Name, x.Type));

    /// <summary>把每条闭环属性设成非缺省值（数字 +7、布尔取反、文本/颜色给可识别值）。</summary>
    private static HmiProject BuildProject(HmiControlType type,
        IReadOnlyList<PropertyCoverage> regs, out HmiControl control)
    {
        var project = new HmiProject { Name = "matrix", HardwareTargetId = "f1c100s-kinseal" };
        var screen = new HmiScreen { Id = "s1", Name = "Main", Width = 800, Height = 480 };
        control = new HmiControl
        {
            Id = "c1", Type = type, Name = "n1", X = 10, Y = 12, Width = 160, Height = 60,
            Text = "文字",
            MinValue = 0, MaxValue = 100, StepValue = 1,
            Options = "A\nB",
            RollerOptions = "A\nB",
            TabTitles = "页1,页2",
            HistTableGroup = "g", HistTableColumns = "time:时间",
            RecipeGroup = "r", RecipeColumns = "name:名称",
            TrendGroup = "t", CurveVariableIds = "v1",
            AlarmGroupFilter = "温度", AlarmColumns = "time,name", AlarmLevelFilter = "1~4",
            ShapeKind = "star5", KeyboardTarget = "c1", QrText = "x", MessageText = "m",
            EnumTextValue = "运行",
            XyTagX = "v1", XyTagY = "v1", ChartSeries = "v1:标签",
            ChartKind = "bar", AlarmMode = "history", AlarmOrder = "newest_last",
            AlarmAckOn = "dblclick", AlarmGrid = "rows", GaugeLabelPos = "outside",
            InputCommit = "blur", PipeDirection = "Vertical", BarDirection = "Vertical",
            CurveDirection = "RightToLeft", AsciiEncoding = "gbk", MessageLevel = "warn",
            GaugeArcWidth = 12, GaugeTickLen = 8, GaugeNeedleLen = 0.7, GaugeNeedleWidth = 5,
            GaugeCenterRadius = 8, SpinnerArcLen = 240, SpinnerArcWidth = 6, SpinSpeed = 800,
            KnobArcWidth = 14, TabHeight = 60, TabPos = "Left", LampSize = 32, Brightness = 200,
            RowCount = 3, ColumnCount = 3, RowHeight = 30, ColumnWidths = "40,40,40",
            MajorTickCount = 6, MaxLength = 8, SelectedIndex = 1, StretchMode = "Fit",
            LabelLetterSpacing = 2,
            LabelLineSpacing = 6, ButtonHoldMs = 300, ButtonLongPressMs = 1200,
            ButtonOffStyle = "hide", ButtonConfirmText = "确定", ButtonConfirmYes = "是",
            ButtonConfirmNo = "否", ButtonConfirmWaitMs = 4000,
            SwitchColorOn = "#123456", SwitchColorOff = "#234567", SwitchKnobColor = "#345678",
            BoxColor = "#456789", KnobShowValue = false, FigureFillOpa = 128,
            FrameIntervalMs = 200, FrameLoop = false,
            HistTableMaxRows = 42, HistTableRowHeight = 34, HistTableScrollbar = false,
            HistTableRefreshMs = 1500,
            RecipeRowHeight = 36, RecipeScrollbar = false, RecipeShowUnit = false,
            AlarmMaxRows = 42, AlarmRowHeight = 34, AlarmScrollbar = false, AlarmRefreshMs = 1500,
            AlarmBlink = false, AlarmBlinkMs = 900, AlarmDateFormat = "yyyy/MM/dd",
            AlarmTimeFormat = "HH:mm:ss", AlarmNormalText = "正常", AlarmOnlyUnacked = false,
            AlarmScrollMs = 2000, AlarmScrollChars = 8, AlarmShowDate = true, AlarmShowTime = true,
            AlarmScroll = "none",
            CurveYDiv = 5, CurveYMinor = 2, CurveXDiv = 6, CurveXMinor = 3, CurveXLabelStep = 4,
            CurveYDecimals = 2, CurveYDigits = 3, CurveY2Min = 5, CurveY2Max = 200,
            CurveY2Decimals = 3, CurveY2Digits = 2, CurveXFormat = "Clock",
            CurveAxisColor = "#111213", CurveGridColor = "#212223", CurveTickColor = "#313233",
            CurveLabelColor = "#414243", CurveBgColor = "#515253", CurveBorderColor = "#616263",
            CurveGrid = false, CurveLegend = false, CurveShowAxis = false, CurveShowLabels = false,
            SpanMin = 7, HistoryS = 120, SampleMs = 700, RefreshMs = 2500, CurveRangeLastMin = 9,
            XMinValue = -5, XMaxValue = 55, CurveXDecimals = 2, XyDot = false, XyLine = true,
            MessageAutoHideMs = 4000, MessageMarquee = false,
            RollerVisibleRows = 5,
            AsciiMaxLen = 12,
            ValueDecimalPlaces = 3, Unit = "℃", ValuePrefix = "≈", ValueSuffix = "!",
            HeaderTexts = "列1,列2",
            PipeFlowDirection = "Reverse", PipeWall = 0.3,
            LabelFontStyle = "italic", LabelLongMode = "Dots", ShowTicks = true,
            Points = new List<PointData> { new(0, 0), new(20, 20) },
            VisualStates =
            {
                new ControlVisualState
                {
                    State = VisualState.Pressed, BgColor = "#123456", Text = "按下",
                },
            },
        };
        if (type == HmiControlType.TextArea)
        {
            control.VariableBindings.Add(new VariableBindingData
            {
                PropertyName = "Value", VariableId = "v1", Direction = BindingDirection.ReadWrite,
            });
        }

        // 逐条把登记为闭环的字段设成非缺省值（复合结构除外：它们靠上面的基础值做到非空）
        foreach (var reg in regs)
        {
            if (Composite.Contains(reg.ExportKey!)) continue;
            var prop = typeof(HmiControl).GetProperty(reg.Model!);
            if (prop is not { CanWrite: true }) continue;
            var probe = Probe(prop, reg.Property, control);
            if (probe != null) prop.SetValue(control, probe);
        }

        screen.Controls.Add(control);
        project.Screens.Add(screen);

        project.VariableManager.Variables.Add(new Variable
        {
            Id = "v1", Name = "变量1", DataType = "Float", Access = "rw",
        });
        project.DataLogConfigs.Add(new DataLogConfig
        {
            Name = "t", Mode = "period", PeriodS = 10, MaxMb = 4, MaxRecords = 2000,
            CsvDelimiter = ",", CsvTimeFormat = "iso", RecordTimestamp = true, VariableIds = { "v1" },
        });
        project.DataLogConfigs.Add(new DataLogConfig
        {
            Name = "g", Mode = "period", PeriodS = 10, MaxMb = 4, MaxRecords = 2000,
            CsvDelimiter = ",", CsvTimeFormat = "iso", RecordTimestamp = true, VariableIds = { "v1" },
        });
        var alarm = new AlarmGroup { Name = "温度" };
        project.AlarmGroups.Add(alarm);
        return project;
    }

    /// <summary>每个字段的探针值：受量程/枚举约束的走显式表，其余按类型加一个非缺省值。</summary>
    private static object? Probe(System.Reflection.PropertyInfo prop, string key, HmiControl control)
    {
        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        var current = prop.GetValue(control);

        if (type == typeof(string))
            return prop.Name switch
            {
                // 枚举型字符串：基础值里已经是非缺省的那个，由 Probe 改动反而会撞 OneOf 回落
                "Dateformat" or "Timeformat" => "HH:mm:ss",
                "CurveXFormat" => "Clock",
                _ when prop.Name.EndsWith("Format", StringComparison.Ordinal) => "yyyy/MM/dd",
                _ when prop.Name.Contains("Color", StringComparison.Ordinal) => "#123456",
                _ when prop.Name is "Text" or "Unit" or "ValuePrefix" or "ValueSuffix" or
                        "NormalText" or "QrText" or "MessageText" or "EnumTextValue" or
                        "ConfirmText" or "ConfirmYes" or "ConfirmNo" or "Placeholder" or
                        "HeaderTexts" or "TabTitles" or "Tooltip" => "探针文本",
                // 其余字符串是枚举型（direction/scale/kind/commit/off_style…），基础值已给非缺省
                _ => null,
            };

        // ShowTicks 决定 gauge.ticks 是否写（false → ticks:0），保持契约缺省 true
        if (prop.Name == "ShowTicks") return null;
        if (type == typeof(bool)) return !(current is true);
        if (type == typeof(bool?)) return !(current is true);
        if (prop.Name == "Rotation") return 90.0;

        var intValue = current is int i ? i : 0;
        if (type == typeof(int) || type == typeof(int?))
            return prop.Name switch
            {
                "ValueDecimalPlaces" or "CurveYDecimals" or "CurveY2Decimals" or
                    "CurveXDecimals" or "CurveYDigits" or "CurveY2Digits" => 2,
                "MaxLength" => 8,
                "AsciiMaxLen" => 12,
                "MajorTickCount" => 6,
                "RowCount" or "ColumnCount" => 3,
                "SelectedIndex" => 1,
                "TabHeight" => 60,
                "LampSize" => 32,
                "Brightness" => 200,
                "HistTableRowHeight" or "RecipeRowHeight" or "AlarmRowHeight" => 34,
                "HistTableMaxRows" or "AlarmMaxRows" => 42,
                "GaugeArcWidth" => 12,
                "GaugeTickLen" => 8,
                "GaugeNeedleWidth" => 5,
                "GaugeCenterRadius" => 8,
                "SpinnerArcLen" => 240,
                "SpinnerArcWidth" => 6,
                "SpinSpeed" => 800,
                "KnobArcWidth" => 14,
                // 量程与长按阈值有硬约束：给合法且非缺省的值
                "MinValue" => 0,
                "MaxValue" => 100,
                "ButtonHoldMs" => 300,
                "ButtonLongPressMs" => 1200,
                "ButtonConfirmWaitMs" => 4000,
                "CurveXMinor" or "CurveYMinor" => 3,
                "SampleMs" => 700,
                _ => intValue == 7 ? 9 : 7,
            };

        if (type == typeof(double) || type == typeof(double?))
        {
            var d = current is double v ? v : 0;
            return prop.Name switch
            {
                "MinValue" => 0.0,
                "MaxValue" => 100.0,
                "PipeWall" => 0.3,
                "GaugeNeedleLen" => 0.7,
                _ => Math.Abs(d - 7.5) < 0.0001 ? 9.5 : 7.5,
            };
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.Length > 1 ? values.GetValue(values.Length - 1) : null;
        }

        return null;
    }

    private static bool Matches(object expected, JsonElement actual) => expected switch
    {
        bool b => actual.ValueKind == JsonValueKind.True ? b : actual.ValueKind == JsonValueKind.False ? !b : false,
        int i => actual.ValueKind == JsonValueKind.Number && Math.Abs(actual.GetDouble() - i) < 0.001,
        double d => actual.ValueKind == JsonValueKind.Number && Math.Abs(actual.GetDouble() - d) < 0.001,
        _ => actual.ValueKind == JsonValueKind.String &&
             string.Equals(actual.GetString(), expected.ToString(), StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>
    /// 模型值 → 产物里期望的字面量（含导出器的钳制与枚举写法）；null = 不做值比对。
    /// 钳制只写"契约范围决定"的那几条，避免把导出器实现细节抄成第二份。
    /// </summary>
    private static object? Expected(string key, object? model)
    {
        if (model == null) return null;
        switch (model)
        {
            case bool b:
                // gauge.style 是 bool ShowPointer → "pointer"/"arc"
                return key == "style" ? (b ? "pointer" : "arc") : b;
            case Enum e:
                return e.ToString().ToLowerInvariant();
            case int i:
                return key switch
                {
                    "opacity" => (int)Math.Round(i * 255.0 / 100.0),
                    "font_size" => Math.Max(10, i),
                    "blink_ms" => Math.Max(100, i),
                    "flow_ms" => Math.Max(40, i),
                    "sample_ms" => Math.Max(100, i),
                    "span_s" => i * 60,
                    "scroll_ms" => Math.Max(500, i),
                    "auto_hide_ms" => Math.Max(100, i),
                    _ => i,
                };
            case double d:
                return key switch
                {
                    "opacity" => (int)Math.Round(d * 255.0 / 100.0),
                    "wall" => Math.Round(Math.Clamp(d, 0.05, 0.45), 3),
                    "needle_len" => Math.Round(Math.Clamp(d, 0.2, 1.0), 2),
                    "step" => d,
                    _ => (int)Math.Round(d),
                };
            case string s:
                return key switch
                {
                    "direction" => s.ToLowerInvariant() switch
                    {
                        "vertical" => "v",
                        "horizontal" => "h",
                        "lefttoright" => "lr",
                        "righttoleft" => "rl",
                        _ => s,
                    },
                    "scale" => s == "None" ? "actual" : "fit",
                    _ => s,
                };
            default:
                return null;
        }
    }
}
