/*
 * ui_font.h — 字号 → lv_font_t 的按需创建与缓存
 *
 * 字体走 FreeType 加载 CJK TTF（用户可输入任意文字，固定字库不够，37 §3 选型）；
 * 候选目录与 chenge-rt 一致（rootfs 安装位置），宿主另可用 HMI_FONT_DIR 追加
 * 一个目录。找不到文件或创建失败时回退内嵌思源黑体子集（仅 14/16），
 * 再不行回退 LV_FONT_DEFAULT——任何路径都不返回 NULL，画面不出白块。
 */
#ifndef HMI_UI_FONT_H
#define HMI_UI_FONT_H

#include "lvgl.h"

/* 取指定字号的字体；永不为 NULL（最差是 LV_FONT_DEFAULT） */
const lv_font_t* ui_font(int size);

/*
 * 带字形的版本：style 0=normal 1=bold 2=italic（契约 label.font_style）。
 * 粗体/斜体走 FreeType 的合成样式（STYLE_BOLD/ITALIC），**不需要第二套字体文件**。
 */
const lv_font_t* ui_font_ex(int size, int style);

#endif /* HMI_UI_FONT_H */
