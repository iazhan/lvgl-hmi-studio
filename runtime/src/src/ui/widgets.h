/*
 * widgets.h — v1 控件集：label / value / button / lamp
 *
 * 每种控件三件套（37 §5.6 的注册表思想）：按页面 JSON 创建、登记可绑定属性、
 * 值变化时刷新。新增控件 = 新增一个源文件 + widgets_build 里一个分支。
 * x/y/w/h 为绝对坐标（页面无布局，37 §6.1）。
 */
#ifndef HMI_WIDGETS_H
#define HMI_WIDGETS_H

#include "lvgl.h"
#include "expr.h"
#include "hmi_json.h"
#include "project/loader.h"
#include "data/trend_store.h"   /* history_table 要存 trend_rec_t 数组 */

#include <stddef.h>             /* offsetof：union 成员偏移的 static_assert 用 */

/* 一个图元最多几个面（管道接头：三层管 + 两圈法兰 + 8 个螺栓 = 13） */
#define SHAPE_FACES_MAX 16

typedef enum {
    W_LABEL = 0,
    W_VALUE,
    W_BUTTON,
    W_LAMP,
    W_SWITCH,
    W_BAR,
    W_GAUGE,
    W_SLIDER,
    W_ENUM_TEXT,
    W_INPUT,
    W_COMBOBOX,
    W_SHAPE,
    W_STATE_IMAGE,
    W_TABLE,
    W_QRCODE,
    W_REALTIME_CURVE,
    W_HISTORY_CURVE,
    W_XY_CURVE,
    W_CHART,
    W_ROLLER,
    W_ASCII_DISPLAY,
    W_PIPE,
    W_FRAME_ANIM,
    W_HISTORY_TABLE,
    W_ALARM_BAR,
    W_ALARM_LIST,
    W_RECIPE_TABLE,
    W_IMAGE,
    W_MESSAGE,
    W_CHECKBOX,
    W_SPINNER,
    W_KEYBOARD,
    W_TABVIEW,
    W_KNOB,
    W__COUNT
} wtype_t;

/* 每页控件上限来自 core/limits.h（构建期可调，--caps 报同一个值） */
#include "hmi_limits.h"
#define WIDGET_MAX_PER_PAGE HMI_WIDGETS_PER_PAGE

typedef struct state_img_s {
    double         v;
    char           src[48];
    lv_image_dsc_t dsc;
    void*          buf;
    int            loaded;
} state_img_t;

/* ── 状态（states，ADR-0005）：按条件覆盖一组外观属性 ── */
#define WSTATE_TEXT_MAX 72

typedef struct {
    /* 条件三选一 */
    int    kind;              /* 0 = 变量位，1 = 变量值相等，2 = 表达式 */
    int    tag;
    int    bit;
    double eq;
    expr_prog_t expr;
    int    has_expr;
    /* 覆盖项：has_* 表示这一项在状态里给了 */
    int    has_text;      char text[WSTATE_TEXT_MAX];
    int    has_color;     lv_color_t color;
    int    has_bg;        lv_color_t bg_color;
    int    has_border_color; lv_color_t border_color;
    int    has_border_width; int border_width;
    int    has_radius;    int radius;
    int    has_image;     char image[48];
    int    has_visible;   int visible;
    int    has_enabled;   int enabled;
    int    has_opacity;   int opacity;
} widget_state_t;

/* 建控件时的"基础外观"，没有状态命中时用它还原 */
typedef struct {
    char       text[WSTATE_TEXT_MAX];
    lv_color_t color;
    lv_color_t bg_color;
    lv_color_t border_color;
    int        border_width;
    int        radius;
    int        opacity;
    int        visible;
    int        enabled;
} wstate_base_t;

/* ── 各族控件的私有状态 ──
 * 一个控件只属于一族（type 决定），所以这些状态放在同一个 union 里：sizeof(widget_t)
 * 只按最大的那一族（curve）算，不必为 29 种控件的所有可能状态都留位置。
 * 访问写成 w->u.<族>.<字段>：直白、可 grep；跨族误读由 type 判断兜住（见 widget_dispose）。 */
typedef struct { lv_obj_t* cell[10 * 8]; } wt_table_t;      /* table：单元格 label（r*8+c） */

typedef struct {
    lv_chart_series_t* series[4];
    int  curve_tags[4];
    expr_prog_t curve_expr[4];      /* 该条曲线用表达式而不是单个变量时有效 */
    int  curve_has_expr[4];
    int  nseries;
    lv_obj_t* chart;                /* realtime_curve 的 chart（obj 是外层容器） */
    int  has_right_axis;            /* 用了右轴：右侧多一列刻度 */
    int  xy_xmin, xy_xmax;          /* xy_curve 的 X 量程（Y 量程用 min/max） */
    /* X 轴标签：按主分隔逐个标，clock 格式要每秒刷新，所以记住对象与下标 */
    lv_obj_t* xlabels[10];
    int  xlabel_k[10];
    int  nxlabels, xlabel_step;
    int  x_clock;                   /* 1 = 用时间格式（需要系统时钟） */
    int  x_left_grow;               /* 1 = 数据从左往右长（elapsed） */
    char x_fmt[8];                  /* "rel" / "clock" / "clock_short" */
    long xlabel_span;               /* X 轴总跨度（秒） */
    int  xlabel_total_div;          /* X 轴主分隔数 */
    /* 拖动浏览：环形缓冲保留 history_points 个点，可视窗口是其中一段 */
    int32_t* hist[4];
    int  hist_cap;                  /* 0 = 不保留历史 */
    int  hist_n, hist_head, view_offset;
    int  last_v[4];                 /* 求值失败时沿用上一个值，保持时间轴等间隔 */
    int  chart_w, bucket;           /* bucket：每个图点覆盖几个采样 */
    long t_ref, sample_ms;          /* t_ref：最新数据的时刻 */
    lv_timer_t* curve_timer;
    /* history_curve：从趋势文件读一段历史，定时刷新 */
    char tgroup[32];
    int  tchidx[4];                 /* 各曲线在趋势组里的通道号 */
    long span_min;
    int  trefresh_ms;
    lv_timer_t* ttimer;
    lv_obj_t* tinfo;                /* 「暂无历史数据」之类的提示 */
    lv_obj_t* browse_tag;
    int  dragged;                   /* 本次按下拖动过：松手的 CLICKED 不能当"回实时" */
    int  press_x0, press_offset;
} wt_curve_t;

typedef struct {
    /* shape 的图元：多边形/折线/椭圆/扇形的端点（已生成好，绘制时直接用） */
    lv_point_precise_t* poly_pts;
    int  poly_n;
    int  poly_closed;               /* 闭合（多边形/扇形） */
    int  have_fill;
    lv_color_t shape_fill, shape_border;
    int  shape_border_w;
    int  shape_fill_opa;            /* 0 = 不填充 */
    int  shape_is_custom;           /* 1 = 走自绘回调（图元族的几何绘制） */
    /* 复合图元（cube / 管道 / 平面）：多面共用顶点缓冲，逐面填充与描边 */
    int  nfaces;
    int  stroke_first_only;
    int  face_begin[SHAPE_FACES_MAX];
    int  face_len[SHAPE_FACES_MAX];
    lv_color_t face_fill[SHAPE_FACES_MAX];
    /* arc 的进度弧（契约 figures.arc 的 min/max + 图元 angle_start/angle_end）：
     * 环宽在建控件时记下来（>0 = 这是 arc 图元），bind.value 刷新时只改 INDICATOR 段。
     * arc_progress = 0 时 INDICATOR 宽度为 0 —— 没有 value 绑定的老工程逐像素不变。 */
    int  arc_w;
    int  arc_progress;
} wt_shape_t;

typedef struct {
    /* 二次确认（props.confirm）：点下去先弹框，确认了才执行动作 */
    char cf_text[120];              /* 提示文字，可写 @id */
    char cf_yes[32], cf_no[32];
    int  cf_wait_ms;                /* >0 时超时自动取消 */
    uint32_t press_tick;            /* 本次按下的时刻（lv_tick） */
    /* 按下态：文字与颜色要手工换，松开后恢复 */
    char last_text[128];            /* 最近一次显示的文本（按钮文字上限 32 字符，中文可到 96 字节） */
    char press_text[80];            /* 按下时显示的文本，空表示不变 */
    lv_color_t text_normal, text_pressed;
    int  has_text_pressed;
} wt_button_t;

typedef struct {
    /* 列定义在拆页前解析成数组；记录读进内存，滚动时不再读盘。
     * 行对象只建「可见行 + 2」，滚动时改起始行重排重填。 */
    char tgroup[32];                /* 读哪个趋势组 */
    int  ht_ncol;
    int  ht_chidx[8];               /* -2 = 时间列，其它 = 趋势组通道号 */
    int  ht_colw[8];
    int  ht_dec[8];
    int  ht_row_h;
    int  ht_total, ht_vis, ht_first, ht_max_rows;
    int  ht_refresh_ms;             /* 0 = 只读一次 */
    long ht_last_min;
    trend_rec_t* ht_recs;           /* 最新的在前；拆页时释放 */
    int  ht_nrec;
    lv_obj_t* ht_rows, * ht_spacer;
    lv_timer_t* ht_timer;
} wt_ht_t;

typedef struct {
    int  style;                     /* 0=arc，1=pointer */
    int  cx, cy, radius;
    int  needle_len;                /* 指针长度百分比（100 = 顶到弧），默认 80；见 props.needle_len */
    lv_point_precise_t pts[2];      /* 指针的线端点（lv_line 不复制点） */
    lv_point_precise_t* tick_pts;   /* 刻度线端点：tick_count 条，每条 2 点 */
    int  tick_count;
    lv_obj_t* aux2;                 /* pointer 的数值标签 */
    lv_obj_t* aux3;                 /* pointer 的进度弧 */
} wt_gauge_t;

typedef struct {
    lv_image_dsc_t dsc;             /* 描述符必须与控件同寿命 */
    void* img_buf;                  /* 像素缓冲，拆页时 free */
    state_img_t* states;            /* state_image / frame_anim：值 → 图片、帧序列 */
    int  nstates, cur_state;
    /* frame_anim：帧序列复用 states，这里只记播放状态（与 image 同族，放在一起） */
    int  anim_loop, anim_idx;
    lv_timer_t* anim_timer;
} wt_image_t;

typedef struct { char text_on[20], text_off[20]; } wt_switch_t;   /* 通断文字 */

/* 管道走向点数上限（契约 points 2..64）。给了路径就按折线画，相位沿整条路径累计。 */
#define WT_PIPE_PATH_MAX 64

/* ⚠ 字段顺序是**契约的一部分**：wt_pipe_t 与 wt_shape_t 在同一个 union 里，
 * 而 shape 族的前 sizeof(wt_shape_t) 字节（poly_pts/poly_n/shape_fill/shape_border/…）
 * 在控件建好后仍会被通用外观代码改写 —— 实测 path[2].y 落在 shape_border 的偏移上，
 * 管径/端点会被写成颜色值（画出来要么一整片色块、要么竖腿消失）。所以整块 reserve 出
 * shape 的宽度，pipe 自己的数据一律排在它后面；下面的 static_assert 守住这条。 */
typedef struct {
    unsigned char reserve[sizeof(wt_shape_t)];   /* 让位给 shape 族（union 本就这么大） */
    /* 路径模式（props.points 给了 ≥2 点）：直管两点、弯管三点、多段 N 点。
     * 流动相位按**总弧长**累计，拐点处不重置 —— 这是"沿整条路径走"的唯一实现方式。 */
    lv_point_precise_t path[WT_PIPE_PATH_MAX];
    float seg_len[WT_PIPE_PATH_MAX];   /* seg_len[i] = path[i] → path[i+1] */
    float path_len;
    int  path_n;
    int  flow_dir;                  /* 0 = 横向，1 = 纵向 */
    int  flow_rev;                  /* 1 = 反向流动（契约 pipe.flow_dir=reverse） */
    int  flow_ms, flow_wall, flow_ofs;
    int  flow_w;                    /* 管径（外径像素）：路径模式由路径与框算出 */
    int  flow_on;                   /* 0 = 停（value 绑定的变量为 0） */
    lv_color_t flow_color, flow_chan, flow_wall_c;
    lv_timer_t* flow_timer;
} wt_pipe_t;

_Static_assert(offsetof(wt_pipe_t, path) >= sizeof(wt_shape_t),
               "pipe 的数据必须落在 shape 字段之外（见上面的 union 说明）");
_Static_assert(offsetof(wt_pipe_t, flow_dir) >= sizeof(wt_shape_t),
               "pipe 的标量字段必须落在 shape 字段之外（见上面的 union 说明）");

/* tabview：各页签的内容页对象（子控件挂在这些页上，坐标相对内容区） */
#define WT_TAB_MAX 8
typedef struct {
    lv_obj_t* pages[WT_TAB_MAX];
    int       npages;
} wt_tab_t;

typedef struct { int msg_hide_ms; lv_timer_t* msg_timer; } wt_msg_t;
typedef struct { int text_max, text_gbk; } wt_ascii_t;
typedef struct { lv_point_precise_t arrow_pts[4]; } wt_combo_t;   /* 下拉箭头（三角形） */

typedef struct {
    wtype_t  type;
    lv_obj_t* obj;
    lv_obj_t* aux;          /* 文本/标签类控件要改的那个对象（按钮的文字、switch 的通断字） */
    int  bind_tag;          /* 绑在 text/value/checked 上的变量，写回时用 */
    int  opt_tag;           /* combobox 的 options_tag：-1 表示静态选项 */
    int  gate_tag;          /* frame_anim：bind.running 指定的变量（<0 = 不受控） */
    /* value/input 的附加显示属性（bind 求值时拼接）；bar/gauge/slider 的量程也在这 */
    char unit[16];
    char prefix[16];                /* value 的前缀（如 ¥）；见 props.prefix */
    char suffix[16];                /* value 的后缀（如 %）；见 props.suffix */
    int  decimals;
    int  min, max;
    double step;            /* slider 的步长；0 表示连续 */
    int  has_min, has_max, max_len;
    int  input_text;        /* input：1 = text/password 模式，0 = 数值 */
    int  input_blur;        /* input：1 = commit=blur（这次输入结束就写，含取消） */
    /* 权限（ADR-0012）：组位掩码，0 = 不限。建控件时定初值，动作执行前再判一次 */
    uint8_t pview;          /* 能看见的组 */
    uint8_t poper;          /* 能操作的组 */
    int  off_hide;          /* enabled=0 时隐藏（1）而不是变灰（0）——任何控件都适用 */
    /* 点击闸门：click 要看按住多久、长按是否已经执行过（通用，不属某一族） */
    int  hold_ms;           /* >0 时要按住这么久才算一次 click */
    int  long_press_ms;     /* 按住超过此时长算长按，300..5000 */
    int  lp_slot;           /* long_press 的动作槽下标，-1 = 没配 */
    int  lp_fired;          /* 本次按下已判成长按：松开时那次 click 要吞掉 */
    int  tip_shown;         /* 本次按下已弹出 tooltip：click 同样要吞掉 */
    char tip[64];           /* props.tooltip 的副本（页面 JSON 建完就释放） */
    void* cdata;            /* 控件私有的堆数据（chart / alarm_list / recipe_table） */
    /* states（ADR-0005）：命中哪条就按它的覆盖值改外观，没命中就还原基础值。
     * 任何控件都能配，所以留在核心。 */
    widget_state_t* wstates;
    int             nwstates;
    int             cur_wstate;    /* -1 = 没有状态命中 */
    wstate_base_t   st_base;
    int             st_captured;
    union {
        wt_table_t  table;
        wt_curve_t  curve;
        wt_shape_t  shape;
        wt_button_t btn;
        wt_ht_t     ht;
        wt_gauge_t  gauge;
        wt_image_t  img;
        wt_switch_t sw;
        wt_pipe_t   pipe;
        wt_msg_t    msg;
        wt_ascii_t  ascii;
        wt_combo_t  combo;
        wt_tab_t    tab;
    } u;
} widget_t;

/* 每个刷新周期调一次：按变量版本求状态，命中变化时改写外观（ADR-0005） */
int  ui_widget_states_apply(widget_t* w);

/* "#RRGGBB" → lv_color_t；非法返回 LV_COLOR_MAKE(0,0,0) 并由调用方报错 */
lv_color_t ui_color_hex(const char* s);
int ui_color_valid(const char* s);

/* state_image：按变量值切图；返回 0 成功，-1 表示加载失败（保留当前图） */
int  ui_state_image_select(widget_t* w, double v);
/* history_table：拆页时释放读进来的趋势记录 */
void history_table_dispose(widget_t* w);

/* 拆页时释放一个控件占用的堆内存与定时器（按 type 分派到各族的清理） */
void widget_dispose(widget_t* w);

/* pipe：流动开关（value 绑定的变量为 0 时停住） */
void ui_pipe_set_flow(widget_t* w, int on);

/* ascii_display：把设备原始字节按控件声明的 encoding 转 UTF-8 后显示 */
void ui_ascii_display_set(widget_t* w, const char* raw, int n, int quality);

/* message 消息栏：换文字并重新计时（auto_hide_ms>0 时到点清空） */
void ui_message_set_text(widget_t* w, const char* text);

/* 趋势数据根目录（其下 trends/）。main 启动时按环境/板级设置一次 */
void ui_set_trend_dir(const char* dir);


/* 建控件时挂到哪个父对象上（容器里的子控件用）；传 NULL 恢复"挂屏幕" */
void ui_set_parent(lv_obj_t* parent);

/* ui_figure.c 建图元对象时要用的父对象与摆放规则（与其它控件保持一致） */
lv_obj_t* ui_cur_parent(void);
void ui_place(lv_obj_t* o, long x, long y, long ww, long hh);

/* 可滚动数据区的统一外观（竖向滚动 + 6px 圆角滚动条）：
 * history_table / recipe_table / alarm_list 共用 */
void ui_scroll_area(lv_obj_t* o, int scrollbar);

/* 容器子控件的生命周期：建页前 reset，拆页时释放可能占用的像素缓冲 */
void ui_children_reset(void);
void ui_children_free_images(void);

/* 当前工程（combobox 的 options_tag 要查变量的 enum 定义）；建页前由 page_mgr 设置 */
void ui_set_project(const project_t* p);

/* 页面资源根目录（<工程根>/assets/img/ 的上一级）；建页前由 page_mgr 设置 */
void ui_set_asset_root(const char* root);

/* 控件类型名字符串 → 枚举；不认识返回 -1 */
int widget_type_from_str(const char* s, wtype_t* out);

/* 该控件依赖的工程能力（manifest.features 取值）；无依赖返回 NULL */
const char* schema_widget_requires(const char* type);

/* 仪表统一入口：arc 样式设弧长 + 数值标签，pointer 样式算指针角度 */
void ui_gauge_set(widget_t* w, double v);

/* 旋钮：绑定刷新与动作走这里（钳到量程、更新弧与中心读数） */
void ui_knob_set(widget_t* w, double v);

/* 圆弧的进度弧（契约 figures.arc：min/max + angle_start/angle_end，由 bind.value 驱动）。
 * 第一次被调用时才把 INDICATOR 段打开，所以没有 value 绑定的图元观感不变。 */
void ui_arc_set_value(widget_t* w, double v);

/* 指针式仪表：按当前值算指针角度并更新线段端点 */
void ui_gauge_pointer_set(widget_t* w, double v);

/* 按一个控件 JSON 项创建控件。失败写 err（含控件 id 与原因）返回 -1。 */
int widget_build(const hmi_json_t* w, widget_t* out, char* err, size_t errsz);

#endif /* HMI_WIDGETS_H */
