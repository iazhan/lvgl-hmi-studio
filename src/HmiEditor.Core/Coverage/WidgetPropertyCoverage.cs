using System;
using System.Collections.Generic;
using System.Linq;
using HmiEditor.Core.Models;

namespace HmiEditor.Core.Coverage;

/// <summary>契约属性的四类结论（面板重做方案 52 §2）。</summary>
public enum CoverageDecision
{
    /// <summary>闭环：面板可编辑 → 模型字段 → 导出键 → 运行时读取，四层都在。</summary>
    Closed,

    /// <summary>运行时已消费、面板还没有行（登记在册，不装作已覆盖）。</summary>
    PanelToAdd,

    /// <summary>面板能改、运行时不读：契约该删，或运行时该补。删键前不提供入口。</summary>
    ContractToDelete,

    /// <summary>结构/派生项：由画布、绑定或别的字段推导，没有独立编辑器（不是漏）。</summary>
    Structural,
}

/// <summary>一个（类型, 属性）的覆盖登记。</summary>
public sealed class PropertyCoverage
{
    public PropertyCoverage(string type, string property, string section, string? editor, string? model,
        string? exportKey, CoverageDecision decision, string evidence)
    {
        Type = type;
        Property = property;
        Section = section;
        Editor = editor;
        Model = model;
        ExportKey = exportKey;
        Decision = decision;
        Evidence = evidence;
    }

    public string Type { get; }
    public string Property { get; }
    public string Section { get; }
    /// <summary>面板编辑器 x:Name；null = 没有入口。</summary>
    public string? Editor { get; }
    /// <summary>HmiControl 上的字段名；null = 没有对应模型字段。</summary>
    public string? Model { get; }
    /// <summary>写进 pages/*.json props 的键；null = 不导出。</summary>
    public string? ExportKey { get; }
    public CoverageDecision Decision { get; }
    public string Evidence { get; }

    public bool IsClosed => Decision == CoverageDecision.Closed;
}

/// <summary>
/// 契约（spec/widget-schema.json）→ 编辑器覆盖的**唯一登记表**。
///
/// <para>它不复制契约：类型与属性清单由测试从 schema 读，这里只登记"编辑器怎么覆盖它"。
/// 每条必须落进四类结论之一；测试逐项核对面板显隐、写回模型、导出键值，缺项即红。</para>
/// </summary>
public static class WidgetPropertyCoverage
{
    private const string CurveAxis =
        "y_min=MinValueBox, y_max=MaxValueBox, y2_min=CurveY2MinBox, y2_max=CurveY2MaxBox, " +
        "y_decimals=CurveYDecimalsBox, y_digits=CurveYDigitsBox, y2_decimals=CurveY2DecimalsBox, " +
        "y2_digits=CurveY2DigitsBox, y_div=CurveYDivBox, y_minor=CurveYMinorBox, " +
        "x_div=CurveXDivBox, x_minor=CurveXMinorBox, x_format=CurveXFormatCombo, " +
        "x_label_step=CurveXLabelStepBox, grid=CurveGridCheck, legend=CurveLegendCheck, " +
        "show_axis=CurveShowAxisCheck, show_ticks=ShowTicksCheck, show_labels=CurveShowLabelsCheck, " +
        "axis_color=CurveAxisColorBox, grid_color=CurveGridColorBox, " +
        "tick_color=CurveTickColorBox, label_color=CurveLabelColorBox, bg_color=CurveBgColorBox, " +
        "border_color=CurveBorderColorBox, font_size=PropFontSize";

    private static readonly Dictionary<string, string> WidgetTables = new(StringComparer.Ordinal)
    {
        ["label"] = "valign=PropTextVAlign, text=PropText, font_size=PropFontSize, color=PropTextColor, " +
                    "align=PropTextAlign, font_style=LabelFontStyleCombo, line_spacing=LineSpacingBox, " +
                    "letter_spacing=LetterSpacingBox, long_mode=LongModeCombo, wrap=-, decimals=-",
        ["value"] = "valign=PropTextVAlign, decimals=ValueDecimalsCombo, unit=UnitBox, prefix=ValuePrefixBox, " +
                    "suffix=ValueSuffixBox, align=PropTextAlign, font_size=PropFontSize, color=PropTextColor",
        ["enum_text"] = "valign=PropTextVAlign, map=-, text=EnumTextValueBox, align=PropTextAlign, " +
                        "font_size=PropFontSize, color=PropTextColor",
        ["button"] = "text=PropText, font_size=PropFontSize, color=PropTextColor, pressed=VsBgColor, " +
                     "hold_ms=HoldMsBox, long_press_ms=LongPressMsBox, off_style=OffStyleCombo, " +
                     "confirm=ConfirmTextBox",
        ["switch"] = "color_on=SwitchColorOnBox, color_off=SwitchColorOffBox, text_on=PropText, " +
                     "text_off=SwitchTextOffBox, knob_color=SwitchKnobColorBox",
        ["checkbox"] = "text=PropText, font_size=PropFontSize, color=PropTextColor, checked=PropIsChecked, " +
                       "box_color=BoxColorBox",
        ["spinner"] = "color=IndicatorColorBox, arc_width=SpinnerArcWidthBox, arc_len=SpinnerArcLenBox, " +
                      "speed_ms=SpinSpeedBox",
        ["keyboard"] = "target=KeyboardTargetBox, title=-",
        ["tabview"] = "tabs=TabTitlesBox, tab_pos=TabPosCombo, tab_h=TabHeightBox",
        ["knob"] = "min=MinValueBox, max=MaxValueBox, step=StepValueBox, color=IndicatorColorBox, " +
                   "arc_width=KnobArcWidthBox, decimals=ValueDecimalsCombo, show_value=KnobShowValueCheck",
        ["lamp"] = "color=IndicatorColorBox, ranges=-, size=LampSizeBox, brightness=BrightnessBox",
        ["bar"] = "direction=BarDirectionCombo, min=MinValueBox, max=MaxValueBox, color=IndicatorColorBox, " +
                  "ranges=-, target=-, show_percent=BarShowPercentCheck, show_value=-, decimals=-",
        ["gauge"] = "style=ShowPointerCheck, min=MinValueBox, max=MaxValueBox, color=IndicatorColorBox, " +
                    "ticks=TickCountBox, tick_len=GaugeTickLenBox, arc_width=GaugeArcWidthBox, ranges=-, " +
                    "needle_len=GaugeNeedleLenBox, needle_width=GaugeNeedleWidthBox, " +
                    "center_radius=GaugeCenterRadiusBox, decimals=ValueDecimalsCombo, " +
                    "show_value=GaugeShowValueCheck, label_pos=GaugeLabelPosCombo",
        ["input"] = "mode=-, min=MinValueBox, max=MaxValueBox, decimals=InputDecimalsBox, max_len=MaxLengthBox, " +
                    "step=-, commit=InputCommitCombo, font_size=PropFontSize, align=PropTextAlign",
        ["slider"] = "min=MinValueBox, max=MaxValueBox, step=StepValueBox, color=IndicatorColorBox, show_value=-",
        ["combobox"] = "options=OptionsBox, options_tag=-, font_size=PropFontSize, color=PropTextColor, " +
                       "selected=SelectedIndexBox",
        ["image"] = "src=ImageAssetCombo, scale=StretchCombo",
        ["shape"] = "kind=ShapeKindCombo, children=-",
        ["state_image"] = "images=StateImagesBox",
        ["table"] = "row_h=RowHeightBox, col_w=ColWidthsBox, rows=RowCountBox, cols=ColCountBox, " +
                    "header=HeaderBox, font_size=PropFontSize, cells=-",
        ["qrcode"] = "text=QrTextBox, dark_color=QrDarkColorBox, light_color=QrLightColorBox",
        ["pipe"] = "direction=PipeDirectionCombo, flow_dir=PipeFlowDirectionCombo, points=FigurePointsBox, " +
                   "wall=PipeWallBox, width=PipeWidthBox, color=IndicatorColorBox, " +
                   "channel_color=PipeChannelColorBox, wall_color=PipeWallColorBox, flow_ms=PipeFlowSpeedBox",
        ["frame_anim"] = "frames=FrameImagesBox, interval_ms=FrameIntervalBox, loop=FrameLoopCheck",
        ["realtime_curve"] = "curves=CgCurveVarsBox, sample_ms=SampleMsBox, span_s=CgSpanMinBox, " +
                             "history_s=HistorySBox, direction=CurveDirectionCombo, " + CurveAxis,
        ["history_curve"] = "group=CgTrendGroupBox, range=CurveLastMinBox, refresh_ms=CurveRefreshBox, span_s=-, " +
                            "curves=CgCurveVarsBox, direction=CurveDirectionCombo, " + CurveAxis,
        ["xy_curve"] = "x=XyTagXBox, y=XyTagYBox, sample_ms=SampleMsBox, span_s=CgSpanMinBox, " +
                       "dot=XyDotCheck, line=XyLineCheck, x_min=XyXMinBox, x_max=XyXMaxBox, " +
                       "y_min=MinValueBox, y_max=MaxValueBox, grid=CurveGridCheck, x_div=CurveXDivBox, " +
                       "y_div=CurveYDivBox, x_minor=CurveXMinorBox, y_minor=CurveYMinorBox, " +
                       "x_decimals=XyXDecimalsBox, y_decimals=CurveYDecimalsBox, font_size=PropFontSize, " +
                       "axis_color=CurveAxisColorBox, grid_color=CurveGridColorBox, " +
                       "tick_color=CurveTickColorBox, label_color=CurveLabelColorBox",
        ["history_table"] = "source=HistGroupBox, columns=HistColumnsBox, row_height=HistRowHeightBox, " +
                            "max_rows=HistMaxRowsBox, font_size=PropFontSize, scrollbar=HistScrollbarCheck, " +
                            "refresh_ms=HistRefreshBox",
        ["alarm_bar"] = "source=CgAlarmGroupBox, only_unacked=AlarmOnlyUnackedCheck, scroll=AlarmScrollCombo, " +
                        "scroll_ms=AlarmScrollMsBox, scroll_chars=AlarmScrollCharsBox, blink=AlarmBlinkCheck, " +
                        "blink_ms=AlarmBlinkMsBox, show_date=AlarmShowDateCheck, show_time=AlarmShowTimeCheck, " +
                        "date_format=AlarmDateFormatBox, time_format=AlarmTimeFormatBox, " +
                        "order=AlarmOrderCombo, normal_text=AlarmNormalTextBox, font_size=PropFontSize, " +
                        "color=PropTextColor",
        ["alarm_list"] = "source=CgAlarmGroupBox, mode=CgAlarmModeCombo, filter=AlarmLevelFilterBox, " +
                         "columns=AlarmColumnsBox, group_by=-, ack_on=AlarmAckOnCombo, blink=AlarmBlinkCheck, " +
                         "blink_ms=AlarmBlinkMsBox, order=AlarmOrderCombo, grid=AlarmGridCombo, " +
                         "max_rows=CgAlarmRowsBox, row_height=AlarmRowHeightBox, " +
                         "scrollbar=AlarmScrollbarCheck, refresh_ms=AlarmRefreshBox, " +
                         "date_format=AlarmDateFormatBox, time_format=AlarmTimeFormatBox, font_size=PropFontSize",
        ["recipe_table"] = "source=RecipeGroupBox, columns=RecipeColumnsBox, show_unit=RecipeUnitCheck, " +
                           "row_height=RecipeRowHeightBox, scrollbar=RecipeScrollbarCheck, font_size=PropFontSize",
        ["chart"] = "kind=ChartKindCombo, series=ChartSeriesBox, show_value=ChartValueCheck, " +
                    "show_legend=ChartLegendCheck, y_min=MinValueBox, y_max=MaxValueBox, " +
                    "y_decimals=ChartDecimalsBox",
        ["message"] = "text=MessageTextBox, level=MessageLevelCombo, auto_hide_ms=MessageAutoHideBox, " +
                      "align=PropTextAlign, marquee=MessageMarqueeCheck",
        ["roller"] = "options=RollerOptionsBox, visible_rows=RollerRowsBox, font_size=PropFontSize",
        ["ascii_display"] = "valign=PropTextVAlign, max_len=AsciiMaxLenBox, encoding=AsciiEncodingCombo, " +
                            "align=PropTextAlign, font_size=PropFontSize, color=PropTextColor",
    };

    /// <summary>没有编辑器时的结论与理由（逐项给出，缺一条测试就判"没有结论"）。</summary>
    private static readonly Dictionary<string, (CoverageDecision Decision, string Reason)> NoEditor =
        new(StringComparer.Ordinal)
        {
            ["label.wrap"] = (CoverageDecision.ContractToDelete,
                "运行时建完 label 后不读 wrap（换行/省略/滚动由 long_mode 表达）；契约删键"),
            ["label.decimals"] = (CoverageDecision.ContractToDelete, "label 不是数值控件，运行时不读 decimals"),
            ["enum_text.map"] = (CoverageDecision.ContractToDelete,
                "运行时只读 bind[].map（ui_binding.c），props.map 写了不生效"),
            ["keyboard.title"] = (CoverageDecision.ContractToDelete,
                "build_keyboard 直接 (void)props；键盘标题取 target 绑定的变量名"),
            ["lamp.ranges"] = (CoverageDecision.ContractToDelete,
                "ranges 只在 bind[kind=ranges] 里被读；props.ranges 没有读取点"),
            ["bar.ranges"] = (CoverageDecision.ContractToDelete, "同上：props.ranges 无读取点"),
            ["gauge.ranges"] = (CoverageDecision.ContractToDelete, "同上：props.ranges 无读取点"),
            ["bar.target"] = (CoverageDecision.ContractToDelete, "build_bar 不读 target（绑定走 bind[]）"),
            ["bar.show_value"] = (CoverageDecision.ContractToDelete, "build_bar 只读 show_percent，不读 show_value"),
            ["bar.decimals"] = (CoverageDecision.ContractToDelete, "build_bar 不读 decimals"),
            ["slider.show_value"] = (CoverageDecision.ContractToDelete, "build_slider 不读 show_value"),
            ["input.step"] = (CoverageDecision.ContractToDelete, "build_input 不读 step（步长由键位决定）"),
            ["history_curve.span_s"] = (CoverageDecision.ContractToDelete,
                "history_curve 的窗口是 range.last_min；span_s 没有读取点"),
            ["alarm_list.group_by"] = (CoverageDecision.ContractToDelete,
                "build_alarm_list 不读 group_by（分组列未实现）"),
            ["input.mode"] = (CoverageDecision.Structural,
                "由绑定变量类型推导（IsNumericBinding → num/text/password），不是独立可编辑项"),
            ["combobox.options_tag"] = (CoverageDecision.Structural,
                "选项来自变量时由 options_tag 生成，面板只编辑 options 文本"),
            ["shape.children"] = (CoverageDecision.Structural, "容器子控件由画布拖入，属性面板不逐项编辑"),
            ["table.cells"] = (CoverageDecision.Structural, "单元格由「编辑单元格」入口改，不逐格建行"),
        };

    /// <summary>属性 → (模型字段, 导出键)。导出键缺省同属性名。</summary>
    private static readonly Dictionary<string, (string Model, string Export)> ModelOf = new(StringComparer.Ordinal)
    {
        ["x"] = ("X", "x"), ["y"] = ("Y", "y"), ["w"] = ("Width", "w"), ["h"] = ("Height", "h"),
        ["visible"] = ("IsVisible", "visible"), ["enabled"] = ("IsEnabled", "enabled"),
        ["opacity"] = ("Opacity", "opacity"), ["rotation"] = ("Rotation", "rotation"),
        ["mirror_x"] = ("MirrorX", "mirror_x"), ["mirror_y"] = ("MirrorY", "mirror_y"),
        ["bg_color"] = ("BgColor", "bg_color"), ["border_color"] = ("BorderColor", "border_color"),
        ["border_width"] = ("BorderWidth", "border_width"), ["radius"] = ("Radius", "radius"),
        ["tooltip"] = ("Tooltip", "tooltip"),
        ["valign"] = ("TextVAlign", "valign"), ["text"] = ("Text", "text"),
        ["font_size"] = ("FontSize", "font_size"), ["color"] = ("TextColor", "color"),
        ["align"] = ("TextAlign", "align"), ["font_style"] = ("LabelFontStyle", "font_style"),
        ["line_spacing"] = ("LabelLineSpacing", "line_spacing"),
        ["letter_spacing"] = ("LabelLetterSpacing", "letter_spacing"),
        ["long_mode"] = ("LabelLongMode", "long_mode"), ["decimals"] = ("ValueDecimalPlaces", "decimals"),
        ["unit"] = ("Unit", "unit"), ["prefix"] = ("ValuePrefix", "prefix"),
        ["suffix"] = ("ValueSuffix", "suffix"),
        ["checked"] = ("IsChecked", "checked"), ["box_color"] = ("BoxColor", "box_color"),
        ["color_on"] = ("SwitchColorOn", "color_on"), ["color_off"] = ("SwitchColorOff", "color_off"),
        ["text_on"] = ("Text", "text_on"), ["text_off"] = ("SwitchTextOff", "text_off"),
        ["knob_color"] = ("SwitchKnobColor", "knob_color"),
        ["pressed"] = ("VisualStates", "pressed"),
        ["hold_ms"] = ("ButtonHoldMs", "hold_ms"), ["long_press_ms"] = ("ButtonLongPressMs", "long_press_ms"),
        ["off_style"] = ("ButtonOffStyle", "off_style"), ["confirm"] = ("ButtonConfirmText", "confirm"),
        ["arc_width"] = ("GaugeArcWidth", "arc_width"), ["arc_len"] = ("SpinnerArcLen", "arc_len"),
        ["speed_ms"] = ("SpinSpeed", "speed_ms"),
        ["tabs"] = ("TabTitles", "tabs"), ["tab_pos"] = ("TabPos", "tab_pos"), ["tab_h"] = ("TabHeight", "tab_h"),
        ["min"] = ("MinValue", "min"), ["max"] = ("MaxValue", "max"), ["step"] = ("StepValue", "step"),
        ["style"] = ("ShowPointer", "style"), ["label_pos"] = ("GaugeLabelPos", "label_pos"),
        ["ticks"] = ("MajorTickCount", "ticks"), ["tick_len"] = ("GaugeTickLen", "tick_len"),
        ["needle_len"] = ("GaugeNeedleLen", "needle_len"),
        ["needle_width"] = ("GaugeNeedleWidth", "needle_width"),
        ["center_radius"] = ("GaugeCenterRadius", "center_radius"),
        ["show_value"] = ("GaugeShowValue", "show_value"),
        ["size"] = ("LampSize", "size"), ["brightness"] = ("Brightness", "brightness"),
        ["direction"] = ("BarDirection", "direction"), ["show_percent"] = ("BarShowPercent", "show_percent"),
        ["max_len"] = ("MaxLength", "max_len"), ["commit"] = ("InputCommit", "commit"),
        ["options"] = ("Options", "options"), ["selected"] = ("SelectedIndex", "selected"),
        ["src"] = ("ImageAssetId", "src"), ["scale"] = ("StretchMode", "scale"),
        ["kind"] = ("ShapeKind", "kind"), ["images"] = ("StateImages", "images"),
        ["row_h"] = ("RowHeight", "row_h"), ["col_w"] = ("ColumnWidths", "col_w"),
        ["rows"] = ("RowCount", "rows"), ["cols"] = ("ColumnCount", "cols"),
        ["header"] = ("HeaderTexts", "header"),
        ["dark_color"] = ("QrDarkColor", "dark_color"), ["light_color"] = ("QrLightColor", "light_color"),
        ["flow_dir"] = ("PipeFlowDirection", "flow_dir"), ["points"] = ("Points", "points"),
        ["wall"] = ("PipeWall", "wall"), ["width"] = ("PipeWidth", "width"),
        ["channel_color"] = ("PipeChannelColor", "channel_color"),
        ["wall_color"] = ("PipeWallColor", "wall_color"), ["flow_ms"] = ("PipeFlowSpeed", "flow_ms"),
        ["frames"] = ("FrameImages", "frames"), ["interval_ms"] = ("FrameIntervalMs", "interval_ms"),
        ["loop"] = ("FrameLoop", "loop"), ["curves"] = ("CurveVariableIds", "curves"),
        ["sample_ms"] = ("SampleMs", "sample_ms"), ["span_s"] = ("SpanMin", "span_s"),
        ["history_s"] = ("HistoryS", "history_s"),
        ["y_min"] = ("MinValue", "y_min"), ["y_max"] = ("MaxValue", "y_max"),
        ["y2_min"] = ("CurveY2Min", "y2_min"), ["y2_max"] = ("CurveY2Max", "y2_max"),
        ["y_decimals"] = ("CurveYDecimals", "y_decimals"), ["y_digits"] = ("CurveYDigits", "y_digits"),
        ["y2_decimals"] = ("CurveY2Decimals", "y2_decimals"), ["y2_digits"] = ("CurveY2Digits", "y2_digits"),
        ["y_div"] = ("CurveYDiv", "y_div"), ["y_minor"] = ("CurveYMinor", "y_minor"),
        ["x_div"] = ("CurveXDiv", "x_div"), ["x_minor"] = ("CurveXMinor", "x_minor"),
        ["x_format"] = ("CurveXFormat", "x_format"), ["x_label_step"] = ("CurveXLabelStep", "x_label_step"),
        ["grid"] = ("CurveGrid", "grid"), ["legend"] = ("CurveLegend", "legend"),
        ["show_axis"] = ("CurveShowAxis", "show_axis"), ["show_ticks"] = ("ShowTicks", "show_ticks"),
        ["show_labels"] = ("CurveShowLabels", "show_labels"),
        ["axis_color"] = ("CurveAxisColor", "axis_color"), ["grid_color"] = ("CurveGridColor", "grid_color"),
        ["tick_color"] = ("CurveTickColor", "tick_color"), ["label_color"] = ("CurveLabelColor", "label_color"),
        ["group"] = ("TrendGroup", "group"),
        ["range"] = ("CurveRangeLastMin", "range"), ["refresh_ms"] = ("RefreshMs", "refresh_ms"),
        ["dot"] = ("XyDot", "dot"), ["line"] = ("XyLine", "line"),
        ["x_min"] = ("XMinValue", "x_min"), ["x_max"] = ("XMaxValue", "x_max"),
        ["x_decimals"] = ("CurveXDecimals", "x_decimals"),
        ["source"] = ("AlarmGroupFilter", "source"), ["columns"] = ("HistTableColumns", "columns"),
        ["row_height"] = ("HistTableRowHeight", "row_height"),
        ["max_rows"] = ("HistTableMaxRows", "max_rows"), ["scrollbar"] = ("HistTableScrollbar", "scrollbar"),
        ["only_unacked"] = ("AlarmOnlyUnacked", "only_unacked"),
        ["scroll"] = ("AlarmScroll", "scroll"), ["scroll_ms"] = ("AlarmScrollMs", "scroll_ms"),
        ["scroll_chars"] = ("AlarmScrollChars", "scroll_chars"),
        ["blink"] = ("AlarmBlink", "blink"), ["blink_ms"] = ("AlarmBlinkMs", "blink_ms"),
        ["show_date"] = ("AlarmShowDate", "show_date"), ["show_time"] = ("AlarmShowTime", "show_time"),
        ["date_format"] = ("AlarmDateFormat", "date_format"),
        ["time_format"] = ("AlarmTimeFormat", "time_format"),
        ["order"] = ("AlarmOrder", "order"), ["normal_text"] = ("AlarmNormalText", "normal_text"),
        ["mode"] = ("AlarmMode", "mode"), ["filter"] = ("AlarmLevelFilter", "filter"),
        ["ack_on"] = ("AlarmAckOn", "ack_on"),
        ["show_unit"] = ("RecipeShowUnit", "show_unit"), ["series"] = ("ChartSeries", "series"),
        ["show_legend"] = ("ChartShowLegend", "show_legend"),
        ["level"] = ("MessageLevel", "level"),
        ["auto_hide_ms"] = ("MessageAutoHideMs", "auto_hide_ms"),
        ["marquee"] = ("MessageMarquee", "marquee"),
        ["visible_rows"] = ("RollerVisibleRows", "visible_rows"),
        ["encoding"] = ("AsciiEncoding", "encoding"), ["target"] = ("KeyboardTarget", "target"),
    };

    /// <summary>同一属性在不同类型上落到不同字段/键（键为 type.prop）。</summary>
    private static readonly Dictionary<string, (string Model, string Export)> Overrides =
        new(StringComparer.Ordinal)
        {
            ["lamp.color"] = ("IndicatorColor", "color"),
            ["spinner.color"] = ("IndicatorColor", "color"),
            ["pipe.color"] = ("IndicatorColor", "color"),
            ["bar.color"] = ("IndicatorColor", "color"),
            ["gauge.color"] = ("IndicatorColor", "color"),
            ["knob.color"] = ("IndicatorColor", "color"),
            ["slider.color"] = ("IndicatorColor", "color"),
            ["knob.arc_width"] = ("KnobArcWidth", "arc_width"),
            ["spinner.arc_width"] = ("SpinnerArcWidth", "arc_width"),
            ["knob.show_value"] = ("KnobShowValue", "show_value"),
            ["chart.show_value"] = ("ChartShowValue", "show_value"),
            ["knob.decimals"] = ("ValueDecimalPlaces", "decimals"),
            ["gauge.decimals"] = ("ValueDecimalPlaces", "decimals"),
            ["chart.y_decimals"] = ("ValueDecimalPlaces", "y_decimals"),
            ["input.decimals"] = ("ValueDecimalPlaces", "decimals"),
            ["message.text"] = ("MessageText", "text"),
            ["qrcode.text"] = ("QrText", "text"),
            ["enum_text.text"] = ("EnumTextValue", "text"),
            ["history_table.columns"] = ("HistTableColumns", "columns"),
            ["history_table.refresh_ms"] = ("HistTableRefreshMs", "refresh_ms"),
            ["history_table.source"] = ("HistTableGroup", "source"),
            ["recipe_table.columns"] = ("RecipeColumns", "columns"),
            ["recipe_table.source"] = ("RecipeGroup", "source"),
            ["recipe_table.row_height"] = ("RecipeRowHeight", "row_height"),
            ["recipe_table.scrollbar"] = ("RecipeScrollbar", "scrollbar"),
            ["alarm_list.source"] = ("AlarmGroupFilter", "source"),
            ["alarm_list.columns"] = ("AlarmColumns", "columns"),
            ["alarm_list.max_rows"] = ("AlarmMaxRows", "max_rows"),
            ["alarm_list.row_height"] = ("AlarmRowHeight", "row_height"),
            ["alarm_list.scrollbar"] = ("AlarmScrollbar", "scrollbar"),
            ["alarm_list.refresh_ms"] = ("AlarmRefreshMs", "refresh_ms"),
            ["alarm_list.grid"] = ("AlarmGrid", "grid"),
            ["alarm_bar.source"] = ("AlarmGroupFilter", "source"),
            ["history_curve.refresh_ms"] = ("RefreshMs", "refresh_ms"),
            ["history_curve.group"] = ("TrendGroup", "group"),
            ["history_curve.range"] = ("CurveRangeLastMin", "range"),
            ["ascii_display.max_len"] = ("AsciiMaxLen", "max_len"),
            ["pipe.direction"] = ("PipeDirection", "direction"),
            ["xy_curve.x"] = ("XyTagX", "x"),
            ["xy_curve.y"] = ("XyTagY", "y"),
            ["roller.options"] = ("RollerOptions", "options"),
            ["chart.kind"] = ("ChartKind", "kind"),
            // 曲线的背景/外框由 CurveBgColor / CurveBorderColor 表达（导出器的 MapCurveCommon 写同名键）
            ["realtime_curve.bg_color"] = ("CurveBgColor", "bg_color"),
            ["realtime_curve.border_color"] = ("CurveBorderColor", "border_color"),
            ["history_curve.bg_color"] = ("CurveBgColor", "bg_color"),
            ["history_curve.border_color"] = ("CurveBorderColor", "border_color"),
            // 曲线的时间轴方向是 CurveDirection（BarDirection 是进度条的方向）
            ["realtime_curve.direction"] = ("CurveDirection", "direction"),
            ["history_curve.direction"] = ("CurveDirection", "direction"),
        };

    /// <summary>运行时明确拒绝、面板不该给入口的项（键还在契约里，等着删）。</summary>
    private static readonly Dictionary<string, string> NotConsumable = new(StringComparer.Ordinal)
    {
        ["realtime_curve.direction"] = "widget_curve.c:397 只接受 lr（rl 直接拒绝整页）",
        ["history_curve.direction"] = "widget_curve.c:397 只接受 lr（rl 直接拒绝整页）",
        };

    /// <summary>公共行的编辑器在不同类型上指向不同控件（键为 type.prop）。</summary>
    private static readonly Dictionary<string, string> EditorOverrides = new(StringComparer.Ordinal)
    {
        ["realtime_curve.bg_color"] = "CurveBgColorBox",
        ["realtime_curve.border_color"] = "CurveBorderColorBox",
        ["history_curve.bg_color"] = "CurveBgColorBox",
        ["history_curve.border_color"] = "CurveBorderColorBox",
    };

    private static readonly Dictionary<string, string> SectionOf = new(StringComparer.Ordinal)
    {
        ["x"] = "geometry", ["y"] = "geometry", ["w"] = "geometry", ["h"] = "geometry",
        ["visible"] = "geometry", ["rotation"] = "geometry", ["mirror_x"] = "geometry",
        ["mirror_y"] = "geometry", ["opacity"] = "appearance", ["bg_color"] = "appearance",
        ["border_color"] = "appearance", ["border_width"] = "appearance", ["radius"] = "appearance",
        ["color"] = "appearance", ["font_size"] = "appearance", ["align"] = "appearance",
        ["valign"] = "appearance", ["font_style"] = "appearance", ["letter_spacing"] = "appearance",
        ["line_spacing"] = "appearance", ["long_mode"] = "appearance",
        ["color_on"] = "appearance", ["color_off"] = "appearance", ["knob_color"] = "appearance",
        // 可用与可见同处「位置与大小」：两者都是静态显隐，且「绑定与状态」整区在部分类型上不出现
        ["enabled"] = "geometry", ["tooltip"] = "advanced",
        ["checked"] = "behavior", ["pressed"] = "behavior", ["confirm"] = "behavior",
        ["hold_ms"] = "behavior", ["long_press_ms"] = "behavior", ["off_style"] = "behavior",
        ["commit"] = "behavior",
    };

    /// <summary>公共属性（widgets 的 common）。图元另有一套 figure_common，见 <see cref="FigureCommon"/>。</summary>
    private static readonly (string Key, string Editor, string Model, CoverageDecision Decision, string Evidence)[] CommonProps =
    {
        ("x", "PropX", "X", CoverageDecision.Closed, "ui_page.c place"),
        ("y", "PropY", "Y", CoverageDecision.Closed, "ui_page.c place"),
        ("w", "PropWidth", "Width", CoverageDecision.Closed, "ui_page.c place"),
        ("h", "PropHeight", "Height", CoverageDecision.Closed, "ui_page.c place"),
        ("visible", "PropVisible", "IsVisible", CoverageDecision.Closed, "widgets_common.c apply_common"),
        ("enabled", "PropEnabled", "IsEnabled", CoverageDecision.Closed, "widgets_common.c:310 + widget_states.c:281"),
        ("opacity", "OpacityBox", "Opacity", CoverageDecision.Closed, "widgets_common.c apply_common"),
        ("rotation", "RotationBox", "Rotation", CoverageDecision.Closed, "widgets_common.c apply_transform"),
        ("mirror_x", "MirrorHCheck", "MirrorX", CoverageDecision.Closed, "widgets_common.c apply_transform"),
        ("mirror_y", "MirrorVCheck", "MirrorY", CoverageDecision.Closed, "widgets_common.c apply_transform"),
        ("bg_color", "PropBgColor", "BgColor", CoverageDecision.Closed, "widgets_common.c apply_box_style"),
        ("border_color", "PropBorderColor", "BorderColor", CoverageDecision.Closed, "widgets_common.c apply_box_style"),
        ("border_width", "PropBorderWidth", "BorderWidth", CoverageDecision.Closed, "widgets_common.c apply_box_style"),
        ("radius", "PropRadius", "Radius", CoverageDecision.Closed, "widgets_common.c apply_box_style"),
        ("tooltip", "TooltipBox", "Tooltip", CoverageDecision.Closed, "ui_page.c:885"),
        ("z", null, "LayerIndex", CoverageDecision.ContractToDelete, "运行时没有读取点"),
        ("perm", null, "Permission", CoverageDecision.ContractToDelete,
            "运行时读控件节点的 perm（ui_page.c:124），不是 props.perm"),
        ("states", null, "VisualStates", CoverageDecision.Structural,
            "按变量条件的 states[] 由绑定表达，props.states 不导出"),
    };

    /// <summary>图元的公共属性（figure_common）。</summary>
    private static readonly (string Key, string? Editor)[] FigureCommon =
    {
        ("fill_color", "PropBgColor"),
        ("fill_opa", "FigureFillOpaBox"),
        ("border_color", "PropBorderColor"),
        ("border_width", "PropBorderWidth"),
        ("radius", "PropRadius"),
        ("points", "FigurePointsBox"),
        ("angle_start", "ArcStartBox"),
        ("angle_end", "ArcEndBox"),
        ("shade", null),
        ("static", null),
    };

    private static readonly Lazy<IReadOnlyList<PropertyCoverage>> Built = new(Build);

    public static IReadOnlyList<PropertyCoverage> All => Built.Value;

    public static IEnumerable<PropertyCoverage> ForType(string type) => All.Where(c => c.Type == type);

    public static PropertyCoverage? Find(string type, string property) =>
        All.FirstOrDefault(c => c.Type == type && c.Property == property);

    public static IReadOnlyList<string> WidgetTypes { get; } = WidgetTables.Keys.ToArray();

    /// <summary>契约里的类型名（snake_case）→ 编辑器的 <see cref="HmiControlType"/>。</summary>
    private static readonly Dictionary<string, HmiControlType> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["label"] = HmiControlType.Label,
        ["value"] = HmiControlType.Value,
        ["enum_text"] = HmiControlType.EnumText,
        ["button"] = HmiControlType.Button,
        ["switch"] = HmiControlType.Switch,
        ["checkbox"] = HmiControlType.CheckBox,
        ["spinner"] = HmiControlType.Spinner,
        ["keyboard"] = HmiControlType.Keyboard,
        ["tabview"] = HmiControlType.TabView,
        ["knob"] = HmiControlType.Knob,
        ["lamp"] = HmiControlType.Led,
        ["bar"] = HmiControlType.Bar,
        ["gauge"] = HmiControlType.Gauge,
        ["input"] = HmiControlType.TextArea,
        ["slider"] = HmiControlType.Slider,
        ["combobox"] = HmiControlType.Dropdown,
        ["image"] = HmiControlType.Image,
        ["shape"] = HmiControlType.Shape,
        ["state_image"] = HmiControlType.StateImage,
        ["table"] = HmiControlType.Table,
        ["qrcode"] = HmiControlType.QrCode,
        ["pipe"] = HmiControlType.Pipe,
        ["frame_anim"] = HmiControlType.FrameAnim,
        ["realtime_curve"] = HmiControlType.RealtimeCurve,
        ["history_curve"] = HmiControlType.HistoryCurve,
        ["xy_curve"] = HmiControlType.XyCurve,
        ["history_table"] = HmiControlType.HistoryTable,
        ["alarm_bar"] = HmiControlType.AlarmBar,
        ["alarm_list"] = HmiControlType.AlarmList,
        ["recipe_table"] = HmiControlType.RecipeTable,
        ["chart"] = HmiControlType.Chart,
        ["message"] = HmiControlType.Message,
        ["roller"] = HmiControlType.Roller,
        ["ascii_display"] = HmiControlType.AsciiDisplay,
    };

    /// <summary>契约类型名 → 编辑器控件类型（图元类型返回 false）。</summary>
    public static bool TryWidgetType(string type, out HmiControlType controlType) =>
        TypeNames.TryGetValue(type, out controlType);

    private static IReadOnlyList<PropertyCoverage> Build()
    {
        var list = new List<PropertyCoverage>();
        foreach (var (type, table) in WidgetTables)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in table.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = entry.IndexOf('=');
                var prop = eq < 0 ? entry : entry[..eq];
                var editor = eq < 0 ? "-" : entry[(eq + 1)..].Trim();
                list.Add(MakeWidget(type, prop, editor));
                seen.Add(prop);
            }

            foreach (var (key, editor, model, declared, evidence) in CommonProps)
            {
                // 契约的 common 与个别控件的 props 同名（xy_curve 的 x/y 既是控件几何也是
                // 变量轴）：JSON 里几何在节点根、轴在 props，同一个键串却两种含义。
                // 每个类型只能登记一条 —— 归控件专属那条（轴），几何由根字段用例覆盖。
                if (seen.Contains(key)) continue;
                // 公共属性的适用性**按面板同一套判据**（AppearanceSupport）算，不另抄一份名单：
                // 判据说"这类控件没有这一行"时结论是结构性，而不是"面板漏了"。
                var (decision, why) = ApplyCommonApplicability(type, key, declared, evidence);
                var actualEditor = decision == CoverageDecision.Closed
                    ? EditorOverrides.GetValueOrDefault($"{type}.{key}", editor)
                    : null;
                var (m2, e2) = Resolve(type, key);
                list.Add(new PropertyCoverage(type, key, SectionOf.GetValueOrDefault(key, "appearance"),
                    actualEditor, m2 ?? model, e2, decision, why));
            }
        }

        foreach (var kind in FigurePropertyCatalog.Kinds)
        {
            var type = $"figure:{kind}";
            foreach (var prop in FigurePropertyCatalog.For(kind))
            {
                list.Add(new PropertyCoverage(type, prop.Key, "figure", "Fig_" + prop.Key,
                    FigureModel(prop.Key), prop.Key, CoverageDecision.Closed, "ui_figure.c"));
            }

            if (kind == "container")
            {
                list.Add(new PropertyCoverage(type, "children", "figure", null, "Children", "children",
                    CoverageDecision.Structural, "子控件由画布拖入容器，属性面板不逐个建行"));
                list.Add(new PropertyCoverage(type, "layout", "figure", null, null, null,
                    CoverageDecision.ContractToDelete, "运行时按 free 处理，全仓没有读 layout 的地方"));
            }

            foreach (var (key, editor) in FigureCommon)
            {
                var decision = key is "shade" or "static"
                    ? CoverageDecision.ContractToDelete
                    : CoverageDecision.Closed;
                var evidence = decision == CoverageDecision.Closed
                    ? "ui_figure.c"
                    : "ui_figure.c 不读该键（契约删键）";
                list.Add(new PropertyCoverage(type, key, "figure", editor,
                    key == "fill_color" ? "BgColor" : FigureModel(key), key, decision, evidence));
            }

            // 公共属性里对图元成立的子集（尺寸/显隐/变换/提示/层级）。
            // 同名项（border_color/border_width/radius）已由 figure_common 登记；bg_color 与
            // fill_color 是同一个字段 —— 登记成结构性，指向 fill_color 那一行。
            foreach (var (key, editor, model, decision, evidence) in CommonProps)
            {
                if (key is "border_color" or "border_width" or "radius") continue;
                if (key == "bg_color")
                {
                    list.Add(new PropertyCoverage(type, key, "appearance", null, "BgColor", "fill_color",
                        CoverageDecision.Structural, "与 figure_common.fill_color 同一字段，面板只有一行"));
                    continue;
                }
                list.Add(new PropertyCoverage(type, key, SectionOf.GetValueOrDefault(key, "appearance"),
                    editor, model, key, decision, evidence));
            }
        }

        return list;
    }

    /// <summary>
    /// 公共属性的适用性：直接问 <see cref="AppearanceSupport"/>（面板与导出器共用的那张判据表）。
    /// 不适用 ⇒ 结构性（该类型没有这一行），适用 ⇒ 保持登记里声明的结论。
    /// </summary>
    private static (CoverageDecision Decision, string Evidence) ApplyCommonApplicability(
        string type, string key, CoverageDecision declared, string evidence)
    {
        if (!TypeNames.TryGetValue(type, out var t))
            return (declared, evidence);

        bool applies = key switch
        {
            "opacity" => AppearanceSupport.IsExported(t),
            "rotation" => AppearanceSupport.HasTransform(t),
            "mirror_x" or "mirror_y" => AppearanceSupport.HasMirror(t),
            "bg_color" => AppearanceSupport.HasBgColor(t),
            "border_color" or "border_width" or "radius" => AppearanceSupport.HasBoxBorder(t),
            _ => true,
        };

        return applies
            ? (declared, evidence)
            : (CoverageDecision.Structural, $"{type} 没有这一行（AppearanceSupport 判据不适用）");
    }

    private static PropertyCoverage MakeWidget(string type, string prop, string editor)
    {
        var key = $"{type}.{prop}";
        var (model, export) = Resolve(type, prop);

        if (NotConsumable.TryGetValue(key, out var blocked))
            return new PropertyCoverage(type, prop, SectionOf.GetValueOrDefault(prop, "data"),
                null, model, export, CoverageDecision.ContractToDelete, blocked);

        if (editor == "-")
        {
            if (NoEditor.TryGetValue(key, out var gap))
                return new PropertyCoverage(type, prop, SectionOf.GetValueOrDefault(prop, "data"),
                    null, model, export, gap.Decision, gap.Reason);

            return new PropertyCoverage(type, prop, SectionOf.GetValueOrDefault(prop, "data"),
                null, model, export, CoverageDecision.PanelToAdd,
                "未登记结论：契约有该属性，面板没有入口，运行时的读取点待核");
        }

        return new PropertyCoverage(type, prop, SectionOf.GetValueOrDefault(prop, "data"),
            editor, model, export, CoverageDecision.Closed, "见 docs/chenge-rt/52 的读取位置表");
    }

    private static (string? Model, string Export) Resolve(string type, string prop)
    {
        if (Overrides.TryGetValue($"{type}.{prop}", out var o)) return (o.Model, o.Export);
        if (ModelOf.TryGetValue(prop, out var m)) return (m.Model, m.Export);
        return (null, prop);
    }

    /// <summary>图元属性的模型字段（kind 级用 Figure 前缀，arc 的 min/max 走量程字段）。</summary>
    private static string FigureModel(string key) => key switch
    {
        "min" => "MinValue",
        "max" => "MaxValue",
        "fill_color" => "BgColor",
        "border_color" => "BorderColor",
        "border_width" => "BorderWidth",
        "radius" => "Radius",
        "points" => "Points",
        "angle_start" => "ArcAngleStart",
        "angle_end" => "ArcAngleEnd",
        "fill_opa" => "FigureFillOpa",
        _ => "Figure" + string.Concat(key.Split('_')
            .Select(p => p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..])),
    };
}
