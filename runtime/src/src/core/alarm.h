/*
 * alarm.h — 报警引擎（ADR-0008）
 *
 * 规则来自工程根的 alarms.json（字段见 37 §6.9）；主循环每 100ms 调一次 alarm_tick()，
 * 越限持续 delay_ms 才触发，恢复要跨过死区（deadband）才清。
 * 活动状态只在内存里（重启按当前值重新判定，不保留未确认状态）；
 * 触发/恢复/确认三种事件追加到 alarms/<组>.bin 的环形文件，供列表回看。
 *
 * 不依赖 LVGL：控件通过 alarm_enum() / alarm_history() 拉数据。
 */
#ifndef HMI_ALARM_H
#define HMI_ALARM_H

#include <stddef.h>
#include <stdint.h>

#include "project/loader.h"

#define ALARM_MAX_RULES  256
#define ALARM_GROUP_MAX  24
#define ALARM_TEXT_MAX   64
#define ALARM_NAME_MAX   40

/* 规则类型（与 alarms.json 的 type 一致）
 * deviation = 与参考变量的偏差越界；rate = 窗口内的变化率越界（46 §8.1） */
enum {
    AL_T_HI = 0, AL_T_HI_HI, AL_T_LO, AL_T_LO_LO, AL_T_BOOL, AL_T_COMM,
    AL_T_DEVIATION, AL_T_RATE
};

/* 状态：0 正常（不在活动表）、1 未确认、2 已确认（恢复后直接回到 0） */
enum { AL_S_NORMAL = 0, AL_S_ACTIVE = 1, AL_S_ACKED = 2 };

/* 事件类型 */
enum { AL_E_TRIGGER = 0, AL_E_CLEAR = 1, AL_E_ACK = 2 };

typedef struct {
    uint32_t ts;
    uint16_t rule_id;
    uint8_t  event;
    uint8_t  level;
    float    value;
} alarm_event_t;                     /* 12 字节，落盘格式与之一致 */

typedef struct {
    int      rule_id;
    int      level;
    int      state;                  /* AL_S_* */
    int      noack;                  /* 1 = 规则配了 ack:false，不需要确认 */
    uint32_t active_since;
    uint32_t ack_time;
    double   value;
    uint32_t shelved_until;          /* 搁置到期的墙钟秒，0 = 未搁置 */
    char     name[ALARM_NAME_MAX];
    char     text[ALARM_TEXT_MAX];
    char     group[ALARM_GROUP_MAX];
} alarm_view_t;

/* 加载 alarms.json；data_dir 是事件文件根（其下建 alarms/）。返回规则数，失败 -1。 */
int  alarm_load(const project_t* p, const char* data_dir, char* err, size_t errsz);
void alarm_free(void);
int  alarm_rule_count(void);

/* 主循环每 100ms 调一次 */
void alarm_tick(void);

/* 活动表查询；group 为 NULL 或空串表示全部组 */
int  alarm_active_count(const char* group);
int  alarm_unacked_count(const char* group);
int  alarm_enum(const char* group, int only_unacked, alarm_view_t* out, int max);

/* 确认：返回 0 成功、-1 该规则不存在或不在活动表；alarm_ack_all 返回确认条数 */
int  alarm_ack(int rule_id);
int  alarm_ack_all(const char* group);

/* 搁置（46 §8.1）：minutes 分钟后自动恢复；by 记操作人（可空）。
 * 搁置期间跳过判定——已有状态冻结，既不新触发也不恢复。 */
int  alarm_shelve(int rule_id, int minutes, const char* by);
int  alarm_unshelve(int rule_id);
int  alarm_shelved_remaining(int rule_id);   /* 剩余秒；0 = 未搁置 */

/* ISA-18.2 的存而不用字段（报表/审计用，不参与判定） */
int  alarm_rule_isa(int id, int* priority, int* allowed_ms,
                    char* cause, size_t csz, char* consequence, size_t nsz);

/* 按规则 id 查名字 / 描述 / 等级 / 类型（事件里只有 id，列表要显示文字）。
 * 找到返回 0，找不到返回 -1。name/text 可为 NULL。 */
int  alarm_rule_info(int id, char* name, size_t nsz, char* text, size_t tsz,
                     int* level, int* type);

/* 规则配的"恢复"文案（alarms.json 的 text_clear）。没配时 out 置空串、返回 -1，
 * 调用方回退到 alarm_rule_info() 给的 text。 */
int  alarm_rule_clear_text(int id, char* out, size_t outsz);

/* 最近一条恢复事件（报警条用）：within_s>0 只认这么多秒以内的；有则返回 1。 */
int  alarm_recent_clear(int* rule_id, uint32_t* ts, int within_s);

/* 读事件（from/to 是 unix 秒，0 表示不限）；返回条数，**新的在前**。
 * group 为 NULL 或空串表示全部组：逐组读事件文件后按时间归并。 */
int  alarm_history(const char* group, uint32_t from, uint32_t to,
                   alarm_event_t* out, int max);

#endif /* HMI_ALARM_H */
