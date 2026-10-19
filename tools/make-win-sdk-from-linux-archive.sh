#!/usr/bin/env bash
# ============================================================================
# make-win-sdk-from-linux-archive.sh —— 用「已打好的 Linux 归档」+「Windows LLVM」
#                                    产出 win-x64 版 kinseal SDK 归档。
#
# 【为什么需要它】
#   Windows 上编 .so 用的是 clang（buildroot 的 gcc 是 Linux ELF，Windows 跑不了），
#   但 SDK 的其余部分（运行时契约头、LVGL 源树、ARM sysroot、libgcc.a）与平台无关，
#   全都已经在 Linux 归档里。重新跑一遍 package-kinseal-sdk.sh 要在 Windows 上备齐
#   chenge-rt 全树（在主仓库里，几十 GB），不现实 —— 所以这里只做「拆东墙补西墙」。
#
# 【用法】（Git Bash / MSYS2 里跑；tar、sha256sum 都是 Git for Windows 自带）
#   bash tools/make-win-sdk-from-linux-archive.sh \
#        --linux-archive /c/path/to/kinseal-sdk-1.2.0-linux-x64.tar.gz \
#        --llvm           /c/path/to/clang+llvm-19.1.1-x86_64-pc-windows-msvc \
#        [--version 1.2.0] [--out /c/path/to/sdk]
#
#   产物：<out>/kinseal-sdk-<ver>-win-x64.tar.gz + 同名 .manifest.json
#   把这两个文件复制到编辑器仓库根的 sdk/ 下，重新 build 编辑器即可随包分发。
#
# 【LLVM 下载】（约 844 MB，解压后取整个目录）
#   https://gh-proxy.com/https://github.com/llvm/llvm-project/releases/download/llvmorg-19.1.1/clang+llvm-19.1.1-x86_64-pc-windows-msvc.tar.xz
#   （国内用 gh-proxy；解包：tar -xJf <file>，得到 clang+llvm-<ver>-x86_64-pc-windows-msvc/）
# ============================================================================
set -euo pipefail

LINUX_ARCHIVE=""
LLVM_DIR=""
VERSION="1.2.0"
OUT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/sdk"

while [ $# -gt 0 ]; do
    # 容忍粘贴多行命令时残留的空白参数：行尾 "\ "（反斜杠+空格）在 bash 里是"转义空格"，
    # 会变成一个字面空格参数，严谨解析会在这里断掉。去掉首尾空白再匹配。
    arg="$(printf '%s' "${1:-}" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    shift
    [ -n "$arg" ] || continue
    case "$arg" in
        --linux-archive) LINUX_ARCHIVE="${1:-}"; shift ;;
        --llvm)           LLVM_DIR="${1:-}";       shift ;;
        --version)        VERSION="${1:-}";        shift ;;
        --out)            OUT_DIR="${1:-}";        shift ;;
        *) echo "未知参数: $arg" >&2; exit 2 ;;
    esac
done

[ -n "$LINUX_ARCHIVE" ] || { echo "缺少 --linux-archive（已打好的 Linux 版 SDK 归档）" >&2; exit 2; }
[ -n "$LLVM_DIR" ]       || { echo "缺少 --llvm（解压后的 LLVM for Windows 目录，含 bin/clang.exe）" >&2; exit 2; }
[ -f "$LINUX_ARCHIVE" ]  || { echo "找不到归档: $LINUX_ARCHIVE" >&2; exit 2; }
[ -f "$LLVM_DIR/bin/clang.exe" ] || [ -f "$LLVM_DIR/bin/clang" ] \
    || { echo "$LLVM_DIR 下没有 bin/clang(.exe)，不是 LLVM 发行版目录？" >&2; exit 2; }

NAME="kinseal-sdk-$VERSION-win-x64"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "==> [1/5] 从 Linux 归档抽出与平台无关的部分（runtime / sysroot / libgcc）"
mkdir -p "$WORK"
# runtime/ 整个要（契约头 + LVGL 源树）
tar -xzf "$LINUX_ARCHIVE" -C "$WORK" runtime
mkdir -p "$WORK/toolchain/sysroot"

# 从归档里抽一个子树，但**完全不创建符号链接/硬链接**（Windows 上 MSYS 的 tar 建不了）：
# 1) 只抽该子树下的普通文件/目录成员（用 -T 喂成员清单，把 l/ 开头的链接成员剔除），
#    这样 tar 根本不会去碰链接，不会报 "Cannot create symlink" 退出，所有目标 .so 都能落地；
# 2) 再从归档清单里读出该子树的链接（name -> target），用 cp -L 把目标复制成链接名
#    （目标此刻已落地，顺序无关、幂等），悬空的（指向 /proc、/tmp）直接丢弃。
# 只抽 usr/include + lib 两棵：dev/、etc/mtab 等悬空链接本就不抽；编译只要头文件，
# 链接只要 lib/（实测 lld 把 -lc 解析到 lib/libc.so.6，并自动带上 lib/ld-linux.so.3）。
# 用法：extract_tree_no_links <归档> <前缀> <落盘根> <去掉的前导段数>
# 只抽前缀下的普通文件/目录（剔除 l/h 链接成员，Windows 上 tar 建不了链接），
# 再从归档清单把该前缀下的链接重建为"实体副本"（cp -L）。顺序无关、幂等。
extract_tree_no_links() {
    local archive="$1" prefix="$2" out="$3" strip="$4"
    mkdir -p "$out"
    # 1) 普通文件（剔除链接成员与目录条目；目录由文件自动创建，放进 -T 反而会
    #    让 GNU tar 报"归档中找不到"，见实测）。这样 tar 根本不碰链接，也不会退出。
    tar -tzf "$archive" \
        | grep -E "^$prefix/" \
        | grep -vE '^[lh]' \
        | grep -v '/$' \
        | tar -xzf "$archive" -C "$out" --strip-components="$strip" -T - || true
    # 2) 重建链接为实体副本。
    # 注意：rel/tgt 是"相对 prefix 之后"的名字（如 ld-linux.so.3），但落到 out 下时
    # 还要带上 prefix 被 strip 掉的那几段（如 lib/）——否则会丢子目录（实测踩过）。
    local tailpart
    tailpart="$(echo "$prefix" | awk -F/ -v s="$strip" '{o="";for(i=s+1;i<=NF;i++)o=o (o==""?"":"/") $i; print o}')"
    tar -tvzf "$archive" \
        | grep -E "^[lh].*$prefix" \
        | while IFS= read -r line; do
              local path="${line#*$prefix/}"             # 去掉元数据与前缀，得 "名 -> 目标"
              local rel="${path%% -> *}"                  # 链接名（相对 prefix 之后）
              local tgt="${path##* -> }"                  # 链接目标（相对或绝对）
              [ "$rel" = "$path" ] && continue            # 没有 " -> "，跳过
              local src="$out/$tailpart/${tgt#/}"
              local dst="$out/$tailpart/$rel"
              if [ -e "$src" ]; then
                  rm -f "$dst"; cp -L --preserve=mode,timestamps "$src" "$dst"
              else
                  rm -f "$dst"                            # 悬空链接，丢弃
              fi
          done || true   # grep 无匹配（usr/include 里本就没有链接）会返回 1，别让 set -e 中断
}

# sysroot 在两种归档里位置不同：clang 版是 toolchain/sysroot，
# gcc 版是 toolchain/<target>/sysroot（buildroot 内嵌布局）——都要认。
if tar -tzf "$LINUX_ARCHIVE" | grep -q '^toolchain/sysroot/'; then
    SYS_SUBDIR="toolchain/sysroot"
    SYS_IN_ARCHIVE=1
else
    SYS_SUBDIR="$(tar -tzf "$LINUX_ARCHIVE" | grep -oE '^toolchain/[^/]+/sysroot' | sort -u | head -1)"
    [ -n "$SYS_SUBDIR" ] || { echo "归档里找不到 sysroot（toolchain/sysroot 或 toolchain/<target>/sysroot）" >&2; exit 1; }
    echo "      （gcc 版布局：$SYS_SUBDIR）"
    SYS_IN_ARCHIVE=0
fi

if [ "$SYS_IN_ARCHIVE" = "1" ]; then
    # clang 版：整个 sysroot 已经是"为 clang 准备过"的（无 glibc 软链），整体取出
    # 去掉前导 1 段（toolchain/），把 sysroot/ 落到 out 下
    extract_tree_no_links "$LINUX_ARCHIVE" toolchain/sysroot "$WORK/toolchain" 1
else
    # gcc 版：SYS_SUBDIR 形如 toolchain/<target>/sysroot，去掉这 3 段后，
    # lib、usr/include 才能正确落到 $WORK/toolchain/sysroot 下。
    SYS_DEPTH="$(echo "$SYS_SUBDIR" | awk -F/ '{print NF}')"
    echo "      （抽 usr/include + lib；悬空链接的 dev/ etc/ 不抽）"
    extract_tree_no_links "$LINUX_ARCHIVE" "$SYS_SUBDIR/usr/include" "$WORK/toolchain/sysroot" "$SYS_DEPTH"
    extract_tree_no_links "$LINUX_ARCHIVE" "$SYS_SUBDIR/lib" "$WORK/toolchain/sysroot" "$SYS_DEPTH"
fi
[ -f "$WORK/toolchain/sysroot/usr/include/stdio.h" ] \
    || { echo "sysroot 里没有 usr/include/stdio.h" >&2; exit 1; }
[ -f "$WORK/toolchain/sysroot/lib/libc.so.6" ] || [ -L "$WORK/toolchain/sysroot/lib/libc.so.6" ] \
    || { echo "sysroot 里没有 lib/libc.so.6（-lc 需要）" >&2; exit 1; }

echo "==> [2/5] 拷入 libgcc.a（软浮点 __aeabi_* 例程，clang 默认不链）"
mkdir -p "$WORK/toolchain/lib"
# 位置同样有两种：clang 版 toolchain/lib/libgcc.a；gcc 版 buildroot 的
# toolchain/lib/gcc/<target>/<ver>/libgcc.a
if tar -tzf "$LINUX_ARCHIVE" | grep -q '^toolchain/lib/libgcc.a$'; then
    tar -xzf "$LINUX_ARCHIVE" -C "$WORK" toolchain/lib/libgcc.a
else
    GCC_A="$(tar -tzf "$LINUX_ARCHIVE" | grep -E 'libgcc\.a$' | head -1)"
    [ -n "$GCC_A" ] || { echo "归档里找不到 libgcc.a" >&2; exit 1; }
    echo "      （buildroot 布局：$GCC_A）"
    tar -xzf "$LINUX_ARCHIVE" -C "$WORK" "$GCC_A"
    mv "$WORK/$GCC_A" "$WORK/toolchain/lib/libgcc.a"
fi
[ -s "$WORK/toolchain/lib/libgcc.a" ] || { echo "归档里没有 libgcc.a" >&2; exit 1; }

echo "==> [3/5] 放入 Windows LLVM（clang.exe / lld.exe / libclang-cpp.dll / libLLVM.dll）"
mkdir -p "$WORK/toolchain/bin"
# -rL 而不是 -aL：LLVM 的 bin 里 ld.lld/lld/lld-link 是**同一 inode 的多个名字**
# （硬链接），-a 会把这种关系保留下来，归档时就成了硬链接条目 —— Windows 上同样建不了。
# 保留 mode 是为了不丢可执行位。
cp -rL --preserve=mode,timestamps "$LLVM_DIR/bin/." "$WORK/toolchain/bin/"
# clang 的内建头（stdatomic.h 等）在 lib/clang/<ver>/include；C99 代码也可能用到
if [ -d "$LLVM_DIR/lib/clang" ]; then
    mkdir -p "$WORK/toolchain/lib"
    cp -a "$LLVM_DIR/lib/clang" "$WORK/toolchain/lib/"
fi
[ -f "$WORK/toolchain/bin/clang.exe" ] || [ -f "$WORK/toolchain/bin/clang" ] \
    || { echo "LLVM 目录里没有 clang(.exe)" >&2; exit 1; }
# lld 在 Windows 发行版里叫 lld.exe / ld.lld.exe，两种都认
LD_NAME="ld.lld"
[ -f "$WORK/toolchain/bin/ld.lld.exe" ] || LD_NAME="lld"

echo "==> [4/5] 写 toolchain.json（cc/ld 名 + clang 必需的 flag）"
cat > "$WORK/toolchain/toolchain.json" <<EOF
{
  "kind": "clang",
  "cc": "clang",
  "target": "armv5te-linux-gnueabi",
  "floatAbi": "soft",
  "sysroot": "sysroot",
  "ld": "$LD_NAME",
  "libDir": "lib",
  "libName": "gcc",
  "noStdlib": true
}
EOF
cat > "$WORK/sdk.json" <<EOF
{ "version": "$VERSION", "builtFrom": "$(basename "$LINUX_ARCHIVE") + $(basename "$LLVM_DIR")" }
EOF

echo "==> [5/5] 归档 + 清单"
mkdir -p "$OUT_DIR"
ARCHIVE="$OUT_DIR/$NAME.tar.gz"
rm -f "$ARCHIVE"
tar -C "$WORK" -czf "$ARCHIVE" runtime toolchain sdk.json

# 自检：归档里不能有符号链接/硬链接。Windows 上 MSYS 的 tar 两样都建不了
# （会整片报错退出），而这个脚本自身也要能在 Windows 上反复重跑，所以这里就兜住。
LINKS="$(tar -tvzf "$ARCHIVE" | awk '$1 ~ /^[lh]/' | wc -l)"
if [ "$LINKS" != "0" ]; then
    echo "!! 归档里仍有 $LINKS 个链接/硬链接条目（Windows 上解不开）：" >&2
    tar -tvzf "$ARCHIVE" | awk '$1 ~ /^[lh]/' | head -10 >&2
    exit 1
fi

SHA="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
SIZE="$(stat -c %s "$ARCHIVE")"
cat > "$OUT_DIR/$NAME.manifest.json" <<EOF
{
  "schemaVersion": 1,
  "version": "$VERSION",
  "url": "",
  "sha256": "$SHA",
  "sizeBytes": $SIZE
}
EOF

echo
echo "完成:"
echo "  归档  $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"
echo "  清单  $OUT_DIR/$NAME.manifest.json"
echo "  sha256 $SHA"
echo
echo "下一步: 把这两个文件复制到编辑器仓库根的 sdk/，重新 build 编辑器（csproj 会自动带上）。"
echo "        Windows 上装：.NET 10 SDK、CMake、Git、SDL2（vcpkg 或 MSYS2）、LLVM（本脚本已用）。"
