#!/bin/sh
# golden.sh — 在宿主把 spec/golden 下的工程跑一遍，按预期日志判定通过/失败。
#
# 用法：tools/golden.sh            （先 make HOST=1）
#       tools/golden.sh g02        （只跑名字含 g02 的用例）
#
# 每个用例：无显示（SDL dummy）跑一页、注入点击序列、截图落 logs/golden/，
# 然后按日志里的锚点行判定。锚点是给人看的行为描述（页面打开、栈深、变量值），
# 不是内部实现细节——改实现但只要行为不变，用例仍然有效。

set -u

HERE=$(cd "$(dirname "$0")" && pwd)
RT=$(dirname "$HERE")
ROOT=$(cd "$RT/../.." && pwd)
BIN="$RT/chenge-runtime"
GOLDEN="$ROOT/spec/golden"
DEMO="$ROOT/spec/demo"
OUT="$ROOT/logs/golden"
FILTER="${1:-}"

# 契约漂移先挡一道：生成物（37 属性表、widgets_schema.h）必须与 spec/widget-schema.json 一致。
# 手改生成物或改了 schema 忘了重新生成，都在这里失败——而不是等到某个工程的属性名对不上。
if [ -x "$(command -v python3)" ]; then
    python3 "$HERE/gen_schema.py" --check || {
        echo "!! 契约生成物与 schema 不一致，先跑 tools/gen_schema.py"
        exit 1
    }
    python3 "$HERE/gen_gbk_table.py" --check || {
        echo "!! GBK 表与生成脚本不一致，先跑 tools/gen_gbk_table.py"
        exit 1
    }
    python3 "$HERE/check_caps.py" || {
        echo "!! 能力表与 spec/features.json 不一致（46 §1）"
        exit 1
    }
    python3 "$HERE/check_perm.py" || {
        echo "!! 命名权限位与 spec/perms.json 不一致（46 §8.2）"
        exit 1
    }
    # 缺省值：契约 default 必须等于运行时「键缺失时用的值」（docs/chenge-rt/50 §7）。
    # 这类差异不报错、不整页拒绝，只会让"手写工程"与"编辑器导出的工程"长成两个样。
    python3 "$HERE/check_defaults.py" || {
        echo "!! 缺省值与契约不一致：改运行时回落值，或改 spec 的 default 再 gen_schema.py"
        exit 1
    }
fi

if [ ! -x "$BIN" ]; then
    echo "!! 找不到 $BIN，先执行：cd chenge-hmi/chenge-runtime && make HOST=1"
    exit 1
fi

# 能力表：构建时装进镜像的那份（tools/gen_caps.py 生成）必须与二进制自己报的
# 逐字节一致，否则镜像里的 /etc/hmi/caps.json 会跟固件说两套话。
if [ -x "$(command -v python3)" ]; then
    _caps_bin=$("$BIN" --caps)
    _caps_gen=$(python3 "$HERE/gen_caps.py")
    if [ "$_caps_bin" != "$_caps_gen" ]; then
        echo "!! 能力表不一致：gen_caps.py 与 chenge-runtime --caps 输出不同"
        echo "   bin: $_caps_bin"
        echo "   gen: $_caps_gen"
        exit 1
    fi
fi

mkdir -p "$OUT"
pass=0
fail=0

# ── 控件外观判据（像素级，见 tools/appearance_probe.py）──
# 判的是"面板上能配的外观，真机位图里必须生效"：
#   matrix：每类控件的 bg_color / border_color / border_width / radius / opacity 等，
#           两个取值渲染出来的像素必须不同（全等 = 这个属性根本没进渲染）。
#   multi ：同一页同一类的多个实例各给一个颜色，各自的矩形里必须取到自己的颜色
#           （按类型共用一个样式、或只认第一个实例的实现会在这里红）。
#   edge  ：滑块/进度条的填充段（INDICATOR part）跟着圆角走 —— 只刷 MAIN 的实现
#           在面板上改圆角、看到的"条"不动。
#   figures：折线点集 / 弧的起止角 / 弧的进度段（bind.value）—— 前两条证明几何参数
#           真的进了渲染，最后一条同时守住"没绑 value 的图元一个像素都不变"。
#   binds  ：checkbox.bind.text 与 combobox.bind.selected —— 契约允许、运行时曾经没有
#           分支的两条（改变量值，位图必须跟着变）。
# 这几条用同一个宿主产物（chenge-runtime + HMI_SHOT），跑一次十来秒。
# 带 FILTER 时只跑名字含 appearance 的（迭代单个用例时不额外等）。
if [ -z "$FILTER" ] || [ "${FILTER#appearance}" != "$FILTER" ]; then
    for _probe in matrix multi edge lamp gauge figures binds; do
        printf '%-22s ' "appearance-$_probe"
        if python3 "$HERE/appearance_probe.py" "--$_probe" >"$OUT/appearance-$_probe.txt" 2>&1; then
            echo ok
            pass=$((pass + 1))
        else
            echo "FAIL（差异见 $OUT/appearance-$_probe.txt）"
            fail=$((fail + 1))
        fi
    done
fi

# ── .cgpkg 解包判据（37 §13，见 tools/pkg_selftest.sh）──
# 包由 make_cgpkg.py 独立生成（第二套实现），喂给 chenge-common 的解包代码：
# 好包必须解出与源目录逐字节相同的树，六个坏包必须被拒且不留半截目录。
if [ -z "$FILTER" ] || [ "${FILTER#pkg}" != "$FILTER" ]; then
    printf '%-22s ' "pkg-unpack"
    if sh "$HERE/pkg_selftest.sh" >"$OUT/pkg-selftest.txt" 2>&1; then
        echo ok
        pass=$((pass + 1))
    else
        echo "FAIL（差异见 $OUT/pkg-selftest.txt）"
        fail=$((fail + 1))
    fi
fi

# ── 管道走向 / 直线端点判据（像素级，见 tools/pipe_path_probe.py）──
# 判"按 points 画出来的形状"：弯管/多段管的中心线全程有管道（拐点不缺）、
# 斜直管走对角线且框的空角仍是页底、直管管径填满框高、直线带 points 时是真线段。
# 换成"按 direction 填一条直带"或"整框填成矩形"的旧实现，这一项就红。
if [ -z "$FILTER" ] || [ "${FILTER#pipe-path}" != "$FILTER" ]; then
    printf '%-22s ' "pipe-path"
    if python3 "$HERE/pipe_path_probe.py" >"$OUT/pipe-path.txt" 2>&1; then
        echo ok
        pass=$((pass + 1))
    else
        echo "FAIL（差异见 $OUT/pipe-path.txt）"
        fail=$((fail + 1))
    fi
fi

# ── 缺省值判据（像素级，见 tools/defaults_probe.py）──
# "不写这个键"与"写出契约的 default"必须画出同一张图；同时把值换成一个明显不同的
# 值时必须画得不一样（否则说明这条判据测不到东西）。文字色/字号/读数小数位这些缺省
# 落在共用辅助函数里，静态的 check_defaults.py 看不见，只能看位图。
if [ -z "$FILTER" ] || [ "${FILTER#defaults}" != "$FILTER" ]; then
    printf '%-22s ' "defaults"
    if python3 "$HERE/defaults_probe.py" >"$OUT/defaults.txt" 2>&1; then
        echo ok
        pass=$((pass + 1))
    else
        echo "FAIL（差异见 $OUT/defaults.txt）"
        fail=$((fail + 1))
    fi
fi

# 数据卷指到 $OUT/data：语言/配方副本/趋势这些设备状态不再落到宿主 /data/chenge，
# 也不会把上一次用例的状态带进下一次（语言是设备状态，跑完 g17 切过 en 会污染后面所有
# 文案断言——这是实测踩过的）
DATA_ROOT="$OUT/data"
rm -rf "$DATA_ROOT"
mkdir -p "$DATA_ROOT"

# run_case <用例名> <工程目录> <点击序列> <期望日志片段...>
run_case() {
    name=$1
    proj=$2
    clicks=$3
    shift 3

    if [ -n "$FILTER" ]; then
        case "$name" in
        *"$FILTER"*) ;;
        *) return 0 ;;
        esac
    fi

    log="$OUT/$name.log"
    shot="$OUT/$name.bmp"
    rm -f "$shot"
    # 审计落点指到本用例自己的目录：不往真实数据卷 / 宿主 cwd 里写测试条目
    rm -rf "$OUT/$name"
    mkdir -p "$OUT/$name"

    printf '%-22s ' "$name"
    # 用例要自带事件/趋势文件时前置 CASE_DATA_DIR，其余走本次运行共用的 $DATA_ROOT
    case_dir="${CASE_DATA_DIR:-$DATA_ROOT}"
    rm -f "$case_dir/state/language"      # 每个用例都从工程默认语言起跑
    SDL_VIDEODRIVER=dummy HMI_PROJECT_DIR="$proj" HMI_HOST_CLICK="$clicks" \
        HMI_RT_TRACE=1 HMI_SHOT="$shot" HMI_DATA_DIR="$case_dir" \
        HMI_AUDIT_DIR="$OUT/$name" HMI_USB_DIR="${CASE_USB_DIR:-}" \
        timeout 30 "$BIN" >"$log" 2>&1

    ok=1
    for pat in "$@"; do
        if ! grep -q -- "$pat" "$log"; then
            echo "FAIL（缺：$pat，日志 $log）"
            ok=0
            break
        fi
    done
    # EXPECT_NO_SHOT=1：期望失败的用例（建页被拒后进程直接退出，不产截图）
    if [ "$ok" = 1 ] && [ -z "${EXPECT_NO_SHOT:-}" ] && [ ! -s "$shot" ]; then
        echo "FAIL（没出截图）"
        ok=0
    fi
    if [ "$ok" = 1 ]; then
        echo "ok"
        pass=$((pass + 1))
    else
        fail=$((fail + 1))
    fi
}

# csv_has <说明> <模式> <文件...>：导出的 CSV 里必须有这一行/片段
csv_has() {
    label=$1
    pat=$2
    shift 2

    if [ -n "$FILTER" ]; then
        case "$label" in
        *"$FILTER"*) ;;
        *) return 0 ;;
        esac
    fi

    printf '%-22s ' "$label"
    if grep -qs -- "$pat" "$@"; then
        echo ok
        pass=$((pass + 1))
    else
        echo "FAIL（CSV 里没有 $pat）"
        fail=$((fail + 1))
    fi
}

# csv_rows <说明> <期望行数> <文件...>（行数含表头；最后一个参数是 lower/upper 也可省）
csv_rows() {
    label=$1
    want=$2
    shift 2

    if [ -n "$FILTER" ]; then
        case "$label" in
        *"$FILTER"*) ;;
        *) return 0 ;;
        esac
    fi

    printf '%-22s ' "$label"
    n=$(cat "$@" 2>/dev/null | grep -c . || true)
    if [ "$n" = "$want" ]; then
        echo ok
        pass=$((pass + 1))
    else
        echo "FAIL（CSV 行数 $n，期望 $want）"
        fail=$((fail + 1))
    fi
}

# csv_min_rows <说明> <最少行数> <文件...>
csv_min_rows() {
    label=$1
    want=$2
    shift 2

    if [ -n "$FILTER" ]; then
        case "$label" in
        *"$FILTER"*) ;;
        *) return 0 ;;
        esac
    fi

    printf '%-22s ' "$label"
    n=$(cat "$@" 2>/dev/null | grep -c . || true)
    if [ "$n" -ge "$want" ] 2>/dev/null; then
        echo ok
        pass=$((pass + 1))
    else
        echo "FAIL（CSV 行数 $n，至少要有 $want）"
        fail=$((fail + 1))
    fi
}

# audit_case <用例名> <审计文件里必须出现的片段...>
# 审计的价值在"落盘那一条"，串口日志只证明它被打印过；两处分开判。
audit_case() {
    name=$1
    shift

    if [ -n "$FILTER" ]; then
        case "$name" in
        *"$FILTER"*) ;;
        *) return 0 ;;
        esac
    fi

    printf '%-22s ' "$name/audit"
    for pat in "$@"; do
        if ! grep -q -- "$pat" "$OUT/$name/audit.log" 2>/dev/null; then
            echo "FAIL（审计缺：$pat，文件 $OUT/$name/audit.log）"
            fail=$((fail + 1))
            return 0
        fi
    done
    echo ok
    pass=$((pass + 1))
}

# g01：单页 + 按钮写变量（sim）
run_case g01-write "$GOLDEN/g01_minimal" "100,148,1200" \
    "page: home 打开" "启动=1"

# g05：P1.1 新增控件——勾选框点一下写回变量（灯随之亮），spinner 与三种阀门建得起来
run_case g05-p1-widgets "$GOLDEN/g05_p1_widgets" "100,90,1200" \
    "page: home 打开" "启动=1"

# g06：P1.2 tabview + 常驻键盘——点第一页签里的按钮写变量，键盘按 target 挂上
run_case g06-p12-widgets "$GOLDEN/g06_p12_widgets" "100,120,1200" \
    "page: home 打开" "设定=42.0"

# g07：P1.3 旋钮——从最小端（左下）拖到最大端（右下），变量应写成 100。
# 注意 LVGL 的 arc 是"相对增量"式拖动，单击只走一小段，不能当"点哪到哪"用。
run_case g07-p13-knob "$GOLDEN/g07_p13_knob" "120,240>224,224,600;700,430,1500" \
    "page: home 打开" "设定=100.0" "设定 100.0 ℃"

# g08：P1.4 属性——先按住 1200ms 看提示（这次 click 被吞），再短按一次写入变量
run_case g08-p14-props "$GOLDEN/g08_p14_props" "130,330>130,330,1200,900;130,330,2000" \
    "page: home 打开" "tooltip: 本次按下已弹提示，忽略 click" "设定=9.0"

# g09：P1.5 页面背景图——stretch 铺满，控件仍在上层且可点
run_case g09-p15-bgimage "$GOLDEN/g09_p15_bgimage" "120,108,1200" \
    "page: home 打开" "启动=1"

# g09：同一张图改成 tile（平铺），验证 mode 三个取值里最容易出错的一个
tmp_bgtile="$OUT/tmp_bg_tile"
rm -rf "$tmp_bgtile"
cp -r "$GOLDEN/g09_p15_bgimage" "$tmp_bgtile"
python3 - "$tmp_bgtile/pages/home.json" <<'PYEOF'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
d["bg_image"]["mode"] = "tile"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PYEOF
run_case g09-p15-bgimage-tile "$tmp_bgtile" "760,440,1200" \
    "page: home 打开"

# g10：P1.6 旋转与镜像——图片 30°/镜像、图元 45°、文本 90° 同屏
run_case g10-p16-rotate "$GOLDEN/g10_p16_rotate" "760,440,1200" \
    "page: home 打开"

# g10：文本类给 45° 必须被拒（任意角只对图片与图元开放，ADR-0019）
tmp_rot="$OUT/tmp_rot_bad"
rm -rf "$tmp_rot"
cp -r "$GOLDEN/g10_p16_rotate" "$tmp_rot"
python3 - "$tmp_rot/pages/home.json" <<'PYEOF'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
for w in d["widgets"]:
    if w["id"] == "l1":
        w["props"]["rotation"] = 45
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PYEOF
EXPECT_NO_SHOT=1 run_case g10-p16-rotate-reject "$tmp_rot" "760,440,1200" \
    "文本类只能 0/90/180/270"
EXPECT_NO_SHOT=

# g14：P3.1 报警深度——偏差(紧急)与变化率(严重)启动即触发，点搁置后状态列显示"已搁置"
run_case g14-p31-alarm "$GOLDEN/g14_p31_alarm" "130,346,1600;350,346,900;760,460,900" \
    "alarm: 载入 4 条规则" "alarm: 规则 0 搁置 30 分钟" "alarm: 规则 0 取消搁置"

# g13：P2 属性动画——load 生长、click 位移、表达式触发变色与淡出
run_case g13-p22-anim "$GOLDEN/g13_p22_anim" "110,236,1800;760,440,1500" \
    "anim: 载入 4 条动画" "anim: #3 启动" "anim: #0 启动" "anim: #2 启动" \
    "anim: #0 结束（保持终值）颜色=f44336"

# g12：P2 系统变量——时间/日期/星期/当前页/语言/报警数都靠绑定显示
run_case g12-p21-sysvar "$GOLDEN/g12_p21_sysvar" "760,440,1500" \
    "page: home 打开"

# g12：写系统变量在建页期就被拒（只读），不留到运行期
tmp_ro="$OUT/tmp_sysvar_ro"
rm -rf "$tmp_ro"
cp -r "$GOLDEN/g12_p21_sysvar" "$tmp_ro"
sed -i 's/"start_page": "home"/"start_page": "ro_test"/' "$tmp_ro/manifest.json"
EXPECT_NO_SHOT=1 run_case g12-p21-sysvar-ro "$tmp_ro" "760,440,1200" \
    "是系统变量（只读），不能作为写入目标"
EXPECT_NO_SHOT=''

# g02：P1 新增控件展示页——首页右上角入口 → ui_ext（含图层页头），勾选框写变量
run_case g02-ui-ext "$GOLDEN/g02_basic_widgets" "690,350,900;100,104,900" \
    "page: ui_ext 打开" "启动=1"

# g02：常驻键盘页（从 ui_ext 的页签按钮进不去时也能直接验）——直接把起始页改成 ui_kb
tmp_kb="$OUT/tmp_ui_kb"
rm -rf "$tmp_kb"
cp -r "$GOLDEN/g02_basic_widgets" "$tmp_kb"
sed -i 's/"start_page": "home"/"start_page": "ui_kb"/' "$tmp_kb/manifest.json"
run_case g02-ui-kb "$tmp_kb" "760,440,1200" \
    "page: ui_kb 打开"

# g11：P1.7 页面图层——本页引用 common_header，点图层里的按钮应能写变量
run_case g11-p17-layers "$GOLDEN/g11_p17_layers" "700,32,1200" \
    "page: home 打开" "启动=1"

# g11：图层页带脚本必须被拒（图层是纯版式，挂脚本会出现"进一次页跑三次脚本"）
tmp_lay="$OUT/tmp_layer_script"
rm -rf "$tmp_lay"
cp -r "$GOLDEN/g11_p17_layers" "$tmp_lay"
python3 - "$tmp_lay/pages/common_header.json" <<'PYEOF'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
d["on_open"] = "cg.log('layer script')"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PYEOF
EXPECT_NO_SHOT=1 run_case g11-p17-layers-script "$tmp_lay" "700,32,1200" \
    "不得带脚本"
EXPECT_NO_SHOT=''

# g11：图层不支持嵌套
tmp_lay2="$OUT/tmp_layer_nest"
rm -rf "$tmp_lay2"
cp -r "$GOLDEN/g11_p17_layers" "$tmp_lay2"
python3 - "$tmp_lay2/pages/common_header.json" <<'PYEOF'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
d["layers"] = ["home"]
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PYEOF
EXPECT_NO_SHOT=1 run_case g11-p17-layers-nest "$tmp_lay2" "700,32,1200" \
    "不支持嵌套"
EXPECT_NO_SHOT=''

# g02：首页 → 监控（页头返回），验证多页与页面栈
run_case g02-nav "$GOLDEN/g02_basic_widgets" "107,188,900;75,25,700" \
    "page: home 打开" "page: monitor 打开" "栈深 2" "栈深 1"

# g02：通讯报警（type=comm）——夹具加一台打不开的 modbus 设备，断线即触发。
tmp_comm="$OUT/tmp_comm_alarm"
rm -rf "$tmp_comm"
cp -r "$GOLDEN/g02_basic_widgets" "$tmp_comm"
mkdir -p "$tmp_comm/data"
python3 - "$tmp_comm" <<'PYEOF'
import json, sys
d = sys.argv[1]
dev = json.load(open(d + "/devices.json", encoding="utf-8"))
dev.append({"id": "plc1", "name": "PLC", "protocol": "modbus_rtu", "port": "com1",
            "slave": 1, "poll_ms": 500, "timeout_ms": 100})
json.dump(dev, open(d + "/devices.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
ports = json.load(open(d + "/ports.json", encoding="utf-8"))
ports.append({"id": "com1", "baud": 9600, "data_bits": 8, "stop_bits": 1, "parity": "none"})
json.dump(ports, open(d + "/ports.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
al = json.load(open(d + "/alarms.json", encoding="utf-8"))
al["alarms"].append({"id": 9, "type": "comm", "device": "plc1", "level": 3, "ack": False,
                     "group": "设备", "text": "PLC 通讯中断", "text_clear": "PLC 通讯恢复"})
json.dump(al, open(d + "/alarms.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PYEOF
CASE_DATA_DIR="$tmp_comm/data"
run_case g02-comm-alarm "$tmp_comm" "477,188,900;760,20,900" \
    "alarm: 载入 5 条规则" "通讯中断（规则 9）"
CASE_DATA_DIR=""

# g02：配方带 string 成员（批号）——点"下载配方A"后，批号变量要变成 B001。
run_case g02-recipe-str "$GOLDEN/g02_basic_widgets" "292,188,900;110,389,900" \
    "page: recipe 打开" '批号="B001"'

# g02：报警条在报警恢复后显示几秒恢复文案（text_clear）。开关打开再关掉，回首页看条。
run_case g02-bar-clear "$GOLDEN/g02_basic_widgets" \
    "107,278,900;265,176,800;75,25,900;107,278,900;265,176,800;75,25,900" \
    "bar: 显示恢复文案「设备已停机」"

# g02：存盘组总配额——各组 max_mb 合计 12 超过 total_max_mb=3，按比例压到 1。
tmp_quota="$OUT/tmp_savecfg_quota"
rm -rf "$tmp_quota"
cp -r "$GOLDEN/g02_basic_widgets" "$tmp_quota"
mkdir -p "$tmp_quota/data/trends"
python3 - "$tmp_quota/savecfg.json" <<'PYEOF'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
d["total_max_mb"] = 3
for g in d["groups"]:
    g["max_mb"] = 4
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PYEOF
CASE_DATA_DIR="$tmp_quota/data"
run_case g02-savecfg-quota "$tmp_quota" "662,188,900;760,20,900" \
    "按总配额压到"
CASE_DATA_DIR=""

# g02：文本占位符 {tag:...} 要跟着变量走（监控页那行"炉温：xx.x℃"）。
# 多停 2.5s 让占位符至少刷两次：第二次刷新的文字里必须已经是数值，而不是原样的 {tag:0|%.1f}。
run_case g02-monitor-live "$GOLDEN/g02_basic_widgets" "107,188,900;700,20,2500" \
    "page: monitor 打开" "占位符刷新 #2：炉温：2"

# g02：首页 → 配方（配方表读 recipes/烘烤参数.json）
run_case g02-recipe "$GOLDEN/g02_basic_widgets" "292,188,900;760,20,1200" \
    "page: home 打开" "page: recipe 打开"

# g02：首页 → 报警（报警条 + 活动列表 + 历史列表）
run_case g02-alarm "$GOLDEN/g02_basic_widgets" "477,188,900;760,20,1500" \
    "page: home 打开" "page: alarm 打开"

# g02：首页 → 历史（历史曲线 + 历史表 + 立即存盘）
run_case g02-history "$GOLDEN/g02_basic_widgets" "662,188,900;760,20,1500" \
    "page: home 打开" "page: history 打开"

# g02：配方 apply_on_start=true 时开机把当前配方下发到成员变量。
# 夹具造一份设备副本（current=产品B，设定 30），启动后的变量跟踪里设定必须是 30.0。
tmp_rcp="$OUT/tmp_recipe_start"
rm -rf "$tmp_rcp"
cp -r "$GOLDEN/g02_basic_widgets" "$tmp_rcp"
mkdir -p "$tmp_rcp/data/recipes"
python3 - "$tmp_rcp/data/recipes" <<'PYEOF'
import json, sys, os
def fnv(s):
    h = 2166136261
    for b in s.encode():
        h = ((h ^ b) * 16777619) & 0xffffffff
    return h
d = sys.argv[1]
group = "烘烤参数"
dev = {"group": group, "members": [2, 1, 3],
       "recipes": [{"name": "产品A", "desc": "标准 60 秒", "items": [60, 1, "B001"]},
                   {"name": "产品B", "desc": "低温 30 秒", "items": [30, 1, "B002"]},
                   {"name": "产品C", "desc": "高温 90 秒", "items": [90, 0, "B003"]}],
       "current": "产品B"}
with open(os.path.join(d, "rcp_%08x.json" % fnv(group)), "w", encoding="utf-8") as f:
    json.dump(dev, f, ensure_ascii=False)
PYEOF
CASE_DATA_DIR="$tmp_rcp/data"
run_case g02-recipe-apply "$tmp_rcp" "292,188,900;760,20,1500" \
    "page: recipe 打开" "开机下发当前配方 烘烤参数/产品B" "设定=30.0"
CASE_DATA_DIR=""

# g02：报警历史（组名留空 = 全部组）——预置两个组的事件文件，页面要把两组归并读出两条。
# 事件文件按组名哈希命名，这里用同样的 FNV-1a 造文件，不依赖本机跑过多久。
tmp_hist="$OUT/tmp_alarm_hist"
rm -rf "$tmp_hist"
cp -r "$GOLDEN/g02_basic_widgets" "$tmp_hist"
mkdir -p "$tmp_hist/data/alarms"
python3 - "$tmp_hist/data/alarms" <<'PYEOF'
import struct, sys, time
def fnv(s):
    h = 2166136261
    for b in s.encode():
        h = ((h ^ b) * 16777619) & 0xffffffff
    return h
d, now = sys.argv[1], int(time.time())
# 头 32 字节：magic "ALM1" | ver u16 @4 | rec_size u16 @6 | max_rec @8 | head @12 | count @16 | hash @20
def write(group, events):
    hdr = bytearray(32)
    hdr[0:4] = b"ALM1"
    struct.pack_into("<H", hdr, 4, 1)
    struct.pack_into("<H", hdr, 6, 12)
    struct.pack_into("<I", hdr, 8, 5000)
    struct.pack_into("<I", hdr, 12, len(events) % 5000)
    struct.pack_into("<I", hdr, 16, len(events))
    struct.pack_into("<I", hdr, 20, fnv(group))
    body = b"".join(struct.pack("<IHBBf", ts, rid, ev, lvl, val) for ts, rid, ev, lvl, val in events)
    open("%s/alm_%08x.bin" % (d, fnv(group)), "wb").write(bytes(hdr) + body)
write("炉温", [(now - 90, 1, 0, 2, 61.5), (now - 60, 1, 1, 2, 55.0)])
write("设备", [(now - 30, 3, 0, 1, 1.0)])
PYEOF
# 事件目录设成只读：引擎启动时还会按当前值触发新报警，这里只验证"读到已有事件"，
# 不让它往夹具里再写，条数才是确定的（3 条）。
chmod 0444 "$tmp_hist/data/alarms"/*.bin
chmod 0555 "$tmp_hist/data/alarms"
CASE_DATA_DIR="$tmp_hist/data"
run_case g02-alarm-history "$tmp_hist" "477,188,900;760,20,1500" \
    "page: alarm 打开" "历史事件 全部组：3 条"
CASE_DATA_DIR=""
chmod 0755 "$tmp_hist/data/alarms"   # 恢复可写，下次运行才能 rm -rf 夹具

# g02：首页 → 基础控件（label/value/lamp/switch/bar/gauge/slider/input/combobox …）
run_case g02-ui "$GOLDEN/g02_basic_widgets" "107,278,900" \
    "page: home 打开" "page: ui_basic 打开" "栈深 2"

# g02：提示条同时只留一条——连点两次弹提示的按钮（间隔 < 2 秒），第二条要把第一条收掉，
# 否则旧条没定时器管，会永远留在屏上。
# 用系统页的权限演示按钮（show_message，与任何变量状态无关），连点两次。
run_case g02-toast-replace "$GOLDEN/g02_basic_widgets" "662,278,900;205,285,600;205,285,600" \
    "page: system 打开" "toast: 新提示顶掉未消失的旧提示"

# g02：首页 → 图元（37 种图元全部建得起来）
run_case g02-graph "$GOLDEN/g02_basic_widgets" "292,278,900" \
    "page: home 打开" "page: ui_graph 打开" "栈深 2"

# g02：首页 → 图表与动画（柱/饼/环 + XY），跑两秒让采样定时器出点；
# 页面顶部那个 message 消息栏 auto_hide_ms=1200，到点应自动清空。
run_case g02-chart "$GOLDEN/g02_basic_widgets" "477,278,900;760,20,2000" \
    "page: home 打开" "page: ui_chart 打开" "message: 1200ms 到点，已清空"

# g02：首页 → 系统（登录/语言/存盘/权限演示 @id 文本）
run_case g02-system "$GOLDEN/g02_basic_widgets" "662,278,900;760,20,1200" \
    "page: home 打开" "page: system 打开"

# g02：ack:false 的规则（设备/启动，bool）——点基础控件页的开关让它触发，再回首页进报警页
# 点"全部确认"：这条不能被确认（不进未确认、不写确认事件），日志里会点明它不需要确认。
run_case g02-alarm-noack "$GOLDEN/g02_basic_widgets" "107,278,900;265,176,800;75,25,900;477,188,900;700,25,900" \
    "ack:false，不需要确认" "page: alarm 打开"

# g02：二次确认——点按钮先弹框（动作不执行），点"确定"才把设定下发成 44
run_case g02-confirm "$GOLDEN/g02_basic_widgets" "662,278,900;320,159,900;300,285,900" \
    "page: system 打开" "confirm: 已弹确认框" "设定=44.0"

# g02：长按——按住 1200ms（阈值 900）触发 long_press 动作（设定=55），且松开时的 click 被吞掉
run_case g02-longpress "$GOLDEN/g02_basic_widgets" "662,278,900;520,159>520,159,1200,900" \
    "page: system 打开" "长按 900ms 触发" "设定=55.0"

# g02：文本输入（input mode=text，绑 string 变量）——点开文本键盘、清空、打 AB、确定，
# 值要写进变量并下发到设备（trace 里能看到 批号="AB"）。
run_case g02-text-input "$GOLDEN/g02_basic_widgets" \
    "662,278,900;700,159,700;142,442,300;90,354,300;362,398,300;658,442,900" \
    "keypad: 文本键盘" 'input: 批号 已写入 "AB"' '批号="AB"'

# g02：从 detail 起页按返回，应被栈底保护挡住
tmp="$OUT/tmp_monitor"
rm -rf "$tmp"
cp -r "$GOLDEN/g02_basic_widgets" "$tmp"
sed -i 's/"start_page": "home"/"start_page": "monitor"/' "$tmp/manifest.json"
run_case g02-stack-guard "$tmp" "75,25,900" \
    "page: monitor 打开" "已在栈底"

# g02：坏图必须被 CRC 拦下，且错误文案要点名文件与原因
tmp2="$OUT/tmp_badimg"
rm -rf "$tmp2"
cp -r "$GOLDEN/g02_basic_widgets" "$tmp2"
python3 - "$tmp2/assets/img/logo.bin" <<'PYEOF'
import sys
p = sys.argv[1]
b = bytearray(open(p, 'rb').read())
b[100] ^= 0xFF
open(p, 'wb').write(b)
PYEOF
run_case g02-bad-image "$tmp2" "107,278,1200" "CRC32 校验失败" "退回 home"

# g15：P3.2 命名权限位 + 审计——默认用户 op（组 operator：view/operate/ack_alarm）。
# 依次点：启动=1（写变量）、确认报警（有 ack_alarm）、搁置（无 shelve）、
# 导出配方（无 export）、直接写设备（无 debug）。前两条要成功并留审计，后三条要拒。
run_case g15-p32-perm "$GOLDEN/g15_p32_perm" "130,90,800;370,90,600;640,90,400;130,170,400;370,170,400" \
    "auth: 组 operator 命名位 view,operate,ack_alarm" \
    "失败 2 次锁 1 分钟" \
    "（当前用户 op）" \
    "auth: 控件 ht1 需要 view_history 权限，已隐藏" \
    "拒绝动作 shelve_alarm（用户 op，缺少 shelve 权限）" \
    "拒绝动作 export_recipes（用户 op，缺少 export 权限）" \
    "拒绝动作 write_device（用户 op，缺少 debug 权限）" \
    "AUDIT"
audit_case g15-p32-perm "login-ok user=op" "alarm-ack user=op" "tag-write user=op" \
    "denied user=op" "groups=operator"

# g15：只读组（perms: []）连 operate 都没有——启动按钮点了要拒，且写变量不落审计。
tmp_g15ro="$OUT/tmp_p32_readonly"
rm -rf "$tmp_g15ro"
cp -r "$GOLDEN/g15_p32_perm" "$tmp_g15ro"
sed -i 's/"default_user": "op"/"default_user": "guest"/' "$tmp_g15ro/manifest.json"
run_case g15-p32-readonly "$tmp_g15ro" "130,90,600" \
    "auth: 组 viewer 命名位 (无)" \
    "拒绝动作 write_tag（用户 guest，缺少 operate 权限）"
audit_case g15-p32-readonly "login-ok user=guest" "denied user=guest"

# g15：占位符的"空值兜底"——登录 guest 顶部变 guest，登出又回到「未登录」。
# 占位符只在文本变化时打 trace，所以这里要靠一次登录/登出制造两次变化。
tmp_g15anon="$OUT/tmp_p32_anon"
rm -rf "$tmp_g15anon"
cp -r "$GOLDEN/g15_p32_perm" "$tmp_g15anon"
sed -i '/"default_user"/d' "$tmp_g15anon/manifest.json"
run_case g15-p32-anon "$tmp_g15anon" "370,250,1200;400,184,900;130,250,1200" \
    "（当前用户 guest）" \
    "（当前用户 未登录）"
audit_case g15-p32-anon "login-ok user=guest" "logout user=guest"

# g16：P3.3 数据记录——quick 组 max_records=3 + full_policy=stop（写满就停），
# grow 组缺省 overwrite（继续环形覆盖）；两组都要能导成 CSV（分隔符/时间格式按组配置）。
tmp_usb="$OUT/tmp_p33_usb"
rm -rf "$tmp_usb" "$OUT/tmp_p33_data"
mkdir -p "$tmp_usb" "$OUT/tmp_p33_data"
CASE_USB_DIR="$tmp_usb"
CASE_DATA_DIR="$OUT/tmp_p33_data"
run_case g16-p33-datalog "$GOLDEN/g16_p33_datalog" "140,90,3600;400,90,1000;670,90,1000" \
    "savecfg: 组 quick 用 max_records=3 条" \
    "savecfg: 组 quick 已满（3 条，full_policy=stop），停止采集" \
    "savecfg: 导出组 quick → " "（3 行" \
    "savecfg: 导出组 grow → " \
    "savecfg: 导出失败：存盘组 nope 不存在"
csv_has g16-p33-quick-head '^time,炉温,计数$' "$tmp_usb"/quick_*.csv
csv_rows g16-p33-quick-rows 4 "$tmp_usb"/quick_*.csv
csv_has g16-p33-grow-head '^time|炉温$' "$tmp_usb"/grow_*.csv
csv_min_rows g16-p33-grow-rows 3 "$tmp_usb"/grow_*.csv
CASE_USB_DIR=""
CASE_DATA_DIR=""

# g17：脚本侧三个桩换成真实现（cg.registerUser / cg.LogOff / cg.setLanguage），
# 外加报警 text_id 按语言现取、write_device 的 from_tag。
# 顺序：触发→恢复（中文文案）→脚本登录→脚本切英文→再触发恢复（英文文案）→
#       设定=7 → 写设备 <- 设定（回读=7）→ 脚本登出。
run_case g17-p35-script "$GOLDEN/g17_p35_script" \
    "130,248,1200;370,248,900;130,88,900;370,88,900;130,248,900;370,248,900;130,168,900;370,168,900;610,88,900" \
    "alarm: 载入 1 条规则" \
    "bar: 显示恢复文案「设备已停机」" \
    "auth: 登录成功 op" \
    "i18n: 切到 en" \
    "bar: 显示恢复文案「Device stopped」" \
    "回读=7" \
    "auth: 登出 op"
audit_case g17-p35-script "login-ok user=op" "logout user=op"

# demo-allinone：全能验收工程（spec/demo/allinone，12 页）逐页打开。
# 它是上板验收用的同一份工程，这里跑一遍保证"每一页都能建"——页面建不出来的报错
# 在上板前就该暴露，而不是等人坐在设备前面才发现。
if [ -d "$DEMO/allinone" ]; then
    run_case demo-allinone "$DEMO/allinone" \
        "106,96,900;738,24,600;304,96,600;738,24,600;502,96,600;738,24,600;697,96,600;738,24,600;106,176,600;738,24,600;304,176,600;738,24,600;502,176,600;738,24,600;697,176,600;738,24,600;106,256,600;738,24,600;304,256,600;738,24,600;502,256,600;738,24,600" \
        "page: home 打开" \
        "page: w_basic 打开" "page: w_input 打开" "page: w_data 打开" "page: w_ext 打开" \
        "page: anim 打开" "page: alarm 打开" "page: data 打开" "page: recipe 打开" \
        "page: users 打开" "page: sys 打开" "page: about 打开" \
        "占位符初值 #1：运行时"     # about 页长文本里的两个占位符要真的展开（96 字节截断会原样显示）
fi

echo
echo "通过 $pass，失败 $fail（日志与截图在 $OUT/）"
[ "$fail" = 0 ]
