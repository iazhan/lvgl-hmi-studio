/*
 * ui_font.c — 字体缓存实现
 */
#include "ui_font.h"
#include "hmi_log.h"
#include "hmi_paths.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

LV_FONT_DECLARE(lv_font_source_han_sans_sc_14_cjk);
LV_FONT_DECLARE(lv_font_source_han_sans_sc_16_cjk);

#define FONT_CACHE_MAX 12     /* 字号档位上限（37 §5.11 是 8；加粗/斜体各占一档，留点余量） */
#define CN_FONT_FILE  "font_cjk_sc.ttf"

static const char* const s_dirs[] = {
    "/usr/lib/hmi/fonts/",
    HMI_ETC_DIR "/fonts/",
};

typedef struct {
    int size;
    int style;                      /* 0 normal / 1 bold / 2 italic */
    const lv_font_t* f;
} font_ent_t;

static font_ent_t s_cache[FONT_CACHE_MAX];
static int s_cache_count;
static const char* s_cn_path;      /* 解析到的 TTF 路径；NULL = 没有 */
static int s_cn_resolved;

/* 在候选目录里定位 CJK 字体；宿主可用 HMI_FONT_DIR 追加一个目录（同 chenge-rt） */
static const char* cn_font_path(void)
{
    static char path[256];
    const char* extra;
    size_t i;

    if (s_cn_resolved) return s_cn_path;
    s_cn_resolved = 1;

    extra = getenv("HMI_FONT_DIR");
    if (extra && extra[0]) {
        size_t n = strlen(extra);
        snprintf(path, sizeof(path), "%s%s%s", extra,
                 extra[n - 1] == '/' ? "" : "/", CN_FONT_FILE);
        if (access(path, R_OK) == 0) {
            s_cn_path = path;
            return s_cn_path;
        }
    }
    for (i = 0; i < sizeof(s_dirs) / sizeof(s_dirs[0]); i++) {
        snprintf(path, sizeof(path), "%s%s", s_dirs[i], CN_FONT_FILE);
        if (access(path, R_OK) == 0) {
            s_cn_path = path;
            return s_cn_path;
        }
    }
    s_cn_path = NULL;
    return NULL;
}

const lv_font_t* ui_font_ex(int size, int style)
{
    lv_freetype_font_style_t st;

    if (size <= 0) size = 16;
    if (style < 0 || style > 2) style = 0;
    st = style == 1 ? LV_FREETYPE_FONT_STYLE_BOLD
       : style == 2 ? LV_FREETYPE_FONT_STYLE_ITALIC
                    : LV_FREETYPE_FONT_STYLE_NORMAL;

    for (int i = 0; i < s_cache_count; i++)
        if (s_cache[i].size == size && s_cache[i].style == style) return s_cache[i].f;

    /* 缓存满就不再创建：lv_freetype_font_create 每次都是新分配，不缓存会漏内存。
     * 用"最接近的已缓存字号"顶替，而不是回退内嵌子集——内嵌字体只保证 GB2312 常用字，
     * 一退回去，℃/℉/Ω 这类符号整片变方框（实测：工程用了 9 种字号，第 9 种起全缺符号）。 */
    if (s_cache_count >= FONT_CACHE_MAX) {
        int best = 0;

        for (int i = 1; i < s_cache_count; i++) {
            int d_new = abs(s_cache[i].size - size) + (s_cache[i].style == style ? 0 : 100);
            int d_best = abs(s_cache[best].size - size) + (s_cache[best].style == style ? 0 : 100);

            if (d_new < d_best) best = i;
        }
        {
            static int warned;

            if (!warned) {
                warned = 1;
                HMI_LOG("font: 字号 %d 超过缓存上限 %d，按最接近的 %d 渲染（符号字形不受影响）",
                        size, FONT_CACHE_MAX, s_cache[best].size);
            }
        }
        return s_cache[best].f;
    }

    {
        const lv_font_t* f = NULL;
        const char* path = cn_font_path();

        if (path) {
            f = lv_freetype_font_create(path, LV_FREETYPE_FONT_RENDER_MODE_BITMAP,
                                        (uint32_t)size, st);
        }
        /* 回退：按 size 选最近的内嵌子集（小字号回退到 16 会发虚） */
        if (!f) f = (size <= 14) ? &lv_font_source_han_sans_sc_14_cjk
                                 : &lv_font_source_han_sans_sc_16_cjk;

        s_cache[s_cache_count].size = size;
        s_cache[s_cache_count].style = style;
        s_cache[s_cache_count].f = f;
        s_cache_count++;
        return f;
    }
}

const lv_font_t* ui_font(int size)
{
    return ui_font_ex(size, 0);
}
