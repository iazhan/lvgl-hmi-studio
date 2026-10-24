using HmiEditor.Core.Models;
using HmiEditor.Core.Models.Alarm;
using HmiEditor.Core.Models.Trend;
using HmiEditor.Export.Chenge;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 构造一个覆盖验收清单的工程：label/value/button/lamp/switch/slider/bar + alarm_list + history_curve，
/// 一个 sim 之外的 Modbus 设备、两个趋势组、一个报警组。导出结果是要交给宿主运行时真跑的。
/// </summary>
internal static class ExportFixture
{
    public static HmiProject Build()
    {
        var project = new HmiProject
        {
            Name = "验收工程",
            HardwareTargetId = "f1c100s-kinseal",
            ProjectVersion = "1.0.0",
        };

        var conn = new DeviceConnection { Id = "conn1", Name = "plc1", DriverId = "modbus-rtu" };
        conn.Config["port"] = "COM1";
        conn.Config["baud"] = "9600";
        conn.Config["dataBits"] = "8";
        conn.Config["stopBits"] = "1";
        conn.Config["parity"] = "None";
        conn.Config["slaveId"] = "1";
        conn.Config["timeoutMs"] = "300";
        project.VariableManager.Connections.Add(conn);

        var temp = new Variable
        {
            Id = "v_temp", Name = "炉温", DataType = "Float", ConnectionId = "conn1",
            Area = "holding", Address = "40001", Unit = "℃", Scale = 0.1, Access = "r",
            ByteOrder = "CDAB",
        };
        // 内部变量（没有 source）：按钮写它不会再经设备队列，宿主点击能立刻看到值变化
        var start = new Variable
        {
            Id = "v_start", Name = "启动", DataType = "Bool", Access = "rw",
        };
        var setpoint = new Variable
        {
            Id = "v_set", Name = "设定", DataType = "Int16", Access = "rw",
        };
        project.VariableManager.Variables.Add(temp);
        project.VariableManager.Variables.Add(start);
        project.VariableManager.Variables.Add(setpoint);

        var screen = new HmiScreen { Id = "s1", Name = "home", Width = 800, Height = 480, BgColor = "#101820" };
        screen.Controls.Add(new HmiControl
        {
            Id = "c1", Type = HmiControlType.Label, Name = "Label1", X = 20, Y = 16, Width = 200, Height = 32,
            Text = "炉温监控", FontSize = 24, TextColor = "#FFFFFF",
            LabelLetterSpacing = 2, LabelLineSpacing = 6,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c2", Type = HmiControlType.Value, Name = "Value1", X = 240, Y = 16, Width = 160, Height = 32,
            FontSize = 24, TextColor = "#00E676", ValueDecimalPlaces = 1, Unit = "℃",
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c3", Type = HmiControlType.Button, Name = "BtnStart", X = 40, Y = 80, Width = 140, Height = 56,
            Text = "启动", FontSize = 20, BgColor = "#2F70D0", TextColor = "#FFFFFF", Radius = 8,
            // 批次 1a：长按 + 禁用表现（**不设 confirm**：二次确认会拦住宿主测试的单击，
            // 那条属性由 Export_DetailProps_ReachThePageJson 单独构造工程来验）
            ButtonHoldMs = 100,   // 要小于注入点击的按住时长（约 130ms），否则 click 会被忽略
            ButtonLongPressMs = 1200,
            ButtonOffStyle = "hide",
            Actions =
            {
                new HmiAction { ActionType = "SetVariable", Target = "v_start", Value = "1", TriggerEvent = "OnClick" },
            },
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c4", Type = HmiControlType.Led, Name = "Lamp1", X = 220, Y = 88, Width = 40, Height = 40,
            IndicatorColor = "#38C172",
            // 批次 1a：灯珠直径 + 按值换色
            LampSize = 32, LampRanges = "50:#38C172,80:#F5A524,:#E5484D",
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c4b", Type = HmiControlType.Gauge, Name = "Gauge1", X = 220, Y = 140, Width = 120, Height = 120,
            MinValue = 0, MaxValue = 100, ShowPointer = true, MajorTickCount = 6, ShowTicks = true,
            // 批次 1a：弧/刻度/指针/中心圆 + 色段
            GaugeArcWidth = 12, GaugeTickLen = 8, GaugeNeedleLen = 0.7, GaugeNeedleWidth = 5,
            GaugeCenterRadius = 8, GaugeRanges = "60:#38C172,:#E5484D",
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c5", Type = HmiControlType.Switch, Name = "Switch1", X = 300, Y = 88, Width = 100, Height = 40,
            Text = "运行", SwitchKnobColor = "#FFCC00", SwitchColorOn = "#22AA55",
            VariableBindings =
            {
                new VariableBindingData { PropertyName = "IsChecked", VariableId = "v_start", Direction = BindingDirection.ReadWrite },
            },
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c6", Type = HmiControlType.Slider, Name = "Slider1", X = 40, Y = 180, Width = 300, Height = 40,
            MinValue = 0, MaxValue = 100, StepValue = 1,
            VariableBindings =
            {
                new VariableBindingData { PropertyName = "Value", VariableId = "v_set", Direction = BindingDirection.ReadWrite },
            },
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c7", Type = HmiControlType.Bar, Name = "Bar1", X = 40, Y = 240, Width = 300, Height = 24,
            MinValue = 0, MaxValue = 500, IndicatorColor = "#4EA1FF",
            BarDirection = "Vertical", BarShowPercent = true, BarRanges = "200:#38C172,:#E5484D",
            VariableBindings =
            {
                new VariableBindingData { PropertyName = "Value", VariableId = "v_temp", Direction = BindingDirection.ReadOnly },
            },
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c10", Type = HmiControlType.Spinner, Name = "Spinner1", X = 620, Y = 16, Width = 60, Height = 60,
            IndicatorColor = "#2F70D0", SpinSpeed = 800, SpinnerArcLen = 240, SpinnerArcWidth = 6,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c11", Type = HmiControlType.Knob, Name = "Knob1", X = 620, Y = 96, Width = 120, Height = 120,
            MinValue = 0, MaxValue = 100, StepValue = 1, KnobArcWidth = 14,
            VariableBindings =
            {
                new VariableBindingData { PropertyName = "Value", VariableId = "v_set", Direction = BindingDirection.ReadWrite },
            },
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c12", Type = HmiControlType.TextArea, Name = "Input1", X = 40, Y = 300, Width = 200, Height = 44,
            Text = "", MaxLength = 8, InputCommit = "blur",
            VariableBindings =
            {
                new VariableBindingData { PropertyName = "Text", VariableId = "v_set", Direction = BindingDirection.ReadWrite },
            },
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c8", Type = HmiControlType.AlarmList, Name = "AlarmList1", X = 380, Y = 80, Width = 400, Height = 180,
            AlarmGroupFilter = "温度", AlarmMode = "active", AlarmLevelFilter = "1~4", AlarmMaxRows = 8,
        });
        screen.Controls.Add(new HmiControl
        {
            Id = "c9", Type = HmiControlType.HistoryCurve, Name = "HistoryCurve1", X = 380, Y = 280, Width = 400, Height = 180,
            TrendGroup = "temp", SpanMin = 30, RefreshMs = 5000, CurveVariableIds = "v_temp", MinValue = 0, MaxValue = 500,
        });
        project.Screens.Add(screen);

        project.DataLogConfigs.Add(new DataLogConfig
        {
            Name = "temp", Mode = "period", PeriodS = 10, MaxMb = 4, MaxRecords = 2000,
            CsvDelimiter = ",", CsvTimeFormat = "iso", RecordTimestamp = true,
            VariableIds = { "v_temp" },
        });

        var group = new AlarmGroup { Name = "温度" };
        group.Alarms.Add(new AlarmConfig
        {
            Name = "炉温过高", Severity = AlarmSeverity.Critical, RequiresAck = true,
            Condition = new AlarmCondition { VariableId = "v_temp", Type = AlarmConditionType.HighLimit, Threshold = 400, Deadband = 2 },
        });
        project.AlarmGroups.Add(group);

        return project;
    }

    public static ChengeExportResult Export(HmiProject project, string outDir) =>
        new ChengeExporter().Export(project, null, new ChengeExportOptions(), outDir);
}
