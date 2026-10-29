/*
 * i18n.h — 多语言（37 §6.10 / ADR-0013）
 *
 * strings.json 在工程根，只读；当前语言是设备状态，存 <data>/state/language。
 * 控件文本写 "@id" 时按当前语言取值；文本里的 {tag:<id>|<fmt>} 在运行期按变量值替换，
 * {ver} 换成编译进二进制的运行时版本（工程里不必手抄版本号）。
 */
#ifndef HMI_I18N_H
#define HMI_I18N_H

#include <stddef.h>

#include "project/loader.h"

#define I18N_MAX_LANGS 4

/* 加载 strings.json（没有就返回 0，功能不启用）；恢复设备上次选的语言 */
int  i18n_load(const project_t* p, const char* data_dir, char* err, size_t errsz);
int  i18n_enabled(void);
int  i18n_lang_count(void);
const char* i18n_lang_code(int i);
const char* i18n_lang_name(int i);
const char* i18n_lang(void);                 /* 当前语言代码 */

/* 切语言：写 <data>/state/language；调用方负责重建当前页 */
int  i18n_switch(const char* code, char* err, size_t errsz);

/* 文本里是否有 {tag: 占位符（含占位符的控件要定时重算） */
int  i18n_has_placeholder(const char* s);

/* 运行时界面文本（登录框 / 键盘 / 提示条这类内置 UI）：
 * 先查 strings.json 的 "runtime.<key>"（现场可改措辞、可加语言），
 * 查不到用编译进程序的内置表（zh / en），再没有就返回 key 本身（便于发现漏项）。 */
const char* i18n_rt(const char* key);

/* 按 id 取当前语言的文本（缺译回退 default）；没有这个 id 返回 NULL。
 * 给"存 id、用时才取文案"的地方用（报警规则的 text_id），切语言后不用重建数据表。 */
const char* i18n_lookup(const char* id);

/* 渲染：@id → 当前语言文本（缺译回退 default），再把 {tag:...} 换成变量值。
 * 结果写入 out；src 为空时 out 置空串。 */
void i18n_render(const char* src, char* out, size_t outsz);

/* 便捷包装：返回渲染结果（写入调用方给的 buf），src 为空返回 ""。
 * 控件与表格列头都用它，省去每处判空。 */
const char* i18n_text(const char* src, char* buf, size_t n);

/* 同一件事，但缓冲由 i18n 自己的静态区按输入长度自适应（大文本不会被 96/160 字节截断）。
 * 单线程 UI 用完即弃：lv_label_set_text 会拷贝；不要跨调用保存返回指针。 */
const char* i18n_text_static(const char* src);

#endif /* HMI_I18N_H */
