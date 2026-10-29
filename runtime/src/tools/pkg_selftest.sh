#!/bin/sh
# pkg_selftest.sh — .cgpkg 解包的宿主判据（已挂 tools/golden.sh）
#
# 三件事：
#   1. 造一份最小 37 工程（manifest + 一页 + 一个资产文件）
#   2. 用 tools/make_cgpkg.py 独立打成好包 + 五个坏包
#   3. 编 tools/pkg_selftest.c（连同 chenge-common 的解包实现）在宿主上跑
#
# 判据的意义：坏包必须被拒且不留半截目录 —— 包坏了却装进去一个"能加载一半"的工程，
# 比直接报错危险得多。
set -e

HERE=$(cd "$(dirname "$0")" && pwd)
RT=$(cd "$HERE/.." && pwd)
COMMON=$(cd "$RT/../chenge-common" && pwd)
OUT=${PKG_SELFTEST_DIR:-$RT/../../logs/tests/pkg_selftest}
CC=${CC:-cc}

rm -rf "$OUT"
mkdir -p "$OUT/proj/pages" "$OUT/proj/assets" "$OUT/bin"

cat > "$OUT/proj/manifest.json" <<'EOF'
{
  "format_version": "0.6",
  "name": "pkgtest",
  "title": "打包自测",
  "hw_profile": "f1c100s-kinseal",
  "screen": { "width": 800, "height": 480 },
  "start_page": "home"
}
EOF
cat > "$OUT/proj/pages/home.json" <<'EOF'
{ "id": "home", "title": "首页", "widgets": [] }
EOF
# 资产文件故意跨 512 字节边界，覆盖 ustar 补齐与分块读取
python3 - "$OUT/proj/assets/blob.bin" <<'PY'
import sys
with open(sys.argv[1], "wb") as f:
    f.write(bytes(range(256)) * 5)   # 1280 字节
PY

python3 "$HERE/make_cgpkg.py" "$OUT/proj" "$OUT/good.cgpkg"
for v in bad_magic bad_crc bad_path bad_hw bad_len truncated; do
    python3 "$HERE/make_cgpkg.py" "$OUT/proj" "$OUT/$v.cgpkg" --variant "$v"
done

$CC -std=gnu99 -O1 -Wall -Wextra -Werror=implicit-function-declaration \
    -I"$COMMON/include" \
    "$HERE/pkg_selftest.c" \
    "$COMMON/src/hmi_project_import.c" \
    "$COMMON/src/hmi_crc32.c" \
    "$COMMON/src/hmi_json.c" \
    -o "$OUT/bin/pkg_selftest"

"$OUT/bin/pkg_selftest" "$OUT/proj" "$OUT/good.cgpkg" \
    "$OUT/bad_magic.cgpkg" "$OUT/bad_crc.cgpkg" "$OUT/bad_path.cgpkg" \
    "$OUT/bad_hw.cgpkg" "$OUT/bad_len.cgpkg" "$OUT/truncated.cgpkg"
