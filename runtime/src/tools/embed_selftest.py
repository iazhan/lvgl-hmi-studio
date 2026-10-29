#!/usr/bin/env python3
"""embed_selftest.py —— 验证 libchenge_runtime.so 能被非 C 宿主加载并出图。

跑法（先在 chenge-runtime 里 make EMBED=1）：
    cd /home/chenge/f1c100s-project
    chenge-hmi/chenge-runtime/tools/embed_selftest.py [工程目录]

判据（任何一条不过就非零退出）：
  1. ABI 版本一致；
  2. open 成功（0）；
  3. 泵若干帧后 frame() 非空，且画面不是全黑（> 1% 像素有内容）；
  4. 点一下按钮，变量「启动」从 0 变 1（与 golden g01 同一判据）；
  5. 能存出 BMP（大小 = 54 + 800*480*4）。
"""
import ctypes
import os
import sys
import time

RT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.abspath(os.path.join(RT, "..", ".."))
DEFAULT_PROJECT = os.path.join(ROOT, "spec", "golden", "g01_minimal")

lib = ctypes.CDLL(os.path.join(RT, "libchenge_runtime.so"))
lib.chenge_embed_abi.restype = ctypes.c_int
lib.chenge_embed_open.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int]
lib.chenge_embed_open.restype = ctypes.c_int
lib.chenge_embed_screen.argtypes = [ctypes.POINTER(ctypes.c_int)] * 2
lib.chenge_embed_frame.argtypes = [ctypes.POINTER(ctypes.c_int)] * 3
lib.chenge_embed_frame.restype = ctypes.c_void_p
lib.chenge_embed_save_bmp.argtypes = [ctypes.c_char_p]
lib.chenge_embed_save_bmp.restype = ctypes.c_int
lib.chenge_embed_tag_bool.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_int)]
lib.chenge_embed_tag_bool.restype = ctypes.c_int
lib.chenge_embed_project_clear.restype = None
lib.chenge_embed_project_put.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int]
lib.chenge_embed_project_put.restype = ctypes.c_int
lib.chenge_embed_open_mem.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int]
lib.chenge_embed_open_mem.restype = ctypes.c_int


def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)


def pump(n, dt=0.016):
    """按真实节拍泵帧。

    LVGL 的输入读取与定时器都看**墙钟时间**：连续空转调用 tick 而完全不推进时间，
    点击会被"吞掉"（按下与抬起落在同一时刻，LVGL 判不成一次 click）。宿主是 16ms
    一次的，所以这里也睡够 16ms。
    """
    for _ in range(n):
        lib.chenge_embed_tick()
        time.sleep(dt)


if lib.chenge_embed_abi() != 1:
    fail(f"ABI 版本 {lib.chenge_embed_abi()} != 1")

args = [a for a in sys.argv[1:] if not a.startswith("--")]
project = os.path.abspath(args[0]) if args else os.path.abspath(DEFAULT_PROJECT)
log_dir = os.path.join(ROOT, "logs", "embed")
data_dir = os.path.join(log_dir, "data")
os.makedirs(data_dir, exist_ok=True)

err = ctypes.create_string_buffer(256)
memfs = "--memfs" in sys.argv
if memfs:
    # 模拟编辑器：把工程文件塞进内存表，再从内存启动（不写任何工程文件到磁盘）
    lib.chenge_embed_project_clear()
    n_put = 0
    for root_, _dirs, files in os.walk(project):
        for name in files:
            full = os.path.join(root_, name)
            rel = os.path.relpath(full, project).replace(os.sep, "/")
            blob = open(full, "rb").read()
            if lib.chenge_embed_project_put(rel.encode(), blob, len(blob)) != 0:
                fail(f"project_put 失败：{rel}")
            n_put += 1
    print(f"内存工程：{n_put} 个文件")
    rc = lib.chenge_embed_open_mem(data_dir.encode(), err, 256)
else:
    rc = lib.chenge_embed_open(project.encode(), data_dir.encode(), err, 256)
if rc != 0:
    fail(f"open rc={rc} err={err.value.decode(errors='replace')}")

pump(10)

w, h, stride = ctypes.c_int(0), ctypes.c_int(0), ctypes.c_int(0)
lib.chenge_embed_screen(ctypes.byref(w), ctypes.byref(h))
ptr = lib.chenge_embed_frame(ctypes.byref(w), ctypes.byref(h), ctypes.byref(stride))
if not ptr:
    fail("frame() 返回空")
if (w.value, h.value, stride.value) != (800, 480, 3200):
    fail(f"帧几何不对：{w.value}x{h.value} stride {stride.value}")

raw = ctypes.string_at(ptr, stride.value * h.value)
step = 4 * 97
nonblack = sum(1 for i in range(0, len(raw), step) if raw[i] or raw[i + 1] or raw[i + 2])
total = len(range(0, len(raw), step))
ratio = nonblack / total
print(f"frame {w.value}x{h.value} stride={stride.value} 非黑采样比 {ratio:.1%}")
if ratio < 0.01:
    fail("画面几乎全黑（渲染没起来）")

val = ctypes.c_int(-1)
if not lib.chenge_embed_tag_bool("启动".encode(), ctypes.byref(val)):
    fail("工程里没有变量「启动」")
print("点击前 启动 =", val.value)

lib.chenge_embed_pointer(100, 148, 1)
pump(3)
lib.chenge_embed_pointer(100, 148, 0)
pump(10)

lib.chenge_embed_tag_bool("启动".encode(), ctypes.byref(val))
print("点击后 启动 =", val.value)
if val.value != 1:
    fail("点击没有写进变量（按钮链路不通）")

bmp = os.path.join(log_dir, "selftest.bmp")
if lib.chenge_embed_save_bmp(bmp.encode()) != 0:
    fail("save_bmp 失败")
size = os.path.getsize(bmp)
if size != 54 + 800 * 480 * 4:
    fail(f"BMP 大小不对：{size}")

# 整屏重绘成本：LVGL 的刷新由 refresh timer 驱动（周期 ~33ms），所以必须让时间
# 真的过去才量得到重绘。这里每帧 invalidate + tick + 睡 33ms，只累计 tick 自身耗时。
busy = 0.0
for _ in range(30):
    lib.chenge_embed_invalidate()
    a = time.perf_counter()
    lib.chenge_embed_tick()
    busy += time.perf_counter() - a
    time.sleep(0.033)
per = busy / 30 * 1000
print(f"整屏重绘平均 {per:.2f} ms/帧（800x480 ARGB8888，含 LVGL 重绘 + 平台泵）")
if per > 16:
    fail(f"单帧 {per:.1f}ms 超过 16ms，60Hz 跑不动")

lib.chenge_embed_close()

# 重开：编辑器每改一次就预览一次，close→open 必须能复用同一进程
err = ctypes.create_string_buffer(256)
if lib.chenge_embed_open(project.encode(), data_dir.encode(), err, 256) != 0:
    fail(f"重开失败：{err.value.decode(errors='replace')}")
pump(6)
ptr = lib.chenge_embed_frame(ctypes.byref(w), ctypes.byref(h), ctypes.byref(stride))
if not ptr:
    fail("重开后没有帧")
lib.chenge_embed_close()

print("OK: 库加载 / 出图 / 点击写变量 / 截图 / 重开 全部通过")
