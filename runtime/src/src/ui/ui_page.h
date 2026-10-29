/*
 * ui_page.h — 页面生命周期：建页、控件、绑定表、事件动作
 *
 * v1 单页常驻（无切页、无页面栈）：打开起始页 → 建控件 → 建绑定 →
 * 挂 100ms 刷新定时器。切页与页面栈后置（37 §6.14）。
 */
#ifndef HMI_UI_PAGE_H
#define HMI_UI_PAGE_H

#include <stddef.h>

#include "comm/sim.h"
#include "project/loader.h"
#include "ui/ui_binding.h"
#include "ui/widgets.h"

/* 动作（37 §6.7）：v1 支持写变量/写设备/取反/自加/切页/提示 */
enum {
    ACT_TAG = 0,      /* 写变量：Tag DB + 设备（sim / modbus） */
    ACT_DEVICE,       /* 直接写设备寄存器/线圈，不经过变量表 */
    ACT_TOGGLE_TAG,   /* 布尔取反 */
    ACT_ADD_VAR,      /* 变量加/减一个常量或另一个变量 */
    ACT_GOTO_PAGE,    /* 替换当前页 */
    ACT_OPEN_PAGE,    /* 压栈打开 */
    ACT_CLOSE_PAGE,   /* 关闭当前页 */
    ACT_BACK,         /* 返回上一页 */
    ACT_MESSAGE,      /* 显示提示框 */
    ACT_SET_BIT,      /* 把变量的某一位置 1 */
    ACT_CLEAR_BIT,    /* 把变量的某一位清 0 */
    ACT_LUA,          /* 执行一段 Lua（事件里的脚本） */
    ACT_ACK_ALARM,    /* 确认报警：tag_id 是规则 id，-1 = 该组全部；text 是组名 */
    ACT_SHELVE_ALARM, /* 搁置报警：tag_id 是规则 id（-1 = 该组全部），value 是分钟 */
    ACT_LOAD_RECIPE,  /* 下载配方：text = "组|配方名" */
    ACT_SAVE_RECIPE,  /* 存为新配方：text = "组|配方名"（名字空 = 自动编号） */
    ACT_NEW_RECIPE,   /* 新建配方：text = "组|" ，弹文本键盘起名 */
    ACT_EXPORT_RECIPES,  /* 导出全部配方到 U 盘 */
    ACT_IMPORT_RECIPES,  /* 从 U 盘导入配方 */
    ACT_SAVE_NOW,        /* 立即写存盘组（text 是组名，空 = 全部） */
    ACT_EXPORT_DATALOG,  /* 导出存盘组为 CSV：text = "组|last_min|from_ts|to_ts" */
    ACT_LOGIN,           /* 打开登录框（ADR-0012） */
    ACT_LOGOUT,          /* 清会话并重建当前页 */
    ACT_SET_LANGUAGE     /* 切语言并重建当前页（text 是语言代码） */
};

typedef struct {
    int    kind;
    int    tag_id;         /* ACT_TAG / ACT_TOGGLE_TAG / ACT_ADD_VAR / ACT_*_BIT */
    int    value_is_bool;
    int    value_bool;
    double value_num;
    int    bit;            /* ACT_SET_BIT / ACT_CLEAR_BIT 的位号 */
    int    dev;            /* ACT_DEVICE：devices[] 下标 */
    int    area, addr, datatype;
    char   page[64];       /* 切页动作的目标页 */
    double delta;          /* ACT_ADD_VAR 的常量增量 */
    int    from_tag;       /* ACT_ADD_VAR 的来源变量，-1 表示用 delta */
    char   text[160];      /* ACT_MESSAGE 的文本；ACT_LUA 的源码 */
    uint8_t perm_op;       /* 所属控件的 operate 掩码（ADR-0012），0 = 不限 */
} page_action_t;

/* 打开页面：解析 pages/<id>.json → 建屏与控件 → 建绑定表 → 挂事件。
 * 失败写 err（含文件与原因）返回 -1；调用方应转错误页。 */
/* 底部提示条（2 秒消失）：控件与动作都能用 */
void toast_show(const char* text);

/* 把存盘组导成 CSV 到 U 盘（HMI_USB_DIR 可覆盖挂载点，宿主/测试用）。
 * 动作 export_datalog 与 cg.exportDatalog 共用这一条路。
 * 返回数据行数（0 = 区间内没记录），-1 其它失败，-2 = 没插 U 盘。 */
int  hmi_datalog_export(const char* group, int last_min, uint32_t from_ts, uint32_t to_ts,
                        char* err, size_t errsz);

/* 重建当前页（登录/登出/空闲超时后让控件按新身份重算可见与禁用） */
int  page_mgr_reload(void);

/*
 * 立即按当前变量值把绑定刷一遍。
 * 设计画布只调 runtime_render_once（不跑 lv_timer_handler），少了这一步，
 * 键盘提交写进变量后框里还是旧文字。
 */
void ui_page_refresh_bindings(void);

/* 请求重建（下一个刷新周期才拆页）：切语言这类"在控件自己的事件里"发起的最安全；
 * toast 非空时在重建完成后显示（提示条属于页面，重建前弹会被拆掉） */
void page_mgr_request_reload(const char* toast);

int  page_mgr_open(const project_t* proj, sim_dev_t* sim, const char* page_id,
                   char* err, size_t errsz);

/* 关页：删刷新定时器与页面对象（退出或转错误页前调用） */
void page_mgr_close(void);

/* 页面栈操作码 */
enum { PAGE_OP_GOTO = 1, PAGE_OP_OPEN, PAGE_OP_CLOSE, PAGE_OP_BACK };

/* 页面栈操作：动作在事件回调里发起，实际切换推迟到刷新定时器（不能在事件里
 * 删掉自己所在的控件树）。page_id 对 CLOSE/BACK 可为 NULL。 */
void page_mgr_request(int op, const char* page_id);

/* 脚本（cg.*）用的入口：与动作走同一条写路径与页面栈，脚本绕不过权限与量程 */
int  page_mgr_write_tag(int tag_id, int is_bool, int b, double n);
int  page_mgr_script_open(const char* page_id);
int  page_mgr_script_close(void);
int  page_mgr_script_is_open(const char* page_id);

/* 脚本 cg.setDevice 用：area/datatype 名字映射与直接写设备 */
int  page_mgr_area_from_name(const char* name);
int  page_mgr_dt_from_name(const char* name);
int  page_mgr_write_device(const char* dev_id, int area, int addr, int datatype,
                           double num, int is_bool);

int  page_mgr_is_open(void);

/*
 * 设计画布的几何补丁：按控件 id 改位置/尺寸，**不重建控件**。
 *
 * 为什么不重建：控件的事件与绑定是在 build_page 尾部一次性挂上的
 * （parse_actions / bind_table_build），重建单个控件不会重跑那一段，结果是
 * 「颜色改对了，但按钮点不动」—— 这种退化比多花几十毫秒重载整页糟得多。
 * 所以属性变化一律走 page_mgr_reload；这里只承担拖动/缩放的高频几何。
 *
 * 内部自算几何的控件（仪表、曲线、表格…）改尺寸后内部不会立刻重排，
 * 拖动过程中看起来会短暂不对 —— 松手后由整页重载纠正。
 * 返回 0 成功；失败写 err（控件不存在 / 没有打开的页面）。
 */
int  ui_page_patch_rect(const char* widget_id, int x, int y, int w, int h,
                        char* err, size_t errsz);

/*
 * 用调用方推来的页面 JSON 重建当前页（设计画布的"静止精确"路径）。
 *
 * 不能走 page_mgr_reload：那是从宿主里的工程快照重读 pages/<id>.json，
 * 而设计画布拖出来的新坐标只在编辑器模型和快路径补丁里，快照还是旧的 ——
 * 用旧快照重建会把刚拖走的控件拉回原位。返回 0 成功。
 */
int  page_mgr_reload_json(const char* json, size_t len, char* err, size_t errsz);

#endif /* HMI_UI_PAGE_H */
