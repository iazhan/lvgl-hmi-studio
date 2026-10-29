/*
 * widget_alarm.c — 报警条 / 报警列表（ADR-0008）
 *
 * 数据全部来自报警引擎（core/alarm.h）：alarm_bar 取一条轮流显示，alarm_list 取一屏列表。
 * 控件私有数据放 widget_t.cdata，拆页时 alarm_widget_dispose() 停定时器并释放。
 */
#include "widget_alarm.h"
#include "ui_page.h"          /* toast_show：点无需确认的行要说明原因 */
#include "core/audit.h"
#include "core/auth.h"
#include "core/i18n.h"
#include "alarm.h"
#include "ui_font.h"
#include "hmi_log.h"
#include "hmi_board.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define AB_VIEW_MAX 32          /* 报警条一次最多取多少条来轮换 */

typedef struct {
    /* alarm_bar */
    int  only_unacked;
    int  scroll;                /* 0 = 只显示最新，1 = 自动轮换 */
    int  scroll_ms;
    int  scroll_chars;          /* >0 时按跑马灯逐字滚动 */
    int  blink;
    int  blink_ms;
    int  order_newest_first;
    int  show_date;
    int  show_time;
    char group[ALARM_GROUP_MAX];
    char normal_text[32];
    char date_fmt[16];
    char time_fmt[16];
    lv_obj_t* label;
    lv_timer_t* refresh_timer;  /* 100ms 拉活动表 */
    lv_timer_t* rot_timer;      /* scroll_ms 轮换 / 跑马灯 */
    lv_timer_t* blink_timer;
    int  idx;                   /* 当前显示第几条 */
    int  n;                     /* 当前活动条数 */
    int  marquee;               /* 跑马灯偏移 */
    int  blink_on;
    int  unacked;
    alarm_view_t views[AB_VIEW_MAX];
    /* ── alarm_list（同一个私有块，按控件类型用不同字段） ── */
    int  list_mode;             /* 0 = active，1 = history */
    int  ack_on;                /* 0 none，1 click，2 dblclick */
    int  grid;                  /* 0 all，1 rows，2 border，3 none */
    int  ncol;
    int  colkey[10];
    int  colw[10];
    int  flt_level_lo, flt_level_hi;
    int  flt_state;             /* 0 全部，1 未确认，2 已确认 */
    uint32_t range_from, range_to;
    int  row_h, vis, first, total;
    alarm_event_t evs[200];
    lv_obj_t* rows;
    lv_obj_t* spacer;
    lv_timer_t* l_refresh;
    lv_timer_t* l_blink;
} alarm_priv_t;

/* 列 key（顺序即 columns 里允许的字符串） */
enum {
    ACK_ID = 0, ACK_DATE, ACK_TIME, ACK_NAME, ACK_TYPE, ACK_EVENT, ACK_VALUE,
    ACK_LIMIT, ACK_TEXT, ACK_ACK_TIME, ACK_END_TIME, ACK_DURATION,
    ACK_GROUP, ACK_LEVEL, ACK_STATE, ACK_ACK, ACK__COUNT
};

static const char* const ACK_KEYS[ACK__COUNT] = {
    "id", "date", "time", "name", "type", "event", "value", "limit", "text",
    "ack_time", "end_time", "duration", "group", "level", "state", "ack"
};

/* 类型名：与报警引擎的 type 枚举一一对应（0..5），渲染时按当前语言取 */
static const char* al_type_text(int type)
{
    static const char* const keys[] = {
        "alarm.type_hi", "alarm.type_hihi", "alarm.type_lo",
        "alarm.type_lolo", "alarm.type_bool", "alarm.type_comm",
        "alarm.type_deviation", "alarm.type_rate"
    };

    if (type < 0 || type > 7) return "-";
    return i18n_rt(keys[type]);
}

/* 报警"值"：活动表只有 float，类型要按规则 id 反查——布尔规则显示 0/1，
 * 通讯报警没有可比的数，显示 "-"，其余保留一位小数。 */
static void fmt_alarm_value(int rule_id, double v, char* out, size_t outsz)
{
    char name[ALARM_NAME_MAX], text[ALARM_TEXT_MAX];
    int level = 0, type = -1;

    (void)alarm_rule_info(rule_id, name, sizeof(name), text, sizeof(text), &level, &type);
    if (type == AL_T_BOOL) snprintf(out, outsz, "%d", v != 0.0 ? 1 : 0);
    else if (type == AL_T_COMM) snprintf(out, outsz, "-");
    else snprintf(out, outsz, "%.1f", v);
}

#define AP(w) ((alarm_priv_t*)(w)->cdata)

/* 按 date_format / time_format 拼时间；只认 yyyy MM DD hh mm ss，其余字符原样输出 */
static void fmt_time(char* out, size_t outsz, const char* date_fmt, const char* time_fmt,
                     uint32_t ts)
{
    struct tm tmv;
    time_t t = (time_t)ts;
    size_t o = 0;

    localtime_r(&t, &tmv);
    if (date_fmt && *date_fmt) {
        for (const char* p = date_fmt; *p && o + 8 < outsz; ) {
            if (strncmp(p, "yyyy", 4) == 0) {
                o += (size_t)snprintf(out + o, outsz - o, "%04d", tmv.tm_year + 1900);
                p += 4;
            } else if (strncmp(p, "MM", 2) == 0) {
                o += (size_t)snprintf(out + o, outsz - o, "%02d", tmv.tm_mon + 1);
                p += 2;
            } else if (strncmp(p, "DD", 2) == 0) {
                o += (size_t)snprintf(out + o, outsz - o, "%02d", tmv.tm_mday);
                p += 2;
            } else {
                if (o + 1 < outsz) out[o++] = *p;
                p++;
            }
        }
        if (o + 1 < outsz) out[o++] = ' ';
    }
    if (time_fmt && *time_fmt) {
        for (const char* p = time_fmt; *p && o + 4 < outsz; ) {
            if (strncmp(p, "hh", 2) == 0) {
                o += (size_t)snprintf(out + o, outsz - o, "%02d", tmv.tm_hour);
                p += 2;
            } else if (strncmp(p, "mm", 2) == 0) {
                o += (size_t)snprintf(out + o, outsz - o, "%02d", tmv.tm_min);
                p += 2;
            } else if (strncmp(p, "ss", 2) == 0) {
                o += (size_t)snprintf(out + o, outsz - o, "%02d", tmv.tm_sec);
                p += 2;
            } else {
                if (o + 1 < outsz) out[o++] = *p;
                p++;
            }
        }
    }
    if (o < outsz) out[o] = '\0';
}

/* 报警条要显示的一行文本：可选日期时间 + 报警描述 */
static void bar_text(alarm_priv_t* p, const alarm_view_t* v, char* out, size_t outsz)
{
    char tbuf[48];

    tbuf[0] = '\0';
    if (p->show_date || p->show_time)
        fmt_time(tbuf, sizeof(tbuf), p->show_date ? p->date_fmt : "",
                 p->show_time ? p->time_fmt : "", v->active_since);
    {
        char tb[96];        /* 与 out 同量级：再大只是让编译器报"可能截断" */

        /* 报警描述可以写 @id（多语言）：这里是显示处，渲染一次即可 */
        i18n_text(v->text, tb, sizeof(tb));
        if (tbuf[0]) snprintf(out, outsz, "%s %s", tbuf, tb);
        else snprintf(out, outsz, "%s", tb);
    }
}

static void bar_apply(widget_t* w)
{
    alarm_priv_t* p = AP(w);
    char buf[160];

    if (!p || !p->label) return;
    p->n = alarm_enum(p->group, p->only_unacked, p->views, AB_VIEW_MAX);
    p->unacked = alarm_unacked_count(p->group);
    if (p->n <= 0) {
        char rb[96];
        int rid = -1;

        /* 刚恢复的报警：条上先显示几秒恢复文案（text_clear），再退回 normal_text */
        if (alarm_recent_clear(&rid, NULL, 5) &&
            alarm_rule_clear_text(rid, rb, sizeof(rb)) == 0) {
            char tb[96];

            i18n_text(rb, tb, sizeof(tb));
            if (getenv("HMI_RT_TRACE")) HMI_LOG("bar: 显示恢复文案「%s」", tb);
            lv_label_set_text(p->label, tb);
            lv_obj_set_style_text_color(p->label, ui_color_hex("#38C172"), 0);
            return;
        }
        lv_label_set_text(p->label, p->normal_text);
        lv_obj_set_style_text_color(p->label, ui_color_hex("#38C172"), 0);
        return;
    }
    if (p->idx >= p->n) p->idx = 0;
    if (!p->order_newest_first && p->idx >= 0) p->idx = p->n - 1 - (p->idx % p->n);
    if (p->idx < 0) p->idx = 0;
    bar_text(p, &p->views[p->idx], buf, sizeof(buf));
    if (p->scroll_chars > 0) {
        int len = (int)strlen(buf);
        int chars = p->scroll_chars;

        if (len > chars) {
            char tmp[320];
            int off = p->marquee % (len + chars);

            snprintf(tmp, sizeof(tmp), "%s%s", buf, buf);
            if (off + chars < (int)sizeof(tmp)) {
                memcpy(tmp, tmp + off, (size_t)chars);
                tmp[chars] = '\0';
                lv_label_set_text(p->label, tmp);
                return;
            }
        }
    }
    lv_label_set_text(p->label, buf);
    /* 等级色：1 提示黄、2 警告橙、3 严重红 */
    lv_obj_set_style_text_color(p->label,
                                ui_color_hex(p->views[p->idx].level >= 4 ? "#C62828"
                                             : p->views[p->idx].level == 3 ? "#E05C5C"
                                             : p->views[p->idx].level == 2 ? "#F2B84B"
                                             : "#4EA1FF"), 0);
}

static void bar_refresh_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);
    alarm_priv_t* p = AP(w);

    if (!p) return;
    /* 等级/条数可能变，但当前索引保留，避免每条 100ms 都跳回第一条 */
    bar_apply(w);
}

static void bar_rot_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);
    alarm_priv_t* p = AP(w);

    if (!p) return;
    if (p->scroll_chars > 0) {
        p->marquee++;
        bar_apply(w);
        return;
    }
    if (p->n > 1) {
        p->idx = (p->idx + 1) % p->n;
        bar_apply(w);
    }
}

static void bar_blink_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);
    alarm_priv_t* p = AP(w);

    if (!p || !p->label) return;
    if (!p->blink || p->unacked <= 0) {
        lv_obj_set_style_text_opa(p->label, LV_OPA_COVER, 0);
        return;
    }
    p->blink_on = !p->blink_on;
    lv_obj_set_style_text_opa(p->label, p->blink_on ? LV_OPA_COVER : LV_OPA_20, 0);
}

int alarm_bar_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                    widget_t* out, char* err, size_t errsz)
{
    const hmi_json_t* src = hmi_json_get(props, "source");
    const char* group = hmi_json_str(hmi_json_get(src, "alarm_group"), "");
    const char* scroll = hmi_json_str(hmi_json_get(props, "scroll"), "auto");
    const char* order = hmi_json_str(hmi_json_get(props, "order"), "newest_first");
    const char* color = hmi_json_str(hmi_json_get(props, "color"), "#E6EDF3");
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 16);
    alarm_priv_t* p;
    lv_obj_t* box;

    if (ww < 40 || hh < 16) {
        snprintf(err, errsz, "alarm_bar 框太小（至少 40x16）");
        return -1;
    }
    if (strcmp(scroll, "auto") != 0 && strcmp(scroll, "none") != 0) {
        snprintf(err, errsz, "alarm_bar 的 scroll=%s 非法（auto/none）", scroll);
        return -1;
    }
    if (strcmp(order, "newest_first") != 0 && strcmp(order, "newest_last") != 0) {
        snprintf(err, errsz, "alarm_bar 的 order=%s 非法（newest_first/newest_last）", order);
        return -1;
    }
    if (!ui_color_valid(color)) {
        snprintf(err, errsz, "alarm_bar 的 color 非法");
        return -1;
    }
    if (fs < 10) fs = 10;
    if (fs > 32) fs = 32;

    p = calloc(1, sizeof(*p));
    if (!p) {
        snprintf(err, errsz, "alarm_bar 内存不足");
        return -1;
    }
    snprintf(p->group, sizeof(p->group), "%s", group ? group : "");
    {
        char tbuf[32];

        i18n_text(hmi_json_str(hmi_json_get(props, "normal_text"), "系统正常"), tbuf, sizeof(tbuf));
        snprintf(p->normal_text, sizeof(p->normal_text), "%s", tbuf);
    }
    snprintf(p->date_fmt, sizeof(p->date_fmt), "%s",
             hmi_json_str(hmi_json_get(props, "date_format"), "yyyy/MM/dd"));
    snprintf(p->time_fmt, sizeof(p->time_fmt), "%s",
             hmi_json_str(hmi_json_get(props, "time_format"), "hh:mm:ss"));
    p->only_unacked = hmi_json_bool(hmi_json_get(props, "only_unacked"), 1);
    p->scroll = (strcmp(scroll, "auto") == 0);
    p->scroll_ms = (int)hmi_json_int(hmi_json_get(props, "scroll_ms"), 3000);
    p->scroll_chars = (int)hmi_json_int(hmi_json_get(props, "scroll_chars"), 0);
    p->blink = hmi_json_bool(hmi_json_get(props, "blink"), 1);
    p->blink_ms = (int)hmi_json_int(hmi_json_get(props, "blink_ms"), 600);
    p->order_newest_first = (strcmp(order, "newest_first") == 0);
    p->show_date = hmi_json_bool(hmi_json_get(props, "show_date"), 0);
    p->show_time = hmi_json_bool(hmi_json_get(props, "show_time"), 0);
    if (p->scroll_ms < 500) p->scroll_ms = 500;
    if (p->scroll_ms > 60000) p->scroll_ms = 60000;
    if (p->blink_ms < 200) p->blink_ms = 200;
    if (p->blink_ms > 5000) p->blink_ms = 5000;
    if (p->scroll_chars < 0) p->scroll_chars = 0;
    if (p->scroll_chars > 32) p->scroll_chars = 32;

    box = lv_obj_create(ui_cur_parent());
    if (!box) {
        free(p);
        return -1;
    }
    ui_place(box, x, y, ww, hh);
    lv_obj_remove_flag(box, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_opa(box, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(box, 0, 0);

    p->label = lv_label_create(box);
    if (!p->label) {
        free(p);
        return -1;
    }
    lv_label_set_long_mode(p->label, LV_LABEL_LONG_CLIP);
    lv_obj_set_pos(p->label, 0, (int32_t)((hh - fs) / 2));
    lv_obj_set_size(p->label, (int32_t)ww, (int32_t)(fs + 4));
    lv_obj_set_style_text_font(p->label, ui_font(fs), 0);
    lv_obj_set_style_text_color(p->label, ui_color_hex(color), 0);
    lv_obj_set_style_bg_opa(p->label, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(p->label, 0, 0);
    lv_obj_set_style_pad_all(p->label, 0, 0);

    out->obj = box;
    out->type = W_ALARM_BAR;
    out->cdata = p;
    bar_apply(out);
    p->refresh_timer = lv_timer_create(bar_refresh_cb, 100, out);
    if (p->scroll)
        p->rot_timer = lv_timer_create(bar_rot_cb, (uint32_t)p->scroll_ms, out);
    if (p->blink)
        p->blink_timer = lv_timer_create(bar_blink_cb, (uint32_t)p->blink_ms, out);
    if (!p->refresh_timer) {
        snprintf(err, errsz, "alarm_bar 定时器创建失败");
        return -1;
    }
    return 0;
}

/* ── alarm_list ── */
static int ack_key_from_str(const char* s)
{
    for (int i = 0; i < ACK__COUNT; i++)
        if (strcmp(s, ACK_KEYS[i]) == 0) return i;
    return -1;
}

/* 一格文字：mode=0 用 views[idx]，mode=1 用 evs[idx] */
static void list_cell(alarm_priv_t* p, int idx, int key, char* out, size_t outsz)
{
    char name[ALARM_NAME_MAX], text[ALARM_TEXT_MAX], ctext[ALARM_TEXT_MAX], tbuf[48];
    int level = 0, type = -1;

    out[0] = '\0';
    if (p->list_mode == 0) {
        const alarm_view_t* v = &p->views[idx];

        (void)alarm_rule_info(v->rule_id, name, sizeof(name), text, sizeof(text),
                              &level, &type);
        switch (key) {
        case ACK_ID: snprintf(out, outsz, "%d", v->rule_id); break;
        case ACK_DATE:
            fmt_time(tbuf, sizeof(tbuf), p->date_fmt, "", v->active_since);
            snprintf(out, outsz, "%s", tbuf);
            break;
        case ACK_TIME:
            fmt_time(tbuf, sizeof(tbuf), "", p->time_fmt, v->active_since);
            snprintf(out, outsz, "%s", tbuf);
            break;
        case ACK_NAME: {
            char tb[96];

            snprintf(out, outsz, "%s", i18n_text(v->name, tb, sizeof(tb)));
            break;
        }
        case ACK_TYPE: snprintf(out, outsz, "%s",
                                (type >= 0 && type < 8) ? al_type_text(type) : "-"); break;
        case ACK_EVENT: snprintf(out, outsz, "%s", i18n_rt("alarm.in_alarm")); break;
        case ACK_VALUE: fmt_alarm_value(v->rule_id, v->value, out, outsz); break;
        case ACK_LIMIT: snprintf(out, outsz, "-"); break;
        case ACK_TEXT: {
            char tb[160];

            snprintf(out, outsz, "%s", i18n_text(v->text, tb, sizeof(tb)));
            break;
        }
        case ACK_ACK_TIME:
            if (v->ack_time) {
                fmt_time(tbuf, sizeof(tbuf), "", p->time_fmt, v->ack_time);
                snprintf(out, outsz, "%s", tbuf);
            } else {
                snprintf(out, outsz, "-");
            }
            break;
        case ACK_END_TIME: snprintf(out, outsz, "-"); break;
        case ACK_DURATION: {
            uint32_t now = (uint32_t)time(NULL);

            snprintf(out, outsz, "%us", (unsigned)(now > v->active_since
                                                  ? now - v->active_since : 0));
            break;
        }
        case ACK_GROUP: snprintf(out, outsz, "%s", v->group); break;
        case ACK_LEVEL: snprintf(out, outsz, "%s",
                                 v->level >= 4 ? i18n_rt("alarm.sev4")
                                 : v->level == 3 ? i18n_rt("alarm.sev3")
                                 : v->level == 2 ? i18n_rt("alarm.sev2") : i18n_rt("alarm.sev1")); break;
        case ACK_STATE:
            /* 搁置优先显示：现场最想知道的就是"这条为什么没叫" */
            if (v->shelved_until && v->shelved_until > (uint32_t)time(NULL)) {
                /* 向上取整：刚搁 30 分钟时显示 30 分，而不是被截成 29 */
                int mins = (int)((v->shelved_until - (uint32_t)time(NULL) + 59) / 60);

                snprintf(out, outsz, "%s %d%s", i18n_rt("alarm.shelved"),
                         mins < 1 ? 1 : mins, i18n_rt("alarm.minute"));
            } else {
                snprintf(out, outsz, "%s",
                         v->state == AL_S_ACKED ? i18n_rt("alarm.acked")
                                                : i18n_rt("alarm.unacked"));
            }
            break;
        case ACK_ACK: snprintf(out, outsz, "%s",
                               v->noack ? i18n_rt("alarm.noack")
                               : v->state == AL_S_ACKED ? i18n_rt("alarm.yes")
                                                        : i18n_rt("alarm.no")); break;
        default: break;
        }
        return;
    }
    {
        const alarm_event_t* e = &p->evs[idx];

        (void)alarm_rule_info(e->rule_id, name, sizeof(name), text, sizeof(text),
                              &level, &type);
        /* 恢复事件优先用规则配的 text_clear；没配就沿用 text（老工程行为不变） */
        if (e->event == AL_E_CLEAR && alarm_rule_clear_text(e->rule_id, ctext, sizeof(ctext)) == 0)
            snprintf(text, sizeof(text), "%s", ctext);
        switch (key) {
        case ACK_ID: snprintf(out, outsz, "%d", e->rule_id); break;
        case ACK_DATE:
            fmt_time(tbuf, sizeof(tbuf), p->date_fmt, "", e->ts);
            snprintf(out, outsz, "%s", tbuf);
            break;
        case ACK_TIME:
            fmt_time(tbuf, sizeof(tbuf), "", p->time_fmt, e->ts);
            snprintf(out, outsz, "%s", tbuf);
            break;
        case ACK_NAME: snprintf(out, outsz, "%s", name); break;
        case ACK_TYPE: snprintf(out, outsz, "%s",
                                (type >= 0 && type < 8) ? al_type_text(type) : "-"); break;
        case ACK_EVENT:
            snprintf(out, outsz, "%s", e->event == AL_E_TRIGGER ? i18n_rt("alarm.trigger")
                                   : e->event == AL_E_CLEAR ? i18n_rt("alarm.clear")
                                                            : i18n_rt("alarm.ack"));
            break;
        case ACK_VALUE:
            /* 事件里只存了一个 float（12 字节落盘格式），类型从规则反查：
             * 布尔规则是 0/1，通讯报警没有"值"，其它保留一位小数 */
            if (type == AL_T_BOOL) snprintf(out, outsz, "%d", (double)e->value != 0.0 ? 1 : 0);
            else if (type == AL_T_COMM) snprintf(out, outsz, "-");
            else snprintf(out, outsz, "%.1f", (double)e->value);
            break;
        case ACK_LIMIT: snprintf(out, outsz, "-"); break;
        case ACK_TEXT: {
            /* 历史事件的描述来自 alarms.json 的 text，可能写的是 @id（多语言） */
            char tb[ALARM_TEXT_MAX];

            snprintf(out, outsz, "%s", i18n_text(text, tb, sizeof(tb)));
            break;
        }
        case ACK_ACK_TIME:
        case ACK_END_TIME:
        case ACK_DURATION: snprintf(out, outsz, "-"); break;
        case ACK_GROUP: snprintf(out, outsz, "%s", p->group); break;
        case ACK_LEVEL: snprintf(out, outsz, "%s",
                                 e->level >= 3 ? i18n_rt("alarm.sev3")
                                 : e->level == 2 ? i18n_rt("alarm.sev2") : i18n_rt("alarm.sev1")); break;
        case ACK_STATE: snprintf(out, outsz, "-"); break;
        case ACK_ACK: snprintf(out, outsz, "%s",
                               e->event == AL_E_ACK ? "是" : "-"); break;
        default: break;
        }
    }
}

static lv_obj_t* list_box(lv_obj_t* parent, int x, int y, int w, int h, const char* bg)
{
    lv_obj_t* o = lv_obj_create(parent);

    if (!o) return NULL;
    lv_obj_set_pos(o, (int32_t)x, (int32_t)y);
    lv_obj_set_size(o, (int32_t)w, (int32_t)h);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(o, ui_color_hex(bg), 0);
    lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
    lv_obj_set_style_border_width(o, 0, 0);
    lv_obj_set_style_radius(o, 0, 0);
    lv_obj_set_style_pad_all(o, 0, 0);
    return o;
}

static lv_obj_t* list_cell_label(lv_obj_t* parent, int x, int y, int w, int h, int fs,
                                 const char* color)
{
    lv_obj_t* lb = lv_label_create(parent);

    if (!lb) return NULL;
    lv_label_set_text(lb, "");
    lv_label_set_long_mode(lb, LV_LABEL_LONG_CLIP);
    lv_obj_set_pos(lb, (int32_t)x, (int32_t)y);
    lv_obj_set_size(lb, (int32_t)w, (int32_t)h);
    lv_obj_set_style_text_font(lb, ui_font(fs), 0);
    lv_obj_set_style_text_color(lb, ui_color_hex(color), 0);
    lv_obj_set_style_bg_opa(lb, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(lb, 0, 0);
    lv_obj_set_style_pad_all(lb, 0, 0);
    return lb;
}

/* 拉数据 + 填当前窗口（活动模式取活动表，历史模式取事件文件） */
static void list_load(widget_t* w)
{
    alarm_priv_t* p = AP(w);
    int n = 0;

    if (!p || !p->rows) return;
    if (p->list_mode == 0) {
        alarm_view_t tmp[AB_VIEW_MAX];
        int m = alarm_enum(p->group, 0, tmp, AB_VIEW_MAX);

        for (int i = 0; i < m && n < AB_VIEW_MAX; i++) {
            if (tmp[i].level < p->flt_level_lo || tmp[i].level > p->flt_level_hi) continue;
            if (p->flt_state == 1 && tmp[i].state != AL_S_ACTIVE) continue;
            /* "已确认"筛选不列 ack:false 的规则：它们压根没走确认流程 */
            if (p->flt_state == 2 && (tmp[i].state != AL_S_ACKED || tmp[i].noack)) continue;
            p->views[n++] = tmp[i];
        }
    } else {
        int m = alarm_history(p->group, p->range_from, p->range_to, p->evs, 200);

        if (m > 0) n = m;
    }
    p->total = n;
    {
        int max_first = p->total - p->vis;

        if (max_first < 0) max_first = 0;
        if (p->first > max_first) p->first = max_first;
        if (p->first < 0) p->first = 0;
    }
    if (p->spacer)
        lv_obj_set_height(p->spacer, (int32_t)(p->total > 0 ? p->total * p->row_h
                                                           : p->row_h));
    for (int i = 0; i < p->vis; i++) {
        lv_obj_t* row = lv_obj_get_child(p->rows, (int32_t)i);
        int idx = p->first + i;
        int shown = (idx >= 0 && idx < p->total);

        if (!row) continue;
        if (!shown) {
            lv_obj_add_flag(row, LV_OBJ_FLAG_HIDDEN);
            continue;
        }
        lv_obj_remove_flag(row, LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_y(row, (int32_t)(idx * p->row_h));
        for (int c = 0; c < p->ncol; c++) {
            lv_obj_t* lb = lv_obj_get_child(row, (int32_t)c);
            char buf[96];

            if (!lb) continue;
            list_cell(p, idx, p->colkey[c], buf, sizeof(buf));
            lv_label_set_text(lb, buf);
        }
    }
}

static void list_refresh_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);

    if (w) list_load(w);
}

static void list_scroll_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    alarm_priv_t* p = AP(w);
    int sy, first, max_first;

    if (!p || !p->rows) return;
    sy = (int)lv_obj_get_scroll_y(p->rows);
    first = (sy >= 0) ? sy / p->row_h : 0;
    max_first = p->total - p->vis;
    if (max_first < 0) max_first = 0;
    if (first > max_first) first = max_first;
    if (first < 0) first = 0;
    if (first == p->first) return;
    p->first = first;
    list_load(w);
}

/* 点行确认：行号 → 数据下标 → 规则 id */
static void list_click_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    alarm_priv_t* p = AP(w);
    lv_obj_t* row = lv_event_get_current_target_obj(e);
    int row_idx = -1, idx;

    if (!p || !p->rows || p->list_mode != 0 || p->ack_on == 0) return;
    for (int i = 0; i < p->vis; i++)
        if (lv_obj_get_child(p->rows, (int32_t)i) == row) { row_idx = i; break; }
    if (row_idx < 0) return;
    idx = p->first + row_idx;
    if (idx < 0 || idx >= p->total) return;
    if (p->views[idx].noack) {                 /* ack:false：点了也不确认，说清楚为什么 */
        toast_show(i18n_rt("alarm.noack"));
        return;
    }
    /* 点行确认是"确认报警"的第二个入口（第一个是 ack_alarm 动作），
     * 命名位同样要判（ADR-0026） */
    if (!auth_perm(PERM_ACK_ALARM)) {
        HMI_LOG("auth: 拒绝确认报警 %d（用户 %s，缺少 ack_alarm 权限）",
                p->views[idx].rule_id, auth_user_name() ? auth_user_name() : "未登录");
        audit_denied("alarm-ack", "ack_alarm");
        toast_show(i18n_rt("toast.no_perm"));
        return;
    }
    if (alarm_ack(p->views[idx].rule_id) == 0) list_load(w);
    else HMI_LOG("alarm: 确认规则 %d 失败（可能已恢复）", p->views[idx].rule_id);
}

static void list_blink_cb(lv_timer_t* t)
{
    widget_t* w = (widget_t*)lv_timer_get_user_data(t);
    alarm_priv_t* p = AP(w);

    if (!p || !p->rows) return;
    p->blink_on = !p->blink_on;
    /* 只闪未确认的行：逐行按状态给透明度 */
    for (int i = 0; i < p->vis; i++) {
        lv_obj_t* row = lv_obj_get_child(p->rows, (int32_t)i);
        int idx = p->first + i;

        if (!row) continue;
        if (idx < p->total && p->list_mode == 0 && p->views[idx].state == AL_S_ACTIVE)
            lv_obj_set_style_bg_opa(row, p->blink_on ? LV_OPA_COVER : LV_OPA_30, 0);
        else
            lv_obj_set_style_bg_opa(row, LV_OPA_COVER, 0);
    }
}

int alarm_list_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                     widget_t* out, char* err, size_t errsz)
{
    const hmi_json_t* src = hmi_json_get(props, "source");
    const hmi_json_t* cols = hmi_json_get(props, "columns");
    const hmi_json_t* filter = hmi_json_get(props, "filter");
    const char* group = hmi_json_str(hmi_json_get(src, "alarm_group"), "");
    const char* mode = hmi_json_str(hmi_json_get(props, "mode"), "active");
    const char* order = hmi_json_str(hmi_json_get(props, "order"), "newest_first");
    const char* ack_on = hmi_json_str(hmi_json_get(props, "ack_on"), "click");
    const char* grid = hmi_json_str(hmi_json_get(props, "grid"), "all");
    const char* gb = hmi_json_str(hmi_json_get(props, "group_by"), "none");
    int row_h = (int)hmi_json_int(hmi_json_get(props, "row_height"), 26);
    int fs = (int)hmi_json_int(hmi_json_get(props, "font_size"), 14);
    int max_rows = (int)hmi_json_int(hmi_json_get(props, "max_rows"), 500);
    int scrollbar = hmi_json_bool(hmi_json_get(props, "scrollbar"), 1);
    int refresh_ms = (int)hmi_json_int(hmi_json_get(props, "refresh_ms"), 500);
    alarm_priv_t* p;
    lv_obj_t* box, * hdr, * view;
    int ncol, body_h, cw_sum = 0;
    int cell_h, cell_y;

    if (strcmp(gb, "none") != 0) {
        snprintf(err, errsz, "alarm_list 的 group_by=%s 暂未实现（v1 只支持 none）", gb);
        return -1;
    }
    if (strcmp(mode, "active") != 0 && strcmp(mode, "history") != 0) {
        snprintf(err, errsz, "alarm_list 的 mode=%s 非法（active/history）", mode);
        return -1;
    }
    if (strcmp(order, "newest_first") != 0 && strcmp(order, "newest_last") != 0) {
        snprintf(err, errsz, "alarm_list 的 order=%s 非法", order);
        return -1;
    }
    if (hmi_json_type(cols) != HMI_JSON_ARR) {
        snprintf(err, errsz, "alarm_list 缺 columns 数组");
        return -1;
    }
    ncol = (int)hmi_json_count(cols);
    if (ncol < 1 || ncol > 10) {
        snprintf(err, errsz, "alarm_list 的 columns 项数 %d 超出 1..10", ncol);
        return -1;
    }
    if (row_h < 16) row_h = 16;
    if (row_h > 60) row_h = 60;
    if (fs < 10) fs = 10;
    if (fs > 28) fs = 28;
    if (max_rows < 10) max_rows = 10;
    /* 单元格高取字体行高：中文的字面比 em 高，按 fs+2 定高会切掉字底 */
    cell_h = (int)lv_font_get_line_height(ui_font(fs)) + 2;
    cell_y = (row_h - cell_h) / 2;
    if (cell_y < 0) cell_y = 0;

    p = calloc(1, sizeof(*p));
    if (!p) {
        snprintf(err, errsz, "alarm_list 内存不足");
        return -1;
    }
    p->list_mode = (strcmp(mode, "history") == 0);
    p->ack_on = (strcmp(ack_on, "click") == 0) ? 1 : (strcmp(ack_on, "dblclick") == 0) ? 2 : 0;
    p->grid = (strcmp(grid, "rows") == 0) ? 1 : (strcmp(grid, "border") == 0) ? 2
             : (strcmp(grid, "none") == 0) ? 3 : 0;
    p->row_h = row_h;
    p->ncol = ncol;
    p->flt_level_lo = 1;
    p->flt_level_hi = 4;              /* 4 级（1 提示 … 4 紧急） */
    p->flt_state = 0;
    p->range_from = 0;
    p->range_to = 0;
    p->order_newest_first = (strcmp(order, "newest_first") == 0);
    p->blink = hmi_json_bool(hmi_json_get(props, "blink"), 1);
    p->blink_ms = (int)hmi_json_int(hmi_json_get(props, "blink_ms"), 600);
    snprintf(p->group, sizeof(p->group), "%s", group ? group : "");
    snprintf(p->date_fmt, sizeof(p->date_fmt), "%s",
             hmi_json_str(hmi_json_get(props, "date_format"), "yyyy/MM/dd"));
    snprintf(p->time_fmt, sizeof(p->time_fmt), "%s",
             hmi_json_str(hmi_json_get(props, "time_format"), "hh:mm:ss"));

    /* 过滤：level 支持 "2" 与 "2~3"；state 支持 unacked/acked；range 支持 1d/7d/30d/all */
    if (hmi_json_type(filter) == HMI_JSON_OBJ) {
        const char* lv = hmi_json_str(hmi_json_get(filter, "level"), NULL);
        const char* st = hmi_json_str(hmi_json_get(filter, "state"), NULL);
        const char* rg = hmi_json_str(hmi_json_get(filter, "range"), NULL);

        if (lv && *lv) {
            int a = 0, b = 0;

            if (sscanf(lv, "%d~%d", &a, &b) == 2) {
                p->flt_level_lo = a;
                p->flt_level_hi = b;
            } else if (sscanf(lv, "%d", &a) == 1) {
                p->flt_level_lo = p->flt_level_hi = a;
            }
            if (p->flt_level_lo < 1) p->flt_level_lo = 1;
            if (p->flt_level_hi > 4) p->flt_level_hi = 4;
        }
        if (st && *st) {
            if (strcmp(st, "unacked") == 0) p->flt_state = 1;
            else if (strcmp(st, "acked") == 0) p->flt_state = 2;
            else if (strcmp(st, "all") == 0) p->flt_state = 0;
            else {
                snprintf(err, errsz, "alarm_list 的 filter.state=%s 非法", st);
                free(p);
                return -1;
            }
        }
        if (rg && *rg) {
            uint32_t now = (uint32_t)time(NULL);
            long days = (strcmp(rg, "1d") == 0) ? 1 : (strcmp(rg, "7d") == 0) ? 7
                      : (strcmp(rg, "30d") == 0) ? 30 : 0;

            if (days > 0) p->range_from = now - (uint32_t)days * 86400u;
        }
    }

    for (int c = 0; c < ncol; c++) {
        const hmi_json_t* col = hmi_json_at(cols, (size_t)c);
        const char* key = hmi_json_str(hmi_json_get(col, "key"), NULL);
        long w = hmi_json_int(hmi_json_get(col, "w"), 0);

        p->colkey[c] = key ? ack_key_from_str(key) : -1;
        if (p->colkey[c] < 0) {
            snprintf(err, errsz, "alarm_list 第 %d 列的 key=%s 非法", c, key ? key : "(缺失)");
            free(p);
            return -1;
        }
        p->colw[c] = (w > 0) ? (int)w : 0;
        cw_sum += p->colw[c];
    }
    for (int c = 0; c < ncol; c++)
        if (p->colw[c] == 0)
            p->colw[c] = ((int)ww - cw_sum > 0) ? ((int)ww - cw_sum) / ncol : (int)ww / ncol;

    box = list_box(ui_cur_parent(), 0, 0, (int)ww, (int)hh, "#141C26");
    if (!box) {
        free(p);
        return -1;
    }
    ui_place(box, x, y, ww, hh);
    lv_obj_set_style_border_color(box, ui_color_hex("#3A4A5A"), 0);
    lv_obj_set_style_border_width(box, (p->grid == 3 || p->grid == 1) ? 0 : 1, 0);
    lv_obj_set_style_radius(box, 4, 0);

    hdr = list_box(box, 0, 0, (int)ww - 2, row_h, "#1B2530");
    if (hdr) {
        for (int c = 0, cx = 0; c < ncol; cx += p->colw[c], c++) {
            const hmi_json_t* col = hmi_json_at(cols, (size_t)c);
            const char* label = hmi_json_str(hmi_json_get(col, "label"), NULL);
            lv_obj_t* lb = list_cell_label(hdr, cx + 6, cell_y, p->colw[c] - 8,
                                           cell_h, fs, "#9AA7B5");

            if (lb) {
                char tbuf[96];

                lv_label_set_text(lb, label ? i18n_text(label, tbuf, sizeof(tbuf))
                                            : ACK_KEYS[p->colkey[c]]);
            }
        }
    }

    body_h = (int)hh - row_h - 2;
    if (body_h < row_h) {
        snprintf(err, errsz, "alarm_list 太矮：表头之后没有数据行空间");
        free(p);
        return -1;
    }
    p->vis = body_h / row_h + 2;
    if (p->vis < 2) p->vis = 2;
    if (p->vis > 16) p->vis = 16;

    view = list_box(box, 0, row_h, (int)ww - 2, body_h, "#141C26");
    if (!view) {
        free(p);
        snprintf(err, errsz, "alarm_list 建数据区失败");
        return -1;
    }
    ui_scroll_area(view, scrollbar);
    for (int i = 0; i < p->vis; i++) {
        lv_obj_t* row = list_box(view, 0, i * row_h, (int)ww - 10, row_h,
                                 (i & 1) ? "#17212B" : "#141C26");

        if (!row) {
            free(p);
            snprintf(err, errsz, "alarm_list 建第 %d 行失败", i);
            return -1;
        }
        if (p->grid == 0 || p->grid == 1) {
            lv_obj_set_style_border_color(row, ui_color_hex("#243140"), 0);
            lv_obj_set_style_border_width(row, 1, 0);
        }
        for (int c = 0, cx = 0; c < ncol; cx += p->colw[c], c++)
            (void)list_cell_label(row, cx + 6, cell_y, p->colw[c] - 8, cell_h, fs,
                                  "#E6EDF3");
        if (p->ack_on == 1)
            lv_obj_add_event_cb(row, list_click_cb, LV_EVENT_CLICKED, out);
    }
    p->spacer = lv_obj_create(view);
    if (p->spacer) {
        lv_obj_set_pos(p->spacer, 0, 0);
        lv_obj_set_size(p->spacer, 1, (int32_t)row_h);
        lv_obj_remove_flag(p->spacer, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_style_bg_opa(p->spacer, LV_OPA_TRANSP, 0);
        lv_obj_set_style_border_width(p->spacer, 0, 0);
        lv_obj_add_flag(p->spacer, LV_OBJ_FLAG_IGNORE_LAYOUT);
    }

    out->obj = box;
    out->type = W_ALARM_LIST;
    out->cdata = p;
    p->rows = view;
    lv_obj_add_event_cb(view, list_scroll_cb, LV_EVENT_SCROLL, out);
    list_load(out);
    lv_obj_scroll_to_y(view, 0, LV_ANIM_OFF);
    p->l_refresh = lv_timer_create(list_refresh_cb,
                                   (uint32_t)(refresh_ms > 0 ? refresh_ms : 500), out);
    if (p->blink && p->list_mode == 0)
        p->l_blink = lv_timer_create(list_blink_cb,
                                     (uint32_t)(p->blink_ms > 0 ? p->blink_ms : 600), out);
    if (!p->l_refresh) {
        snprintf(err, errsz, "alarm_list 定时器创建失败");
        return -1;
    }
    (void)max_rows;
    return 0;
}

void alarm_widget_dispose(widget_t* w)
{
    alarm_priv_t* p;

    if (!w || !w->cdata) return;
    if (w->type != W_ALARM_BAR && w->type != W_ALARM_LIST) return;
    p = AP(w);
    if (p->refresh_timer) lv_timer_delete(p->refresh_timer);
    if (p->rot_timer) lv_timer_delete(p->rot_timer);
    if (p->blink_timer) lv_timer_delete(p->blink_timer);
    if (p->l_refresh) lv_timer_delete(p->l_refresh);
    if (p->l_blink) lv_timer_delete(p->l_blink);
    free(p);
    w->cdata = NULL;
}
