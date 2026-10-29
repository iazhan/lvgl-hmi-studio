/*
 * chenge_embed —— 把运行时当成库用（Linux .so / 将来 Windows .dll）
 *
 * 给编辑器做「可操作的实时预览」：不需要窗口、不需要 SDL。宿主自己拿帧
 * 去贴图（AVALONIA / Qt / 浏览器 canvas 都行），输入用 chenge_embed_pointer
 * 注进来。板端进程版（main.c）与嵌入版共用同一份启动序列（runtime_boot.c），
 * 所以"预览能跑"与"上板能跑"是同一条代码路径。
 *
 * 线程模型：**单线程**。open/tick/pointer/frame 必须在同一个线程里串行调用
 * （LVGL 不是线程安全的；运行时自己的 Modbus 线程是内部的，不关宿主的事）。
 *
 * 典型用法：
 *     char err[256];
 *     if (chenge_embed_open(dir, data_dir, err, sizeof err) != 0) { 显示 err }
 *     for (;;) { chenge_embed_tick(); 读 chenge_embed_frame() 贴图; }
 *     chenge_embed_close();
 *
 * 崩溃隔离：本库跑在宿主进程里，工程里的 Lua 死循环/越界会让**宿主一起挂**。
 * 要隔离就用同一个库编出的宿主进程（chenge-preview-host）走进程外帧通道。
 */
#ifndef CHENGE_EMBED_H
#define CHENGE_EMBED_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ABI 版本：宿主加载后先比一次，不符就拒绝使用（避免旧 .so 配新编辑器） */
#define CHENGE_EMBED_ABI 1

int chenge_embed_abi(void);

/*
 * 打开一个已导出的工程目录（含 manifest.json）。data_dir 是运行时私有数据目录
 * （日志/趋势/存盘/报警），必须可写；传 NULL 则按 HMI_DATA_DIR → /data/chenge
 * → <cwd>/.hmi-data 的顺序回落。
 *
 * 返回 0 成功；非 0 失败，原因写 err（可能为空串）。失败码与 runtime_boot 的
 * rt_boot_rc 一一对应（1 无工程 / 2 路径不可读 / 3 解析失败 / 4 内存 / 5 子系统 / 6 建页失败）。
 *
 * 同一进程只允许一个活动实例；再次 open 会返回非 0（先 close）。
 */
int chenge_embed_open(const char* project_dir, const char* data_dir, char* err, int errlen);

/*
 * 内存工程：不落盘，直接把编辑器序列化出来的文件放进内存表，再 open_mem。
 * path 必须是**相对工程根**的路径（"manifest.json" / "pages/home.json" /
 * "assets/img1.bin"）。典型顺序：
 *     chenge_embed_project_clear();
 *     for (每个文件) chenge_embed_project_put(rel, bytes, len);
 *     chenge_embed_open_mem(data_dir, err, sizeof err);
 *
 * 只有工程本体走内存；运行期写入（日志/趋势/存盘）仍落 data_dir。
 * 目录不存在也行 —— 装载器对内存表里的文件不做 stat。
 */
void chenge_embed_project_clear(void);
int  chenge_embed_project_put(const char* path, const void* data, int len);

/*
 * 用内存表里的工程启动（不需要真实工程目录）。表为空返回非 0。
 * data_dir 用来放运行期数据（可为 NULL，回落规则同 open）。
 */
int chenge_embed_open_mem(const char* data_dir, char* err, int errlen);

/* 推一拍：LVGL timer + 平台 poll + 100ms/1s 节流子系统。宿主按自己的刷新节拍调（建议 30~60Hz）。 */
void chenge_embed_tick(void);

/*
 * 只重绘一帧，不推进时间（不跑 LVGL timer、不跑子系统）。
 *
 * 设计画布用：属性补丁改完几何/属性后调它把画面刷出来，报警条不滚、曲线不取样、
 * 管道不流动 —— 同一份工程每次渲染的像素必须一样，否则无法逐像素对拍，
 * 用户也会误以为设计态"在跑"。要驱动运行时行为（仿真模式）请用 chenge_embed_tick。
 */
void chenge_embed_render_once(void);

/* 注入指针（像素坐标，左上原点）。down=1 按下，down=0 抬起。 */
void chenge_embed_pointer(int x, int y, int down);

/*
 * 取当前帧：返回指向 ARGB8888 整屏缓冲的指针（LVGL DIRECT 模式，行间可能有 padding，
 * 按 *stride 字节换行）。缓冲由库持有，下一次 tick 会被覆盖 —— 要保留请自己拷贝。
 * 返回 NULL 表示还没就绪。
 */
const void* chenge_embed_frame(int* w, int* h, int* stride);

/* 屏幕尺寸（LVGL display 创建后即可问，不需要先有帧）。返回 0 成功。 */
int chenge_embed_screen(int* w, int* h);

/* 把当前帧写成 32 位 BMP（调试/回归用；生产路径应该直接用 frame）。返回 0 成功。 */
int chenge_embed_save_bmp(const char* path);

/* 变量读取（按工程里的变量名）。没有这个变量返回 0 且不改写输出。 */
int chenge_embed_tag_num(const char* name, double* out);
int chenge_embed_tag_bool(const char* name, int* out);
int chenge_embed_tag_quality(const char* name);

/*
 * 变量类型：-1 = 没有这个变量；0 = 数值；1 = 布尔；2 = 字符串。
 * 编辑器/隔离宿主靠它决定"显示成 1/0 还是数字"（数值变量用 bool 接口读也是 0/1，
 * 光看 bool 接口分不出类型）。
 */
int chenge_embed_tag_kind(const char* name);

/* 让整屏失效重绘一次（宿主换尺寸/窗口被遮挡后用）。 */
void chenge_embed_invalidate(void);

/*
 * 重载页面：page_id 为空 = 重建当前页（改绑定的慢路径），非空 = 切到该页。
 * 与运行时自己的切页/登录重算走同一条路（page_mgr_reload / page_mgr_script_open），
 * 所以"预览看到的"与"板子上这样操作得到的"是同一段代码。返回 0 成功。
 */
int chenge_embed_reload_page(const char* page_id);

/*
 * 几何补丁：按控件 id 改位置/尺寸（不重建控件，事件与绑定保留）。
 *
 * 设计画布拖动/缩放时走这条（每 16ms 一条）；改颜色/文字这类属性不在这里，
 * 客户端发整页重载 —— 重建单个控件会丢事件（见 ui_page.h 的说明）。
 * w/h 传 0 表示只挪位置。返回 0 成功。
 */
int chenge_embed_patch_rect(const char* widget_id, int x, int y, int w, int h);

/*
 * 用调用方推来的**最新页面 JSON** 重建当前页。
 *
 * chenge_embed_reload_page(NULL) 是从宿主里的工程快照重读，设计画布拖出来的
 * 新坐标不在快照里 —— 用它重建会把控件拉回原位（实测踩过）。所以"停手后精确刷"
 * 必须走这条，页面 JSON 由客户端现场生成。
 * 返回 0 成功。
 */
int chenge_embed_reload_page_json(const char* json, int len);

/* 关闭并把能还的都还掉（页面/存盘/趋势/变量表/显示）。close 后可再 open。 */
void chenge_embed_close(void);

/* 最近一次失败的原因（open 失败时也可读，等价于 open 的 err）。 */
const char* chenge_embed_last_error(void);

#ifdef __cplusplus
}
#endif

#endif /* CHENGE_EMBED_H */
