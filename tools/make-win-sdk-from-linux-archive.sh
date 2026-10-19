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
# runtime/ 整个要（契约头 + LVGL 源树）；toolchain/ 只要 sysroot 与 libgcc.a
tar -xzf "$LINUX_ARCHIVE" -C "$WORK" runtime
mkdir -p "$WORK/toolchain"

# sysroot 在两种归档里位置不同：clang 版是 toolchain/sysroot，
# gcc 版是 toolchain/<target>/sysroot（buildroot 内嵌布局）——都要认。
if tar -tzf "$LINUX_ARCHIVE" | grep -q '^toolchain/sysroot/'; then
    tar -xzf "$LINUX_ARCHIVE" -C "$WORK" toolchain/sysroot
else
    SYS_SUBDIR="$(tar -tzf "$LINUX_ARCHIVE" | grep -oE '^toolchain/[^/]+/sysroot' | sort -u | head -1)"
    [ -n "$SYS_SUBDIR" ] || { echo "归档里找不到 sysroot（toolchain/sysroot 或 toolchain/<target>/sysroot）" >&2; exit 1; }
    echo "      （gcc 版布局：$SYS_SUBDIR）"
    tar -xzf "$LINUX_ARCHIVE" -C "$WORK" "$SYS_SUBDIR"
    mv "$WORK/$SYS_SUBDIR" "$WORK/toolchain/sysroot"
fi
[ -d "$WORK/toolchain/sysroot/usr/include" ] \
    || { echo "归档里没有 toolchain/sysroot/usr/include" >&2; exit 1; }

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
cp -a "$LLVM_DIR/bin/." "$WORK/toolchain/bin/"
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
