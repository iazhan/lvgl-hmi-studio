namespace HmiEditor.Core.Models;

public enum HmiControlType
{
    Screen,
    Button,
    Label,
    Slider,
    Switch,
    CheckBox,
    Image,
    Bar,
    Dropdown,
    TextArea,

    // Wave 2 新增
    Gauge,       // 仪表盘
    Knob,        // 旋钮
    Keyboard,    // 键盘
    Led,         // LED 指示灯
    Spinner,     // 加载动画
    TabView,     // 选项卡
    Line,        // 线条
    Table,       // 表格

    // I6 新增
    Svg,         // SVG 矢量图形(内嵌形状列表,见 HmiControl.SvgShapes)

    // I7 新增: 过程图形控件
    Tank,        // 罐体（垂直液位填充 + 刻度）
    Pipe,        // 管道（线缆 + 流向动画）
    Valve,       // 阀门（状态色切换,可交互）

    // I10 新增: 基础绘图图形（画布工具栏"画图"按钮创建）
    Rectangle,   // 矩形（填充 + 边框 + 圆角）
    Ellipse,     // 椭圆/圆（填充 + 边框）
    Polyline,    // 折线（顶点列表 HmiControl.Points，线色 BgColor、线宽 BorderWidth）
    Arc,         // 圆弧（角度范围 MinValue..MaxValue，当前值 Value；轨道色 BgColor、指示色 IndicatorColor/TextColor）

    // chenge-runtime 目标新增（导出契约见 docs/chenge-rt/37）
    Value,       // 数值显示（decimals/unit；绑定 value 后自动刷新）
    AlarmList,   // 报警列表（数据来自 alarms.json，可确认）
    HistoryCurve,  // 历史曲线（数据来自 trends.json / savecfg.json 的存盘组）
    RealtimeCurve, // 实时曲线（数据来自 sim 采样，按 sample_ms 滚动）
    AlarmBar,      // 报警条（单行滚动显示最新一条报警）,
    QrCode,        // 二维码（静态内容或绑字符串变量）
    Message,       // 消息条（info/warn/error，可自动消失）,
    EnumText,      // 枚举文本（值 → 文字/颜色，映射走绑定层）
    Roller,        // 滚轮选择,
    StateImage,    // 值 → 图片
    FrameAnim,     // 帧动画,
    Shape,         // 通用图元（kind 在属性里选，覆盖契约 40 种）,
    Chart,         // 图表（柱/饼/环，多序列）
    XyCurve,       // X-Y 散点/连线
    HistoryTable,  // 历史数据表
    RecipeTable,   // 配方表
    AsciiDisplay   // ASCII 串显示（GBK/ASCII 解码）
}
