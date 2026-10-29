/*
 * alarm.c — 报警引擎（ADR-0008）
 *
 * 规则来自 <工程根>/alarms.json；主循环每 100ms 调 alarm_tick()。
 * 越限要持续 delay_ms 才触发；恢复要跨过死区（deadband）才清，避免阈值附近抖动。
 * 活动状态只在内存里，重启按当前值重新判定；事件（触发/恢复/确认）落到 alarm_store。
 */
#include "alarm.h"
#include "alarm_store.h"
#include "audit.h"
#include "i18n.h"
#include "tagdb.h"
#include "hmi_json.h"
#include "comm/modbus.h"
#include "hmi_log.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct {
    int      id;
    int      tag;
    int      type;
    int      level;
    int      ack_needed;
    double   limit;
    double   deadband;
    int      delay_ms;
    /* deviation：与参考变量的偏差；rate：窗口内变化率 */
    int      ref_tag;                /* deviation 的参考变量 */
    double   band;                   /* deviation 的允许带宽 */
    int      window_ms;              /* rate 的观察窗口 */
    double   rate_v0;                /* 窗口起点的值 */
    long long rate_t0;               /* 窗口起点（单调时钟 ms） */
    double   rate_slope;             /* 最近一次算出的斜率（单位/秒） */
    /* 搁置 */
    uint32_t shelved_until;
    char     shelved_by[24];
    /* ISA-18.2：只存不用 */
    int      priority;
    int      allowed_ms;
    char     cause[48];
    char     consequence[48];
    char     group[ALARM_GROUP_MAX];
    char     device[40];             /* type=comm：判哪台设备的链路 */
    char     name[ALARM_NAME_MAX];
    char     text[ALARM_TEXT_MAX];       /* 报警中的描述；text_is_id 时这段是 strings.json 的 id */
    char     text_clear[ALARM_TEXT_MAX]; /* 恢复时的描述（可选，空 = 仍用 text）；clear_is_id 同理 */
    uint8_t  text_is_id;
    uint8_t  clear_is_id;
    /* 触发沿动作（37 §6.9）：v1 只支持 write_tag / toggle_tag */
    int      act_do;                 /* 0 无，1 write_tag，2 toggle_tag */
    int      act_tag;
    double   act_value;
    /* 运行态 */
    int      state;
    long long pending_since;         /* 越限起始时刻（单调时钟 ms），-1 = 未越限 */
    uint32_t active_since;
    uint32_t ack_time;
    double   value;
} alarm_rule_t;

static alarm_rule_t s_rules[ALARM_MAX_RULES];
static int          s_nrules;
static const project_t* s_proj;      /* 通讯报警要查设备链路状态 */

/* 最近一条"恢复"事件：报警条在没有活动报警时显示几秒恢复文案（37 §6.9 text_clear） */
static int      s_clear_rule = -1;
static uint32_t s_clear_ts;

/* 规则文案：text_id 形式时按当前语言现取。切语言不用重建规则表，历史事件里存的是当时的字符串 */
static const char* rule_text(const alarm_rule_t* r)
{
    if (r->text_is_id) {
        const char* v = i18n_lookup(r->text);

        if (v && *v) return v;
    }
    return r->text;
}

static const char* rule_clear_text(const alarm_rule_t* r)
{
    if (!r->text_clear[0]) return NULL;
    if (r->clear_is_id) {
        const char* v = i18n_lookup(r->text_clear);

        if (v && *v) return v;
    }
    return r->text_clear;
}

static long long mono_ms(void)
{
    struct timespec ts;

    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0) return 0;
    return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

void alarm_free(void)
{
    s_nrules = 0;
    memset(s_rules, 0, sizeof(s_rules));
}

int alarm_rule_count(void)
{
    return s_nrules;
}

static int type_from_str(const char* s)
{
    if (!s) return -1;
    if (strcmp(s, "hi") == 0) return AL_T_HI;
    if (strcmp(s, "hi_hi") == 0) return AL_T_HI_HI;
    if (strcmp(s, "lo") == 0) return AL_T_LO;
    if (strcmp(s, "lo_lo") == 0) return AL_T_LO_LO;
    if (strcmp(s, "bool") == 0) return AL_T_BOOL;
    if (strcmp(s, "comm") == 0) return AL_T_COMM;
    if (strcmp(s, "deviation") == 0) return AL_T_DEVIATION;
    if (strcmp(s, "rate_of_change") == 0) return AL_T_RATE;
    return -1;
}

int alarm_load(const project_t* p, const char* data_dir, char* err, size_t errsz)
{
    s_proj = p;
    char path[300];
    char* text;
    size_t len = 0;
    hmi_json_t* root;
    const hmi_json_t* arr;
    size_t n;

    alarm_free();
    alarm_store_set_dir(data_dir);
    if (!p) return 0;

    snprintf(path, sizeof(path), "%s/alarms.json", p->root);
    text = loader_read_text(path, 256 * 1024, &len);
    if (!text) return 0;                       /* 没有报警定义是正常情况 */

    root = hmi_json_parse(text, len, err, errsz);
    if (!root) {
        free(text);
        return -1;
    }
    arr = hmi_json_get(root, "alarms");
    if (hmi_json_type(arr) != HMI_JSON_ARR) {
        snprintf(err, errsz, "alarms.json 缺 alarms 数组");
        hmi_json_free(root);
        free(text);
        return -1;
    }
    n = hmi_json_count(arr);
    if (n > ALARM_MAX_RULES) {
        snprintf(err, errsz, "alarms.json 规则数 %zu 超过上限 %d", n, ALARM_MAX_RULES);
        hmi_json_free(root);
        free(text);
        return -1;
    }

    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* e = hmi_json_at(arr, i);
        alarm_rule_t* r = &s_rules[s_nrules];
        const char* s;
        long tag;
        long id;

        memset(r, 0, sizeof(*r));
        r->pending_since = -1;

        id = hmi_json_int(hmi_json_get(e, "id"), (long)i);
        if (id < 0 || id > 65535) {
            snprintf(err, errsz, "alarms.json 第 %zu 条 id=%ld 越界（0..65535）", i, id);
            goto fail;
        }
        for (int k = 0; k < s_nrules; k++)
            if (s_rules[k].id == (int)id) {
                snprintf(err, errsz, "alarms.json 第 %zu 条 id=%ld 与前面重复", i, id);
                goto fail;
            }
        r->id = (int)id;

        s = hmi_json_str(hmi_json_get(e, "type"), NULL);
        r->type = type_from_str(s);
        if (r->type < 0) {
            snprintf(err, errsz, "alarms.json 第 %zu 条 type=%s 非法"
                                 "（hi/hi_hi/lo/lo_lo/bool/comm）", i, s ? s : "(缺失)");
            goto fail;
        }

        tag = hmi_json_int(hmi_json_get(e, "tag"), -1);
        if (r->type == AL_T_COMM) {
            const char* dev = hmi_json_str(hmi_json_get(e, "device"), NULL);

            if (!dev || !*dev) {
                snprintf(err, errsz, "alarms.json 第 %zu 条 type=comm 需要 device", i);
                goto fail;
            }
            if (project_device_protocol(p, dev) == NULL) {
                snprintf(err, errsz, "alarms.json 第 %zu 条 type=comm 的 device=%s 不存在",
                         i, dev);
                goto fail;
            }
            snprintf(r->device, sizeof(r->device), "%s", dev);
            r->tag = -1;                        /* 按设备链路判，不看变量 */
        }
        if (r->type != AL_T_COMM) {              /* 通讯报警不看变量，只按设备链路判 */
            if (tag < 0 || tag >= tagdb_count()) {
                snprintf(err, errsz, "alarms.json 第 %zu 条 tag=%ld 不存在", i, tag);
                goto fail;
            }
            r->tag = (int)tag;
        }
        {
            const tag_def_t* td = project_tag(p, (int)tag);

            snprintf(r->name, sizeof(r->name), "%s", td ? td->name : "");
        }

        r->limit = hmi_json_num(hmi_json_get(e, "limit"), 0);
        r->deadband = hmi_json_num(hmi_json_get(e, "deadband"), 0);
        if (r->deadband < 0) r->deadband = 0;
        r->delay_ms = (int)hmi_json_int(hmi_json_get(e, "delay_ms"), 0);
        if (r->delay_ms < 0) r->delay_ms = 0;
        if (r->delay_ms > 60000) r->delay_ms = 60000;

        /* 级别 1..4（1=提示 2=警告 3=严重 4=紧急），升序；默认 2（警告） */
        r->level = (int)hmi_json_int(hmi_json_get(e, "level"), 2);
        if (r->level < 1 || r->level > 4) {
            snprintf(err, errsz, "alarms.json 第 %zu 条 level=%d 越界（1..4）", i, r->level);
            goto fail;
        }

        /* deviation：需要一个参考变量与允许带宽 */
        if (r->type == AL_T_DEVIATION) {
            long rt = hmi_json_int(hmi_json_get(e, "ref_tag"), -1);

            if (rt < 0 || rt >= tagdb_count()) {
                snprintf(err, errsz,
                         "alarms.json 第 %zu 条 type=deviation 需要 ref_tag（参考变量）", i);
                goto fail;
            }
            r->ref_tag = (int)rt;
            r->band = hmi_json_num(hmi_json_get(e, "band"), 0);
            if (r->band <= 0) {
                snprintf(err, errsz,
                         "alarms.json 第 %zu 条 type=deviation 的 band 必须 > 0", i);
                goto fail;
            }
        }
        /* rate_of_change：limit 是单位/秒的斜率阈值（正=上升过快，负=下降过快） */
        if (r->type == AL_T_RATE) {
            r->window_ms = (int)hmi_json_int(hmi_json_get(e, "window_ms"), 2000);
            if (r->window_ms < 200) r->window_ms = 200;
            if (r->window_ms > 600000) r->window_ms = 600000;
            if (r->limit == 0) {
                snprintf(err, errsz,
                         "alarms.json 第 %zu 条 type=rate_of_change 的 limit 不能为 0", i);
                goto fail;
            }
        }

        /* ISA-18.2：存而不用 */
        r->priority = (int)hmi_json_int(hmi_json_get(e, "priority"), 0);
        r->allowed_ms = (int)hmi_json_int(hmi_json_get(e, "allowed_response_ms"), 0);
        {
            const char* cs = hmi_json_str(hmi_json_get(e, "cause"), "");
            const char* qs = hmi_json_str(hmi_json_get(e, "consequence"), "");

            if (cs) snprintf(r->cause, sizeof(r->cause), "%s", cs);
            if (qs) snprintf(r->consequence, sizeof(r->consequence), "%s", qs);
        }
        r->ack_needed = hmi_json_bool(hmi_json_get(e, "ack"), 1);

        s = hmi_json_str(hmi_json_get(e, "group"), "默认");
        snprintf(r->group, sizeof(r->group), "%s", (s && *s) ? s : "默认");

        /* 文本：text 直写优先；写 text_id 就存 id、用时按当前语言现取（切语言不必重建规则表） */
        s = hmi_json_str(hmi_json_get(e, "text"), NULL);
        if (s && *s) {
            snprintf(r->text, sizeof(r->text), "%s", s);
        } else if ((s = hmi_json_str(hmi_json_get(e, "text_id"), NULL)) && *s) {
            if (!i18n_lookup(s)) {
                snprintf(err, errsz, "alarms.json 第 %zu 条的 text_id=%s 在 strings.json 里没有",
                         i, s);
                goto fail;
            }
            snprintf(r->text, sizeof(r->text), "%s", s);
            r->text_is_id = 1;
        } else {
            char tmp[ALARM_TEXT_MAX];

            snprintf(tmp, sizeof(tmp), "%s 报警", r->name);
            snprintf(r->text, sizeof(r->text), "%s", tmp);
        }

        /* 恢复文案（可选）：历史事件里"恢复"那条用它；不写就沿用 text */
        s = hmi_json_str(hmi_json_get(e, "text_clear"), NULL);
        if (s && *s) {
            snprintf(r->text_clear, sizeof(r->text_clear), "%s", s);
        } else if ((s = hmi_json_str(hmi_json_get(e, "text_clear_id"), NULL)) && *s) {
            if (!i18n_lookup(s)) {
                snprintf(err, errsz, "alarms.json 第 %zu 条的 text_clear_id=%s 在 strings.json 里没有",
                         i, s);
                goto fail;
            }
            snprintf(r->text_clear, sizeof(r->text_clear), "%s", s);
            r->clear_is_id = 1;
        }

        /* 触发沿动作：v1 只支持 write_tag / toggle_tag，其余加载期报错 */
        {
            const hmi_json_t* act = hmi_json_get(e, "action");

            if (hmi_json_type(act) == HMI_JSON_OBJ) {
                const char* doit = hmi_json_str(hmi_json_get(act, "do"), NULL);

                if (doit && strcmp(doit, "write_tag") == 0) {
                    long at = hmi_json_int(hmi_json_get(act, "tag"), -1);

                    if (at < 0 || at >= tagdb_count()) {
                        snprintf(err, errsz, "alarms.json 第 %zu 条 action.tag=%ld 不存在",
                                 i, at);
                        goto fail;
                    }
                    r->act_do = 1;
                    r->act_tag = (int)at;
                    r->act_value = hmi_json_num(hmi_json_get(act, "value"), 0);
                } else if (doit && strcmp(doit, "toggle_tag") == 0) {
                    long at = hmi_json_int(hmi_json_get(act, "tag"), -1);

                    if (at < 0 || at >= tagdb_count()) {
                        snprintf(err, errsz, "alarms.json 第 %zu 条 action.tag=%ld 不存在",
                                 i, at);
                        goto fail;
                    }
                    r->act_do = 2;
                    r->act_tag = (int)at;
                } else {
                    snprintf(err, errsz, "alarms.json 第 %zu 条 action.do=%s 暂不支持"
                                         "（v1 只有 write_tag / toggle_tag）",
                             i, doit ? doit : "(缺失)");
                    goto fail;
                }
            }
        }
        s_nrules++;
    }

    hmi_json_free(root);
    free(text);
    HMI_LOG("alarm: 载入 %d 条规则", s_nrules);
    return s_nrules;

fail:
    hmi_json_free(root);
    free(text);
    alarm_free();
    return -1;
}

/* 判越限：恢复方向要跨过死区，否则阈值附近会反复触发 */
static int rule_violated(const alarm_rule_t* r, double v)
{
    double lim = r->limit;
    double db = r->deadband;

    switch (r->type) {
    case AL_T_BOOL:
        return v != 0.0;
    case AL_T_HI:
    case AL_T_HI_HI:
        return (r->state == AL_S_NORMAL) ? (v > lim) : (v > lim - db);
    case AL_T_LO:
    case AL_T_LO_LO:
        return (r->state == AL_S_NORMAL) ? (v < lim) : (v < lim + db);
    case AL_T_DEVIATION: {
        double d = v - tagdb_num(r->ref_tag);

        if (d < 0) d = -d;
        /* 带宽同样带死区：回到 band 以内、且留出 deadband 才恢复 */
        return (r->state == AL_S_NORMAL) ? (d > r->band) : (d > r->band - db);
    }
    default:
        return 0;
    }
}

void alarm_tick(void)
{
    long long now = mono_ms();
    uint32_t wall = (uint32_t)time(NULL);

    for (int i = 0; i < s_nrules; i++) {
        alarm_rule_t* r = &s_rules[i];
        alarm_event_t ev;
        int bad;

        /* 搁置（46 §8.1）：到期自动恢复；搁置期间状态冻结，既不触发也不恢复 */
        if (r->shelved_until && wall >= r->shelved_until) {
            HMI_LOG("alarm: 规则 %d 搁置到期（%s 搁的），恢复判定", r->id,
                    r->shelved_by[0] ? r->shelved_by : "?");
            r->shelved_until = 0;
            r->shelved_by[0] = '\0';
        }
        if (r->shelved_until) continue;

        if (r->type == AL_T_COMM) {
            int online = modbus_device_online(s_proj, r->device);
            int was_normal = (r->state == AL_S_NORMAL);

            r->value = (online == 0) ? 1.0 : 0.0;     /* 1 = 断线 */
            bad = (online == 0);                      /* -1（查不到）按在线处理，不误报 */
            if (bad && was_normal)
                HMI_LOG("alarm: 设备 %s 通讯中断（规则 %d）", r->device, r->id);
            else if (!bad && !was_normal)
                HMI_LOG("alarm: 设备 %s 通讯恢复（规则 %d）", r->device, r->id);
        } else if (r->type == AL_T_RATE) {
            double slope = 0;

            if (r->tag < 0) continue;
            r->value = tagdb_num(r->tag);
            if (r->rate_t0 == 0) {               /* 第一次进：开窗 */
                r->rate_t0 = now;
                r->rate_v0 = r->value;
                continue;
            }
            if (now - r->rate_t0 < r->window_ms) continue;   /* 窗口没满，先攒着 */
            {
                double secs = (double)(now - r->rate_t0) / 1000.0;

                slope = (secs > 0.001) ? (r->value - r->rate_v0) / secs : 0;
            }
            r->rate_slope = slope;
            /* limit > 0 判"上升过快"，limit < 0 判"下降过快" */
            bad = (r->limit > 0) ? (slope > r->limit) : (slope < r->limit);
            r->rate_t0 = now;                    /* 滚动窗口 */
            r->rate_v0 = r->value;
        } else {
            if (r->tag < 0) continue;
            r->value = tagdb_num(r->tag);
            bad = rule_violated(r, r->value);
        }

        if (!bad) {
            r->pending_since = -1;
            if (r->state != AL_S_ACTIVE && r->state != AL_S_ACKED) continue;
            ev.ts = wall;
            ev.rule_id = (uint16_t)r->id;
            ev.event = AL_E_CLEAR;
            ev.level = (uint8_t)r->level;
            ev.value = (float)r->value;
            (void)alarm_store_append(r->group, &ev);
            s_clear_rule = r->id;                 /* 报警条用它显示几秒"已恢复" */
            s_clear_ts = wall;
            r->state = AL_S_NORMAL;               /* 回到正常，移出活动表 */
            r->ack_time = 0;
            continue;
        }
        if (r->pending_since < 0) r->pending_since = now;
        if (r->state != AL_S_NORMAL) continue;   /* 已经在活动表里 */
        if (now - r->pending_since < r->delay_ms) continue;

        /* ack:false 的规则触发即算"已成既定事实"：进活动表但不进未确认，
         * 确认动作也会被拒（见 alarm_ack），免得现场对着一条不需要确认的报警按确认。 */
        r->state = r->ack_needed ? AL_S_ACTIVE : AL_S_ACKED;
        r->active_since = wall;
        r->ack_time = 0;
        ev.ts = wall;
        ev.rule_id = (uint16_t)r->id;
        ev.event = AL_E_TRIGGER;
        ev.level = (uint8_t)r->level;
        ev.value = (float)r->value;
        (void)alarm_store_append(r->group, &ev);
        if (!r->ack_needed)
            HMI_LOG("alarm: 规则 %d 触发（ack:false，不需要确认）", r->id);
        if (r->act_do == 1) (void)tagdb_write_num(r->act_tag, r->act_value);
        else if (r->act_do == 2) (void)tagdb_write_bool(r->act_tag, 1);
    }
}

static int group_match(const char* rule_group, const char* want)
{
    if (!want || !*want) return 1;
    return strcmp(rule_group, want) == 0;
}

int alarm_active_count(const char* group)
{
    int n = 0;

    for (int i = 0; i < s_nrules; i++)
        if (s_rules[i].state != AL_S_NORMAL && group_match(s_rules[i].group, group)) n++;
    return n;
}

int alarm_unacked_count(const char* group)
{
    int n = 0;

    for (int i = 0; i < s_nrules; i++)
        if (s_rules[i].state == AL_S_ACTIVE && group_match(s_rules[i].group, group)) n++;
    return n;
}

int alarm_enum(const char* group, int only_unacked, alarm_view_t* out, int max)
{
    int n = 0;

    if (!out || max < 1) return 0;
    for (int i = 0; i < s_nrules && n < max; i++) {
        const alarm_rule_t* r = &s_rules[i];

        if (r->state == AL_S_NORMAL) continue;
        if (only_unacked && r->state != AL_S_ACTIVE) continue;
        if (!group_match(r->group, group)) continue;
        out[n].rule_id = r->id;
        out[n].level = r->level;
        out[n].shelved_until = r->shelved_until;
        out[n].state = r->state;
        out[n].noack = !r->ack_needed;
        out[n].active_since = r->active_since;
        out[n].ack_time = r->ack_time;
        out[n].value = r->value;
        snprintf(out[n].name, sizeof(out[n].name), "%s", r->name);
        snprintf(out[n].text, sizeof(out[n].text), "%s", rule_text(r));
        snprintf(out[n].group, sizeof(out[n].group), "%s", r->group);
        n++;
    }
    return n;
}

int alarm_ack(int rule_id)
{
    for (int i = 0; i < s_nrules; i++) {
        alarm_rule_t* r = &s_rules[i];

        if (r->id != rule_id) continue;
        if (!r->ack_needed) {
            HMI_LOG("alarm: 规则 %d 配了 ack:false，不接受确认（也不写确认事件）", rule_id);
            return -1;
        }
        if (r->state != AL_S_ACTIVE) continue;
        r->state = AL_S_ACKED;
        r->ack_time = (uint32_t)time(NULL);
        {
            alarm_event_t ev;

            ev.ts = r->ack_time;
            ev.rule_id = (uint16_t)r->id;
            ev.event = AL_E_ACK;
            ev.level = (uint8_t)r->level;
            ev.value = (float)r->value;
            (void)alarm_store_append(r->group, &ev);
        }
        audit_log("alarm-ack", NULL, "rule=%d group=%s level=%d", r->id, r->group,
                  r->level);
        return 0;
    }
    return -1;
}

int alarm_ack_all(const char* group)
{
    int n = 0;

    for (int i = 0; i < s_nrules; i++) {
        alarm_rule_t* r = &s_rules[i];

        if (r->state != AL_S_ACTIVE || !group_match(r->group, group)) continue;
        if (alarm_ack(r->id) == 0) n++;
    }
    return n;
}

/* 最近一条恢复事件（报警条在没有活动报警时显示几秒恢复文案）。
 * within_s > 0 时只认这么多秒以内的；返回 1 表示有可用的。 */
int alarm_recent_clear(int* rule_id, uint32_t* ts, int within_s)
{
    if (s_clear_rule < 0) return 0;
    if (within_s > 0 && (uint32_t)time(NULL) - s_clear_ts > (uint32_t)within_s) return 0;
    if (rule_id) *rule_id = s_clear_rule;
    if (ts) *ts = s_clear_ts;
    return 1;
}

int alarm_rule_clear_text(int id, char* out, size_t outsz)
{
    if (out && outsz) out[0] = '\0';
    for (int i = 0; i < s_nrules; i++) {
        const alarm_rule_t* r = &s_rules[i];

        if (r->id != id) continue;
        {
            const char* clr = rule_clear_text(r);

            if (!clr) return -1;                   /* 没配：调用方回退到 text */
            if (out && outsz) snprintf(out, outsz, "%s", clr);
        }
        return 0;
    }
    return -1;
}

int alarm_rule_info(int id, char* name, size_t nsz, char* text, size_t tsz,
                    int* level, int* type)
{
    for (int i = 0; i < s_nrules; i++) {
        const alarm_rule_t* r = &s_rules[i];

        if (r->id != id) continue;
        if (name && nsz) snprintf(name, nsz, "%s", r->name);
        if (text && tsz) snprintf(text, tsz, "%s", rule_text(r));
        if (level) *level = r->level;
        if (type) *type = r->type;
        return 0;
    }
    return -1;
}

/* 归并：时间新的在前，与单组读取的顺序一致 */
static int ev_newer_first(const void* a, const void* b)
{
    const alarm_event_t* x = (const alarm_event_t*)a;
    const alarm_event_t* y = (const alarm_event_t*)b;

    if (x->ts > y->ts) return -1;
    if (x->ts < y->ts) return 1;
    return 0;
}

/* 历史事件：group 为空表示所有组。
 * 事件文件按组名哈希分开存（见 alarm_store），所以"全部组"要按规则里出现过的组名
 * 逐组读回来，再按时间归并——活动的报警能在内存里一次遍历，历史不能。 */
int alarm_history(const char* group, uint32_t from, uint32_t to,
                  alarm_event_t* out, int max)
{
    static int s_last_logged = -1;
    int n;

    if (!out || max < 1) return 0;

    if (group && *group) {
        n = alarm_store_read(group, from, to, out, max);
    } else {
        const char* groups[ALARM_GROUP_MAX];
        int ng = 0, total = 0;
        alarm_event_t* buf;

        for (int i = 0; i < s_nrules; i++) {
            int seen = 0;

            for (int k = 0; k < ng; k++)
                if (strcmp(groups[k], s_rules[i].group) == 0) { seen = 1; break; }
            if (!seen && ng < ALARM_GROUP_MAX) groups[ng++] = s_rules[i].group;
        }
        if (ng == 0) return 0;
        buf = malloc((size_t)ng * (size_t)max * sizeof(*buf));
        if (!buf) return 0;
        for (int k = 0; k < ng; k++) {
            int m = alarm_store_read(groups[k], from, to, buf + total, max);

            if (m > 0) total += m;
        }
        qsort(buf, (size_t)total, sizeof(*buf), ev_newer_first);
        n = (total < max) ? total : max;
        if (n > 0) memcpy(out, buf, (size_t)n * sizeof(*out));
        free(buf);
    }

    /* 条数变了才记一笔：现场"历史表是空的"要能一眼看出是没事件还是没读到 */
    if (n != s_last_logged) {
        HMI_LOG("alarm: 历史事件 %s：%d 条", (group && *group) ? group : "全部组",
                n < 0 ? 0 : n);
        s_last_logged = n;
    }
    return n;
}

/* ── 搁置（46 §8.1）── */

static alarm_rule_t* rule_by_id(int id)
{
    for (int i = 0; i < s_nrules; i++)
        if (s_rules[i].id == id) return &s_rules[i];
    return NULL;
}

int alarm_shelve(int rule_id, int minutes, const char* by)
{
    alarm_rule_t* r = rule_by_id(rule_id);

    if (!r) return -1;
    if (minutes < 1 || minutes > 480) {          /* 1 分钟 .. 8 小时 */
        HMI_LOG("alarm: 规则 %d 的搁置时长 %d 分钟越界（1..480）", rule_id, minutes);
        return -1;
    }
    r->shelved_until = (uint32_t)time(NULL) + (uint32_t)minutes * 60u;
    snprintf(r->shelved_by, sizeof(r->shelved_by), "%s", by ? by : "");
    HMI_LOG("alarm: 规则 %d 搁置 %d 分钟（%s）", rule_id, minutes,
            (by && *by) ? by : "未登录");
    audit_log("alarm-shelve", NULL, "rule=%d %d 分钟", rule_id, minutes);
    return 0;
}

int alarm_unshelve(int rule_id)
{
    alarm_rule_t* r = rule_by_id(rule_id);

    if (!r) return -1;
    if (!r->shelved_until) return 0;
    r->shelved_until = 0;
    r->shelved_by[0] = '\0';
    HMI_LOG("alarm: 规则 %d 取消搁置", rule_id);
    audit_log("alarm-unshelve", NULL, "rule=%d", rule_id);
    return 0;
}

int alarm_shelved_remaining(int rule_id)
{
    const alarm_rule_t* r = rule_by_id(rule_id);
    uint32_t now;

    if (!r || !r->shelved_until) return 0;
    now = (uint32_t)time(NULL);
    return (r->shelved_until > now) ? (int)(r->shelved_until - now) : 0;
}

int alarm_rule_isa(int id, int* priority, int* allowed_ms,
                   char* cause, size_t csz, char* consequence, size_t nsz)
{
    for (int i = 0; i < s_nrules; i++) {
        const alarm_rule_t* r = &s_rules[i];

        if (r->id != id) continue;
        if (priority) *priority = r->priority;
        if (allowed_ms) *allowed_ms = r->allowed_ms;
        if (cause && csz) snprintf(cause, csz, "%s", r->cause);
        if (consequence && nsz) snprintf(consequence, nsz, "%s", r->consequence);
        return 0;
    }
    return -1;
}
