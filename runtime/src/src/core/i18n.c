/*
 * i18n.c — 多语言（37 §6.10）
 *
 * 只加载当前语言的文本；切语言由调用方重建页面（与 set_theme 同口径）。
 * 文本里的 {tag:<id>|<fmt>} 在渲染时求值，含占位符的控件由 page_mgr 定时重算。
 */
#include "i18n.h"
#include "sysvar.h"
#include "tagdb.h"
#include "vfs.h"
#include "hmi_json.h"
#include "hmi_log.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#define I18N_MAX_TEXTS 2000
#define I18N_TEXT_MAX  160
#define I18N_ID_MAX    64

typedef struct {
    char id[I18N_ID_MAX];
    char vals[I18N_MAX_LANGS][I18N_TEXT_MAX];
} i18n_text_t;

static char        s_lang_code[I18N_MAX_LANGS][8];
static char        s_lang_name[I18N_MAX_LANGS][32];
static int         s_nlangs;
static int         s_cur;
static int         s_def;                     /* default 语言下标 */
static i18n_text_t* s_texts;
static int         s_ntexts;
static char        s_state[256];

static const char* lookup(const char* id);     /* 定义在后面 */

int i18n_enabled(void)
{
    return s_nlangs > 0;
}

int i18n_lang_count(void)
{
    return s_nlangs;
}

const char* i18n_lang_code(int i)
{
    if (i < 0 || i >= s_nlangs) return "";
    return s_lang_code[i];
}

const char* i18n_lang_name(int i)
{
    if (i < 0 || i >= s_nlangs) return "";
    return s_lang_name[i];
}

const char* i18n_lang(void)
{
    if (s_cur < 0 || s_cur >= s_nlangs) return "zh";
    return s_lang_code[s_cur];
}

static int lang_index(const char* code)
{
    if (!code || !*code) return -1;
    for (int i = 0; i < s_nlangs; i++)
        if (strcmp(s_lang_code[i], code) == 0) return i;
    return -1;
}

static void mkdir_p(const char* path)
{
    char tmp[256];
    size_t n;

    snprintf(tmp, sizeof(tmp), "%s", path);
    n = strlen(tmp);
    for (size_t i = 1; i < n; i++) {
        if (tmp[i] != '/') continue;
        tmp[i] = '\0';
        mkdir(tmp, 0755);
        tmp[i] = '/';
    }
    mkdir(tmp, 0755);
}

static void save_lang(void)
{
    FILE* f;

    if (!s_state[0]) return;
    {
        /* 父目录自己建（不能指望调用方）；
         * 老版本把文件路径喂给 mkdir_p，设备上可能留了个同名空目录，先清掉 */
        char dir[256];
        struct stat st;

        snprintf(dir, sizeof(dir), "%s", s_state);
        {
            char* slash = strrchr(dir, '/');

            if (slash) {
                *slash = '\0';
                mkdir_p(dir);
            }
        }
        if (stat(s_state, &st) == 0 && S_ISDIR(st.st_mode)) (void)rmdir(s_state);
    }
    f = fopen(s_state, "wb");
    if (!f) {
        HMI_LOG("i18n: 语言状态写不了（%s）", strerror(errno));
        return;
    }
    fprintf(f, "%s\n", i18n_lang());
    fclose(f);
}

static void load_saved_lang(void)
{
    FILE* f = s_state[0] ? fopen(s_state, "rb") : NULL;
    char buf[16] = { 0 };

    if (!f) return;
    if (fgets(buf, sizeof(buf), f)) {
        char* p = strpbrk(buf, "\r\n");

        if (p) *p = '\0';
    }
    fclose(f);
    if (buf[0]) {
        int i = lang_index(buf);

        if (i >= 0) s_cur = i;
    }
}

int i18n_load(const project_t* p, const char* data_dir, char* err, size_t errsz)
{
    char path[320];
    char* text = NULL;
    long flen = 0;
    FILE* f;
    hmi_json_t* root;
    const hmi_json_t* langs;
    const hmi_json_t* texts;
    const char* def;
    size_t nl, nt;

    free(s_texts);
    s_texts = NULL;
    s_ntexts = 0;
    s_nlangs = 0;
    s_cur = -1;
    s_def = 0;
    s_state[0] = '\0';
    if (data_dir && *data_dir) snprintf(s_state, sizeof(s_state), "%s/state/language", data_dir);

    if (!p) return 0;
    snprintf(path, sizeof(path), "%s/strings.json", p->root);
    {
        /* 内存表优先（预览：工程还没落盘，见 core/vfs.h） */
        size_t vlen = 0;
        const void* vmem = vfs_get(path, &vlen);

        if (vmem) {
            if (vlen == 0 || vlen > 512 * 1024) {
                snprintf(err, errsz, "strings.json 大小异常（%zu 字节）", vlen);
                return -1;
            }
            flen = (long)vlen;
            text = malloc(vlen + 1);
            if (!text) {
                snprintf(err, errsz, "strings.json 内存不足");
                return -1;
            }
            memcpy(text, vmem, vlen);
            text[vlen] = '\0';
        } else {
            f = fopen(path, "rb");
            if (!f) {
                load_saved_lang();
                return 0;                      /* 没有 strings.json = 不启用多语言 */
            }
            fseek(f, 0, SEEK_END);
            flen = ftell(f);
            fseek(f, 0, SEEK_SET);
            if (flen <= 0 || flen > 512 * 1024) {
                fclose(f);
                snprintf(err, errsz, "strings.json 大小异常（%ld 字节）", flen);
                return -1;
            }
            text = malloc((size_t)flen + 1);
            if (!text) {
                fclose(f);
                snprintf(err, errsz, "strings.json 内存不足");
                return -1;
            }
            if (fread(text, 1, (size_t)flen, f) != (size_t)flen) {
                free(text);
                fclose(f);
                snprintf(err, errsz, "strings.json 读失败");
                return -1;
            }
            text[flen] = '\0';
            fclose(f);
        }
    }

    root = hmi_json_parse(text, (size_t)flen, err, errsz);
    if (!root) {
        free(text);
        return -1;
    }

    langs = hmi_json_get(root, "languages");
    nl = hmi_json_count(langs);
    if (hmi_json_type(langs) != HMI_JSON_ARR || nl < 1 || nl > I18N_MAX_LANGS) {
        snprintf(err, errsz, "strings.json 的 languages 项数应为 1..%d", I18N_MAX_LANGS);
        hmi_json_free(root);
        free(text);
        return -1;
    }
    for (size_t i = 0; i < nl; i++) {
        const hmi_json_t* l = hmi_json_at(langs, i);
        const char* code = hmi_json_str(hmi_json_get(l, "code"), NULL);
        const char* name = hmi_json_str(hmi_json_get(l, "name"), NULL);

        if (!code || !*code || strlen(code) >= sizeof(s_lang_code[0])) {
            snprintf(err, errsz, "strings.json 第 %zu 个语言 code 非法", i);
            hmi_json_free(root);
            free(text);
            return -1;
        }
        snprintf(s_lang_code[i], sizeof(s_lang_code[i]), "%s", code);
        snprintf(s_lang_name[i], sizeof(s_lang_name[i]), "%s",
                 (name && *name) ? name : code);
    }
    s_nlangs = (int)nl;

    def = hmi_json_str(hmi_json_get(root, "default"), NULL);
    s_def = lang_index(def);
    if (s_def < 0) {
        snprintf(err, errsz, "strings.json 的 default=%s 不在 languages 里", def ? def : "(缺失)");
        hmi_json_free(root);
        free(text);
        s_nlangs = 0;
        return -1;
    }
    /* 与 manifest.default_language 不一致时以 manifest 为准（37 §6.10） */
    if (p->manifest.default_language[0]) {
        int mi = lang_index(p->manifest.default_language);

        if (mi < 0) {
            HMI_LOG("i18n: manifest.default_language=%s 不在 strings.json 里，用 %s",
                    p->manifest.default_language, s_lang_code[s_def]);
        } else if (mi != s_def) {
            HMI_LOG("i18n: manifest.default_language=%s 与 strings.json 的 default=%s 不一致，"
                    "按 manifest", p->manifest.default_language, s_lang_code[s_def]);
            s_def = mi;
        }
    }
    s_cur = s_def;

    texts = hmi_json_get(root, "texts");
    nt = hmi_json_count(texts);
    if (hmi_json_type(texts) != HMI_JSON_ARR || nt > I18N_MAX_TEXTS) {
        snprintf(err, errsz, "strings.json 的 texts 项数 %zu 超过上限 %d", nt, I18N_MAX_TEXTS);
        hmi_json_free(root);
        free(text);
        s_nlangs = 0;
        return -1;
    }
    s_texts = calloc(nt ? nt : 1, sizeof(*s_texts));
    if (!s_texts) {
        hmi_json_free(root);
        free(text);
        s_nlangs = 0;
        snprintf(err, errsz, "strings.json 内存不足（%zu 条）", nt);
        return -1;
    }
    for (size_t i = 0; i < nt; i++) {
        const hmi_json_t* t = hmi_json_at(texts, i);
        const char* id = hmi_json_str(hmi_json_get(t, "id"), NULL);

        if (!id || !*id || strlen(id) >= I18N_ID_MAX) {
            snprintf(err, errsz, "strings.json 第 %zu 条 id 非法", i);
            hmi_json_free(root);
            free(text);
            s_nlangs = 0;
            return -1;
        }
        for (int k = 0; k < s_ntexts; k++)
            if (strcmp(s_texts[k].id, id) == 0) {
                snprintf(err, errsz, "strings.json 的 id %s 重复", id);
                hmi_json_free(root);
                free(text);
                s_nlangs = 0;
                return -1;
            }
        snprintf(s_texts[s_ntexts].id, I18N_ID_MAX, "%s", id);
        for (int li = 0; li < s_nlangs; li++) {
            const char* v = hmi_json_str(hmi_json_get(t, s_lang_code[li]), NULL);

            if (!v && li == s_def) {
                HMI_LOG("i18n: %s 缺 %s（默认语言）文本，将显示 id", id, s_lang_code[li]);
            }
            snprintf(s_texts[s_ntexts].vals[li], I18N_TEXT_MAX, "%s", v ? v : "");
        }
        s_ntexts++;
    }
    hmi_json_free(root);
    free(text);

    load_saved_lang();
    HMI_LOG("i18n: %d 种语言 %d 条文本，当前 %s", s_nlangs, s_ntexts, i18n_lang());
    return 0;
}

int i18n_switch(const char* code, char* err, size_t errsz)
{
    int i = lang_index(code);

    if (i < 0) {
        snprintf(err, errsz, "没有语言 %s", code ? code : "(空)");
        return -1;
    }
    if (i == s_cur) return 0;
    s_cur = i;
    {
        /* 只建父目录：直接把文件路径喂给 mkdir_p 会把 language 建成目录 */
        char dir[256];

        snprintf(dir, sizeof(dir), "%s", s_state);
        {
            char* slash = strrchr(dir, '/');

            if (slash) {
                *slash = '\0';
                mkdir_p(dir);
            }
        }
    }
    save_lang();
    HMI_LOG("i18n: 切到 %s", i18n_lang());
    return 0;
}

int i18n_has_placeholder(const char* s)
{
    if (!s) return 0;
    if (strstr(s, "{tag:")) return 1;
    /* "@id" 也要看它背后的文本有没有占位符 */
    if (s[0] == '@') {
        const char* v = lookup(s + 1);

        return v && strstr(v, "{tag:") != NULL;
    }
    return 0;
}

const char* i18n_lookup(const char* id)
{
    return (id && *id) ? lookup(id) : NULL;
}

static const char* lookup(const char* id)
{
    for (int i = 0; i < s_ntexts; i++)
        if (strcmp(s_texts[i].id, id) == 0) {
            const char* v = s_texts[i].vals[s_cur];

            if (v && *v) return v;
            return s_texts[i].vals[s_def];
        }
    return NULL;
}

/* 变量是不是字符串型（系统变量看声明，普通变量看有没有字符串缓冲） */
static int tag_is_string(int id)
{
    int st = sysvar_type(id);

    if (st >= 0) return st == TAG_T_STRING;
    if (!tagdb_valid(id)) return 0;
    {
        const tag_t* t = tagdb_at(id);

        return t && t->scap > 0;
    }
}

/* 原样拷贝，超长截断：比 snprintf("%s") 少了编译器的截断告警，也不会写出界 */
static void copy_text(char* out, size_t outsz, const char* s)
{
    size_t n;

    if (!out || outsz == 0) return;
    if (!s) s = "";
    n = strlen(s);
    if (n > outsz - 1) n = outsz - 1;
    if (n > 0) memcpy(out, s, n);
    out[n] = '\0';
}

/* {tag:12} / {tag:12|%.2f} / {tag:12|%s|未登录}：第三段是"值为空时"的兜底文本，以 @ 开头取 strings.json */
static void expand_tag(const char* spec, size_t n, char* out, size_t outsz)
{
    char body[128];
    char* bar;
    char* empty_txt = NULL;
    long id;
    char* end = NULL;

    if (n >= sizeof(body)) n = sizeof(body) - 1;
    memcpy(body, spec, n);
    body[n] = '\0';
    bar = strchr(body, '|');
    if (bar) {
        char* fb;

        *bar = '\0';
        fb = strchr(bar + 1, '|');
        if (fb) {
            *fb = '\0';
            empty_txt = fb + 1;
        }
    }
    id = strtol(body, &end, 10);
    if (!end || *end != '\0' || id < 0) {
        copy_text(out, outsz, body);          /* 坏 id：原样吐回去（与旧行为一致） */
        return;
    }
    if (bar && strchr(bar + 1, 's')) {
        char sbuf[96] = { 0 };

        (void)tagdb_str_copy((int)id, sbuf, (int)sizeof(sbuf), NULL);
        copy_text(out, outsz, sbuf);
    } else if (bar && (strchr(bar + 1, 'd') || strchr(bar + 1, 'i') || strchr(bar + 1, 'u') ||
                       strchr(bar + 1, 'x') || strchr(bar + 1, 'X') || strchr(bar + 1, 'o'))) {
        snprintf(out, outsz, bar + 1, (long long)tagdb_num((int)id));
    } else if (bar) {
        snprintf(out, outsz, bar + 1, tagdb_num((int)id));   /* 空 fmt 也走这里，结果为空串 */
    } else if (tag_is_string((int)id)) {
        /* 省略 <fmt>：数值按 %g、字符串按原样（37 §6.10）。字符串按 %g 会显示成 0 */
        char sbuf[96] = { 0 };

        (void)tagdb_str_copy((int)id, sbuf, (int)sizeof(sbuf), NULL);
        copy_text(out, outsz, sbuf);
    } else {
        snprintf(out, outsz, "%g", tagdb_num((int)id));
    }
    if (out[0] == '\0' && empty_txt && *empty_txt) {
        const char* text = (empty_txt[0] == '@') ? lookup(empty_txt + 1) : empty_txt;

        if (empty_txt[0] == '@' && !text) text = empty_txt + 1;
        copy_text(out, outsz, text);
    }
}

/* ── 运行时界面文本的内置表（zh / en） ──
 * key 与 login_dialog / keypad / page_mgr 里的调用点一一对应；
 * 工程可在 strings.json 里用 "runtime.<key>" 覆盖（现场改措辞或补第三种语言）。 */
typedef struct {
    const char* key;
    const char* zh;
    const char* en;
} rt_text_t;

static const rt_text_t RT[] = {
    /* 登录框 */
    {"login.title",      "选择用户登录",            "Sign in"},
    {"login.change",     "改密码",                  "Change password"},
    {"login.cancel",     "取消",                    "Cancel"},
    {"login.close",      "关闭",                    "Close"},
    {"login.pick",       "改密码：选择用户",        "Change password: pick a user"},
    {"login.pick_tip",   "选中后先验证旧口令",      "Old password is checked first"},
    {"login.more",       "（列表可上下滑动）",      "（scroll for more）"},
    {"login.no_pwd",     "（免密）",                " (no password)"},
    {"login.row_change", "%s  → 改密码",            "%s  → change password"},
    {"login.kb_fail",    "键盘打不开",              "Keypad unavailable"},
    {"login.locked",     "失败次数过多，请 %d 分钟后再试", "Too many attempts, try again in %d min"},
    {"login.bad",        "用户名或密码错",          "Wrong user name or password"},
    /* 改密码 */
    {"pwd.old",          "旧口令",                  "Old password"},
    {"pwd.new",          "新口令（%d~16 位）",      "New password (%d-16)"},
    {"pwd.again",        "再次输入新口令",          "Repeat new password"},
    {"pwd.old_bad",      "旧口令不对",              "Wrong old password"},
    {"pwd.mismatch",     "两次输入不一致，请重新输入新口令", "Passwords differ, try again"},
    {"pwd.no_password",  "该用户是免密账号：改密请在工程 users.json 里配置口令",
                         "This account has no password; set one in users.json"},
    {"pwd.done",         "口令已修改，请用新口令登录", "Password changed, sign in again"},
    /* 键盘 */
    {"kp.bad_num",       "不是合法数值",            "Not a number"},
    {"kp.empty",         "内容不能为空",            "Cannot be empty"},
    {"kp.too_long",      "已到长度上限",            "Length limit reached"},
    {"kp.too_short",     "至少 %d 位",              "At least %d chars"},
    {"kp.bad_char",      "该位不能输入这个字符",    "Character not allowed here"},
    {"kp.ascii_only",    "只能输入 A-Z 0-9 . - _",  "Only A-Z 0-9 . - _"},
    {"kp.need_num",      "请输入数值",              "Enter a number"},
    /* 提示条 */
    {"toast.no_perm",    "无权限",                  "No permission"},
    {"toast.logged_out", "已登出",                  "Signed out"},
    {"toast.lang",       "语言已切换",              "Language switched"},
    {"toast.rcp_saved",  "已将当前参数存为配方",    "Saved current values as a recipe"},
    {"toast.rcp_new",    "已新建配方",              "Recipe created"},
    {"toast.rcp_loaded", "已下载配方",              "Recipe applied"},
    {"toast.rcp_missing", "没有这个配方",           "Recipe not found"},
    {"toast.rcp_dup",    "配方名已存在",            "Recipe name already exists"},
    {"toast.save_fail",  "保存失败",                "Save failed"},
    {"toast.dl_fail",    "下载失败",                "Apply failed"},
    {"toast.usb_out",    "已导出到 U 盘",           "Exported to USB"},
    {"toast.usb_out_fail", "导出失败",              "Export failed"},
    {"toast.export_empty", "区间内没有记录",        "No records in range"},
    {"toast.datalog_out", "已导出 %d 行到 U 盘",    "Exported %d rows to USB"},
    {"toast.usb_in",     "已从 U 盘导入配方",       "Recipes imported"},
    {"toast.usb_in_none", "没有可导入的配方",       "Nothing to import"},
    {"toast.no_usb",     "没找到 U 盘",             "USB drive not found"},
    {"toast.saved",      "已存盘",                  "Saved"},
    {"toast.save_none",  "没有存盘组",              "No save group"},
    /* 报警列表/报警条内部词 */
    {"alarm.unacked",    "未确认",                  "Unacked"},
    {"alarm.acked",      "已确认",                  "Acked"},
    {"alarm.trigger",    "触发",                    "Trigger"},
    {"alarm.clear",      "恢复",                    "Clear"},
    {"alarm.ack",        "确认",                    "Ack"},
    {"alarm.in_alarm",   "报警中",                  "Active"},
    {"alarm.sev1",       "提示",                    "Info"},
    {"alarm.sev2",       "警告",                    "Warning"},
    {"alarm.sev3",       "严重",                    "Critical"},
    {"alarm.sev4",       "紧急",                    "Emergency"},
    {"alarm.type_hi",    "高限",                    "High"},
    {"alarm.type_hihi",  "高高限",                  "High-high"},
    {"alarm.type_lo",    "低限",                    "Low"},
    {"alarm.type_lolo",  "低低限",                  "Low-low"},
    {"alarm.type_bool",  "开关量",                  "Discrete"},
    {"alarm.type_comm",  "通讯",                    "Comm"},
    {"alarm.type_deviation", "偏差",                "Deviation"},
    {"alarm.type_rate",  "变化率",                  "Rate"},
    /* 搁置：列表状态列显示「已搁置 12 分」 */
    {"alarm.shelved",    "已搁置",                  "Shelved"},
    {"alarm.minute",     "分",                      "min"},
    /* 列表"确认"列：是 / 否 / 无需确认（规则配了 ack:false） */
    {"alarm.yes",        "是",                      "Yes"},
    {"alarm.no",         "否",                      "No"},
    {"alarm.noack",      "无需确认",                "No ack"},
    /* 表格没给列头时的通用词 */
    {"table.time",       "时间",                    "Time"},
    {"table.value",      "值",                      "Value"},
    /* 二次确认框的默认按钮（工程没写 confirm.yes / confirm.no 时用） */
    {"confirm.yes",      "确定",                    "OK"},
    {"confirm.no",       "取消",                    "Cancel"},
};

const char* i18n_rt(const char* key)
{
    char id[64];
    int en;

    if (!key || !*key) return "";
    /* 1) 工程覆盖：strings.json 的 runtime.<key> */
    snprintf(id, sizeof(id), "runtime.%s", key);
    {
        const char* v = lookup(id);

        if (v && *v) return v;
    }
    /* 2) 内置表：中文环境取 zh 列，其余取 en 列（多语言未启用时就是 zh） */
    en = (s_nlangs > 0 && s_cur >= 0 && strncmp(s_lang_code[s_cur], "zh", 2) != 0);
    for (size_t i = 0; i < sizeof(RT) / sizeof(RT[0]); i++)
        if (strcmp(RT[i].key, key) == 0) return en ? RT[i].en : RT[i].zh;
    return key;                                /* 3) 兜底：显示 key，便于发现漏项 */
}

const char* i18n_text(const char* src, char* buf, size_t n)
{
    if (!src) {
        if (buf && n) buf[0] = '\0';
        return "";
    }
    i18n_render(src, buf, n);
    return buf;
}

void i18n_render(const char* src, char* out, size_t outsz)
{
    const char* base = src;
    size_t o = 0;
    int trunc = 0;

    if (!out || outsz == 0) return;
    out[0] = '\0';
    if (!src) return;

    if (src[0] == '@') {                        /* @id：查表，缺译回退默认语言 */
        const char* v = lookup(src + 1);

        if (!v) {
            HMI_LOG("i18n: 未知文本 id %s", src + 1);
            snprintf(out, outsz, "%s", src);
            return;
        }
        base = v;
    }
    for (const char* p = base; *p; p++) {
        if (*p == '{' && p[1] == '{') {          /* {{ → 字面量 { */
            if (o + 1 >= outsz) { trunc = 1; break; }
            out[o++] = '{';
            p++;
            continue;
        }
        if (strncmp(p, "{ver}", 5) == 0) {       /* 运行时版本：编进二进制，工程不用手抄 */
            for (const char* q = APP_VERSION; *q; q++) {
                if (o + 1 >= outsz) { trunc = 1; break; }
                out[o++] = *q;
            }
            p += 4;
            continue;
        }
        if (strncmp(p, "{tag:", 5) == 0) {
            const char* close = strchr(p, '}');

            if (close) {
                char tmp[160];

                expand_tag(p + 5, (size_t)(close - (p + 5)), tmp, sizeof(tmp));
                for (const char* q = tmp; *q; q++) {
                    if (o + 1 >= outsz) { trunc = 1; break; }
                    out[o++] = *q;
                }
                p = close;
                continue;
            }
        }
        if (o + 1 >= outsz) { trunc = 1; break; }
        out[o++] = *p;
    }
    out[o] = '\0';
    /* 静默截断最坑：屏上是半句话，看不出是“配太长”还是“渲染坏了” */
    if (trunc) {
        static int warned;

        if (!warned) {
            warned = 1;
            HMI_LOG("i18n: 渲染结果超过 %u 字节被截断（源文本：%.48s…）", (unsigned)outsz, src);
        }
    }
}

/* 渲染到内部静态缓冲：容量按输入长度自适应，大文本不会撞固定上限。
 * 单线程 UI 用完即弃（lv_label_set_text 会拷贝），不要跨调用持有返回指针。 */
const char* i18n_text_static(const char* src)
{
    static char*  buf;
    static size_t cap;

    if (!src) return "";
    {
        size_t need = strlen(src) * 2 + 128;

        if (need > cap) {
            char* nb = (char*)realloc(buf, need);

            if (!nb) return "";                  /* 内存不足给空串，别留悬垂指针 */
            buf = nb;
            cap = need;
        }
    }
    i18n_render(src, buf, cap);
    return buf;
}
