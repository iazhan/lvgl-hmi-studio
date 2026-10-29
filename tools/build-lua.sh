#!/usr/bin/env bash
# ============================================================================
# build-lua.sh —— 把 third_party/lua-5.4.7 编成编辑器用的原生库
#
# 为什么要自己编：脚本编辑器的语法校验必须与设备端**同一个解释器**（运行时用的是
# lua 5.4.7）。此前用的 MoonSharp 只实现到 Lua 5.2，5.3/5.4 的合法语法在编辑器里
# 会被判成语法错，而 5.4 已移除的 5.1 遗留物（全局 unpack / bit32）又不报。
#
# 产物（都进 git，打包脚本直接带）：
#   third_party/lua-5.4.7/lib/linux-x64/liblua54.so
#   third_party/lua-5.4.7/lib/win-x64/lua54.dll
#
# 用法：
#   bash tools/build-lua.sh              # 编两个平台（缺交叉编译器时跳过该平台）
#   bash tools/build-lua.sh --linux      # 只编 Linux
#   bash tools/build-lua.sh --windows    # 只编 Windows
#   bash tools/build-lua.sh --clean
#
# 源码面：src/ 下除 lua.c / luac.c / onelua.c（分别是解释器/编译器/单文件聚合的 main）。
# ============================================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
SRC="$ROOT/third_party/lua-5.4.7/src"
LIB="$ROOT/third_party/lua-5.4.7/lib"

BUILD_LINUX=0
BUILD_WIN=0
CLEAN=0

while [ $# -gt 0 ]; do
    case "$1" in
        --linux)   BUILD_LINUX=1 ;;
        --windows) BUILD_WIN=1 ;;
        --clean)   CLEAN=1 ;;
        -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d'; exit 0 ;;
        *) echo "未知参数：$1" >&2; exit 2 ;;
    esac
    shift
done

if [ "$BUILD_LINUX" = 0 ] && [ "$BUILD_WIN" = 0 ]; then
    BUILD_LINUX=1
    BUILD_WIN=1
fi

[ -d "$SRC" ] || { echo "缺源码目录：$SRC" >&2; exit 2; }

step() { printf '\n== %s ==\n' "$*"; }

if [ "$CLEAN" = 1 ]; then
    rm -rf "$LIB"
    echo "已清 $LIB"
    exit 0
fi

SOURCES=()
for f in "$SRC"/*.c; do
    case "$(basename "$f")" in
        lua.c|luac.c|onelua.c) continue ;;
    esac
    SOURCES+=("$f")
done
[ "${#SOURCES[@]}" -ge 30 ] || { echo "源码数异常（${#SOURCES[@]}）——目录不对？" >&2; exit 1; }

if [ "$BUILD_LINUX" = 1 ]; then
    step "Linux x86_64 → liblua54.so（${#SOURCES[@]} 个 .c）"
    mkdir -p "$LIB/linux-x64"
    gcc -O2 -fPIC -shared -DLUA_USE_POSIX -o "$LIB/linux-x64/liblua54.so" "${SOURCES[@]}" -lm
    printf '  %s（%.0f KB）\n' "$LIB/linux-x64/liblua54.so" \
        "$(awk -v b="$(stat -c%s "$LIB/linux-x64/liblua54.so")" 'BEGIN{print b/1024}')"
fi

if [ "$BUILD_WIN" = 1 ]; then
    if command -v x86_64-w64-mingw32-gcc-posix >/dev/null 2>&1; then
        step "Windows x64 → lua54.dll"
        mkdir -p "$LIB/win-x64"
        x86_64-w64-mingw32-gcc-posix -O2 -shared -DLUA_BUILD_AS_DLL \
            -o "$LIB/win-x64/lua54.dll" "${SOURCES[@]}"
        printf '  %s（%.0f KB）\n' "$LIB/win-x64/lua54.dll" \
            "$(awk -v b="$(stat -c%s "$LIB/win-x64/lua54.dll")" 'BEGIN{print b/1024}')"
    else
        echo "  跳过 Windows：没有 x86_64-w64-mingw32-gcc-posix（apt install gcc-mingw-w64-x86-64-posix）" >&2
    fi
fi

step "自检：用编好的库解析一段 5.4 语法"
if [ -s "$LIB/linux-x64/liblua54.so" ]; then
    python3 - "$LIB/linux-x64/liblua54.so" <<'PY'
import ctypes, sys
lib = ctypes.CDLL(sys.argv[1])
lib.luaL_newstate.restype = ctypes.c_void_p
lib.luaL_loadstring.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
lib.luaL_loadstring.restype = ctypes.c_int
lib.lua_tolstring.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_size_t)]
lib.lua_tolstring.restype = ctypes.c_char_p
lib.lua_close.argtypes = [ctypes.c_void_p]

L = lib.luaL_newstate()
cases = [
    (b"local x = 10 // 3\nlocal y = 0x0F & 0x03", 0, "5.3+ 整除与位运算"),
    (b"local a <const> = 1", 0, "5.4 属性语法"),
    (b"local function f() return 1 end", 0, "普通函数"),
    (b"local x = 1 +", 1, "残缺表达式（应报错）"),
]
bad = 0
for code, want_err, label in cases:
    rc = lib.luaL_loadstring(L, code)
    got_err = 0 if rc == 0 else 1
    ok = got_err == want_err
    msg = ""
    if rc != 0:
        n = ctypes.c_size_t()
        p = lib.lua_tolstring(L, -1, ctypes.byref(n))
        msg = (p[:n.value] if p else b"").decode("utf-8", "replace")
    print(f"  {'ok  ' if ok else 'FAIL'} {label}: {'语法通过' if got_err == 0 else msg}")
    if not ok: bad += 1
lib.lua_close(L)
sys.exit(1 if bad else 0)
PY
fi
