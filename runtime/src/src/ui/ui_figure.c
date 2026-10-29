/*
 * ui_figure.c — 图元（37 种 kind）的顶点生成与绘制
 *
 * 契约见 spec/widget-schema.json 的 figures；对外只有 figure_build()。
 * 所有图元都是同一条路子：按控件框算出顶点 → 覆盖度抗锯齿填充 + 逐段描边；
 * 多面件（cube / 管道 / 罐形 / 平面 / 3D）共用一块顶点缓冲、逐面填充，
 * 渐变靠分块近似（不做真光照，见 docs/chenge-rt/44 §3.6）。
 */
#include "ui_figure.h"
#include "hmi_board.h"     /* HMI_LCD_WIDTH：抗锯齿行缓冲的宽度上限 */
#include "hmi_log.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

/* ── 图元（37 §6.4 的 shape） ──
 * rect / round_rect / circle 用 lv_obj 的样式即可；
 * polygon / polyline / ellipse / pie 需要自绘（LVGL 没有多边形控件），
 * 走 LV_EVENT_DRAW_MAIN 用 lv_draw_triangle / lv_draw_line 画。
 * arc 用 lv_arc 的 bg_angles。 */
enum {
    SH_RECT = 0, SH_ROUND_RECT, SH_CIRCLE, SH_ELLIPSE,
    SH_ARC, SH_PIE, SH_POLYGON, SH_POLYLINE, SH_LINE, SH_CONTAINER,
    /* 多边形族 + 星形/十字：顶点由控件框生成，与 polygon 同一条绘制路径 */
    SH_PARALLELOGRAM, SH_TRAPEZOID, SH_DIAMOND, SH_OCTAGON, SH_HEXAGON,
    SH_TRIANGLE, SH_RIGHT_TRIANGLE, SH_STAR5, SH_STAR, SH_CROSS,
    /* 复合块：注释框是单一路径，立方体是多面 */
    SH_CALLOUT, SH_CUBE, SH_WEDGE,
    /* 工艺件：管道 4 件 / 罐形 / 平面 3 件 */
    SH_PIPE_H, SH_PIPE_V, SH_PIPE_ELBOW, SH_PIPE_JOINT,
    SH_TANK, SH_PLANE_CONCAVE, SH_PLANE_CONVEX, SH_PLANE_GROOVE,
    /* 箭头 3 件 */
    SH_ARROW, SH_ARROW_THICK, SH_ARROW_THIN,
    /* 阀门 3 件 */
    SH_VALVE_BALL, SH_VALVE_BUTTERFLY, SH_VALVE_GATE,
    /* 三维 3 件 */
    SH_BALL3D, SH_TORUS3D, SH_CONE3D
};

static const struct { const char* name; int kind; } s_shape_kinds[] = {
    { "rect", SH_RECT }, { "round_rect", SH_ROUND_RECT }, { "circle", SH_CIRCLE },
    { "ellipse", SH_ELLIPSE }, { "arc", SH_ARC }, { "pie", SH_PIE },
    { "polygon", SH_POLYGON }, { "polyline", SH_POLYLINE },
    { "line", SH_LINE }, { "container", SH_CONTAINER },
    { "parallelogram", SH_PARALLELOGRAM }, { "trapezoid", SH_TRAPEZOID },
    { "diamond", SH_DIAMOND }, { "octagon", SH_OCTAGON },
    { "hexagon", SH_HEXAGON }, { "triangle", SH_TRIANGLE },
    { "right_triangle", SH_RIGHT_TRIANGLE }, { "star5", SH_STAR5 },
    { "star", SH_STAR }, { "cross", SH_CROSS },
    { "callout", SH_CALLOUT }, { "cube", SH_CUBE }, { "wedge", SH_WEDGE },
    { "pipe_h", SH_PIPE_H }, { "pipe_v", SH_PIPE_V },
    { "pipe_elbow", SH_PIPE_ELBOW }, { "pipe_joint", SH_PIPE_JOINT },
    { "tank", SH_TANK }, { "plane_concave", SH_PLANE_CONCAVE },
    { "plane_convex", SH_PLANE_CONVEX }, { "plane_groove", SH_PLANE_GROOVE },
    { "arrow", SH_ARROW }, { "arrow_thick", SH_ARROW_THICK },
    { "arrow_thin", SH_ARROW_THIN },
    { "valve_ball", SH_VALVE_BALL }, { "valve_butterfly", SH_VALVE_BUTTERFLY },
    { "valve_gate", SH_VALVE_GATE },
    { "ball3d", SH_BALL3D }, { "torus3d", SH_TORUS3D }, { "cone3d", SH_CONE3D },
};

#define SHAPE_PTS_MAX 128

/* 扫描线填充（带抗锯齿）：每行取 SHAPE_AA_SUB 个纵向子样本，横向上把像素的
 * 覆盖比例累加成 alpha，再按 alpha 分段合成。不用三角扇——相邻三角形各自抗锯齿，
 * 接缝处覆盖率之和不足 1，会透出底色。 */
#define SHAPE_AA_SUB 8

static lv_opa_t s_aa_acc[HMI_LCD_WIDTH + 2];

static void shape_aa_add(int x, int add)
{
    int v;

    if (x < 0 || x >= HMI_LCD_WIDTH) return;
    v = (int)s_aa_acc[x] + add;
    s_aa_acc[x] = (lv_opa_t)(v > 255 ? 255 : v);
}

void fig_fill_poly(lv_layer_t* layer, int ox, int oy,
                            const lv_point_precise_t* pts, int n,
                            lv_color_t color, lv_opa_t opa)
{
    /* 边表：行范围用整数预筛，只有真正跨过该行的边才做浮点求交
     * （ARM9 没有 FPU，每行都拿 n 条边做浮点比较会拖慢整页重绘）。 */
    typedef struct {
        float x0, y0, x1, y1, k;   /* k = dx/dy */
        int   row_lo, row_hi;
    } aa_edge_t;
    aa_edge_t edge[SHAPE_PTS_MAX];
    lv_draw_rect_dsc_t rd;
    int ymin = 1 << 20, ymax = -(1 << 20);
    int xmin = 1 << 20, xmax = -(1 << 20);
    /* 每个子样本的覆盖度：**向上取整**。
     * 用 255/8 = 31 的话整块内部只累加到 248（31×8），所有填充都比指定色暗 ~3%
     * （实测管道通道 #1E2A36 画成 #1F2B37、流动块 #4EA1FF 画成 #4C9DF9）。
     * 取 32 时内部累加到 256，由 shape_aa_add 饱和成 255 ⇒ 实色区与指定色**一致**。 */
    const int sub_add = (255 + SHAPE_AA_SUB - 1) / SHAPE_AA_SUB;

    if (n < 3 || n > SHAPE_PTS_MAX || opa == 0) return;
    for (int i = 0; i < n; i++) {
        int px = (int)pts[i].x, py = (int)pts[i].y;

        if (px < xmin) xmin = px;
        if (px > xmax) xmax = px;
        if (py < ymin) ymin = py;
        if (py > ymax) ymax = py;
    }
    if (xmin < 0) xmin = 0;
    if (xmax > HMI_LCD_WIDTH - 1) xmax = HMI_LCD_WIDTH - 1;
    for (int i = 0; i < n; i++) {
        int j = (i + 1) % n;
        float y0 = (float)pts[i].y, y1 = (float)pts[j].y;

        edge[i].x0 = (float)pts[i].x;
        edge[i].y0 = y0;
        edge[i].x1 = (float)pts[j].x;
        edge[i].y1 = y1;
        edge[i].k = (y1 != y0) ? (edge[i].x1 - edge[i].x0) / (y1 - y0) : 0.0f;
        edge[i].row_lo = (int)floor((y0 < y1 ? y0 : y1));
        edge[i].row_hi = (int)ceil((y0 < y1 ? y1 : y0));
    }
    lv_draw_rect_dsc_init(&rd);
    rd.bg_color = color;
    rd.border_width = 0;
    rd.radius = 0;

    for (int y = ymin; y <= ymax; y++) {
        int run_start = -1, run_a = -1;

        for (int px = xmin; px <= xmax; px++) s_aa_acc[px] = 0;

        for (int s = 0; s < SHAPE_AA_SUB; s++) {
            /* 子样本落在像素行内的 1/8、3/8、5/8、7/8 处 */
            float yy = (float)y + ((float)s + 0.5f) / (float)SHAPE_AA_SUB;
            double xs[SHAPE_PTS_MAX];
            int nx = 0;

            for (int i = 0; i < n; i++) {
                const aa_edge_t* e = &edge[i];
                float lo, hi;

                if (y < e->row_lo || y > e->row_hi) continue;
                lo = e->y0 < e->y1 ? e->y0 : e->y1;
                hi = e->y0 < e->y1 ? e->y1 : e->y0;
                if (yy < lo || yy >= hi) continue;
                xs[nx++] = (double)(e->x0 + (yy - e->y0) * e->k);
            }
            for (int a = 1; a < nx; a++) {        /* 交点很少，插入排序 */
                double v = xs[a];
                int b = a - 1;

                while (b >= 0 && xs[b] > v) { xs[b + 1] = xs[b]; b--; }
                xs[b + 1] = v;
            }
            for (int a = 0; a + 1 < nx; a += 2) {
                double lx = xs[a], rx = xs[a + 1];
                int i0, i1;

                if (rx <= lx) continue;
                if (lx < 0) lx = 0;
                if (rx > HMI_LCD_WIDTH) rx = HMI_LCD_WIDTH;
                if (rx <= lx) continue;
                i0 = (int)lx;
                i1 = (int)rx;
                if (i1 >= HMI_LCD_WIDTH) i1 = HMI_LCD_WIDTH - 1;
                if (i0 == i1) {
                    shape_aa_add(i0, (int)((rx - lx) * (double)sub_add + 0.5));
                } else {
                    shape_aa_add(i0, (int)(((double)(i0 + 1) - lx) *
                                           (double)sub_add + 0.5));
                    for (int k = i0 + 1; k < i1; k++) shape_aa_add(k, sub_add);
                    shape_aa_add(i1, (int)((rx - (double)i1) *
                                           (double)sub_add + 0.5));
                }
            }
        }

        /* 把这一行的 alpha 按相同值分段画出去：内圈一两个大段 + 两侧边缘各一段 */
        for (int px = xmin; px <= xmax + 1; px++) {
            int a = (px <= xmax) ? s_aa_acc[px] : -1;

            if (a != run_a) {
                if (run_a > 0 && run_start >= 0) {
                    lv_area_t ra;

                    rd.bg_opa = (lv_opa_t)((int)run_a * (int)opa / 255);
                    ra.x1 = (int32_t)(ox + run_start);
                    ra.x2 = (int32_t)(ox + px - 1);
                    ra.y1 = (int32_t)(oy + y);
                    ra.y2 = ra.y1;
                    lv_draw_rect(layer, &rd, &ra);
                }
                run_start = px;
                run_a = a;
            }
        }
    }
}

/* 逐段描边：圆头端点，顶点处不会缺一个 width/2 的缺口（星形尖角最明显） */
static void shape_stroke_poly(lv_layer_t* layer, int ox, int oy,
                              const lv_point_precise_t* pts, int n, int closed,
                              lv_color_t color, int width)
{
    lv_draw_line_dsc_t ld;
    int edges = closed ? n : n - 1;

    if (width <= 0 || n < 2) return;
    lv_draw_line_dsc_init(&ld);
    ld.color = color;
    ld.width = (int32_t)width;
    ld.opa = LV_OPA_COVER;
    ld.round_start = 1;
    ld.round_end = 1;
    for (int i = 0; i < edges; i++) {
        int j = (i + 1) % n;

        ld.p1.x = (lv_value_precise_t)(ox + pts[i].x);
        ld.p1.y = (lv_value_precise_t)(oy + pts[i].y);
        ld.p2.x = (lv_value_precise_t)(ox + pts[j].x);
        ld.p2.y = (lv_value_precise_t)(oy + pts[j].y);
        lv_draw_line(layer, &ld);
    }
}

/* 自绘回调：单面图元画一个多边形；复合件（cube/wedge）逐面填充 + 逐面描边，
 * 立体件的棱线因此天然可见。 */
static void shape_draw_cb(lv_event_t* e)
{
    widget_t* w = (widget_t*)lv_event_get_user_data(e);
    lv_obj_t* obj = lv_event_get_target_obj(e);
    lv_layer_t* layer = lv_event_get_layer(e);
    lv_area_t area;
    int ox, oy;
    int nf;

    if (!w || !layer || w->u.shape.poly_n < 2) return;
    lv_obj_get_coords(obj, &area);
    ox = area.x1;
    oy = area.y1;

    nf = w->u.shape.nfaces > 0 ? w->u.shape.nfaces : 1;
    for (int f = 0; f < nf; f++) {
        const lv_point_precise_t* pts;
        int n, closed;
        lv_color_t fill;

        if (w->u.shape.nfaces > 0) {
            pts = w->u.shape.poly_pts + w->u.shape.face_begin[f];
            n = w->u.shape.face_len[f];
            fill = w->u.shape.face_fill[f];
            closed = 1;
        } else {
            pts = w->u.shape.poly_pts;
            n = w->u.shape.poly_n;
            fill = w->u.shape.shape_fill;
            closed = w->u.shape.poly_closed;
        }
        if (n < 2) continue;
        if (w->u.shape.have_fill && closed)
            fig_fill_poly(layer, ox, oy, pts, n, fill, (lv_opa_t)w->u.shape.shape_fill_opa);
        if (w->u.shape.shape_border_w > 0 && (!w->u.shape.stroke_first_only || f == 0))
            shape_stroke_poly(layer, ox, oy, pts, n, closed,
                              w->u.shape.shape_border, w->u.shape.shape_border_w);
    }
}

/* 把 [[x,y],...] 读进端点数组；坐标相对控件左上角 */
static int shape_read_points(const hmi_json_t* props, widget_t* out)
{
    const hmi_json_t* arr = hmi_json_get(props, "points");
    size_t n;

    if (hmi_json_type(arr) != HMI_JSON_ARR) return -1;
    n = hmi_json_count(arr);
    if (n < 2 || n > SHAPE_PTS_MAX) return -1;
    out->u.shape.poly_pts = calloc(n, sizeof(*out->u.shape.poly_pts));
    if (!out->u.shape.poly_pts) return -1;
    out->u.shape.poly_n = (int)n;
    for (size_t i = 0; i < n; i++) {
        const hmi_json_t* pt = hmi_json_at(arr, i);

        if (hmi_json_type(pt) != HMI_JSON_ARR || hmi_json_count(pt) != 2) return -1;
        out->u.shape.poly_pts[i].x = (lv_value_precise_t)hmi_json_num(hmi_json_at(pt, 0), 0);
        out->u.shape.poly_pts[i].y = (lv_value_precise_t)hmi_json_num(hmi_json_at(pt, 1), 0);
    }
    return 0;
}

/* 多边形族 + 星形/十字的顶点：形状由 kind 参数决定，不读 points。
 * 全部闭合，与 polygon 共用扫描线填充 + 逐段描边。 */
static int shape_gen_poly(int kind, const hmi_json_t* props, int ww, int hh,
                          widget_t* out)
{
    int vx[SHAPE_PTS_MAX], vy[SHAPE_PTS_MAX];
    int n = 0;
    int cx = ww / 2, cy = hh / 2;

    if (ww < 2 || hh < 2) return -1;

    switch (kind) {
    case SH_PARALLELOGRAM: {
        double skew = hmi_json_num(hmi_json_get(props, "skew"), 0.25);
        int s = (int)(skew * ww / 2.0 + 0.5);

        if (s > ww / 2) s = ww / 2;
        vx[0] = s;      vy[0] = 0;
        vx[1] = ww;     vy[1] = 0;
        vx[2] = ww - s; vy[2] = hh;
        vx[3] = 0;      vy[3] = hh;
        n = 4;
        break;
    }
    case SH_TRAPEZOID: {
        double top_w = hmi_json_num(hmi_json_get(props, "top_w"), 0.5);
        int tw = (int)(top_w * ww + 0.5);
        int x0;

        if (tw < 2) tw = 2;
        if (tw > ww) tw = ww;
        x0 = (ww - tw) / 2;
        vx[0] = x0;      vy[0] = 0;
        vx[1] = x0 + tw; vy[1] = 0;
        vx[2] = ww;      vy[2] = hh;
        vx[3] = 0;       vy[3] = hh;
        n = 4;
        break;
    }
    case SH_DIAMOND:
        vx[0] = cx; vy[0] = 0;
        vx[1] = ww; vy[1] = cy;
        vx[2] = cx; vy[2] = hh;
        vx[3] = 0;  vy[3] = cy;
        n = 4;
        break;
    case SH_OCTAGON: {
        int c = (int)((ww < hh ? ww : hh) * 0.2929 + 0.5);   /* (1 - tan22.5°) / 2 */

        if (c < 1) c = 1;
        vx[0] = c;      vy[0] = 0;
        vx[1] = ww - c; vy[1] = 0;
        vx[2] = ww;     vy[2] = c;
        vx[3] = ww;     vy[3] = hh - c;
        vx[4] = ww - c; vy[4] = hh;
        vx[5] = c;      vy[5] = hh;
        vx[6] = 0;      vy[6] = hh - c;
        vx[7] = 0;      vy[7] = c;
        n = 8;
        break;
    }
    case SH_HEXAGON: {
        int c = ww / 4;

        vx[0] = c;      vy[0] = 0;
        vx[1] = ww - c; vy[1] = 0;
        vx[2] = ww;     vy[2] = cy;
        vx[3] = ww - c; vy[3] = hh;
        vx[4] = c;      vy[4] = hh;
        vx[5] = 0;      vy[5] = cy;
        n = 6;
        break;
    }
    case SH_TRIANGLE:
        vx[0] = cx; vy[0] = 0;
        vx[1] = ww; vy[1] = hh;
        vx[2] = 0;  vy[2] = hh;
        n = 3;
        break;
    case SH_RIGHT_TRIANGLE: {
        const char* flip = hmi_json_str(hmi_json_get(props, "flip"), "bl");

        if (strcmp(flip, "tl") == 0) {
            vx[0] = 0;  vy[0] = 0;
            vx[1] = ww; vy[1] = 0;
            vx[2] = 0;  vy[2] = hh;
        } else if (strcmp(flip, "tr") == 0) {
            vx[0] = ww; vy[0] = 0;
            vx[1] = ww; vy[1] = hh;
            vx[2] = 0;  vy[2] = 0;
        } else if (strcmp(flip, "br") == 0) {
            vx[0] = ww; vy[0] = 0;
            vx[1] = ww; vy[1] = hh;
            vx[2] = 0;  vy[2] = hh;
        } else {                                    /* bl（缺省）：直角在左下 */
            vx[0] = 0;  vy[0] = 0;
            vx[1] = 0;  vy[1] = hh;
            vx[2] = ww; vy[2] = hh;
        }
        n = 3;
        break;
    }
    case SH_STAR5:
    case SH_STAR: {
        int spikes = (kind == SH_STAR5)
                     ? 5 : (int)hmi_json_int(hmi_json_get(props, "spikes"), 12);
        double ir = hmi_json_num(hmi_json_get(props, "inner_ratio"),
                                 kind == SH_STAR5 ? 0.4 : 0.5);
        double rx = ww / 2.0, ry = hh / 2.0;

        if (spikes < 3) spikes = 3;
        if (spikes > SHAPE_PTS_MAX / 2) spikes = SHAPE_PTS_MAX / 2;
        if (ir < 0.05) ir = 0.05;
        if (ir > 0.95) ir = 0.95;
        for (int i = 0; i < spikes * 2; i++) {
            /* 12 点方向起：外顶点在 -90°，内顶点每隔 180/spikes 度交替 */
            double a = (-90.0 + 180.0 * i / spikes) * 3.14159265358979 / 180.0;
            double r = (i % 2) ? ir : 1.0;

            vx[i] = cx + (int)(rx * r * cos(a) + 0.5);
            vy[i] = cy + (int)(ry * r * sin(a) + 0.5);
        }
        n = spikes * 2;
        break;
    }
    case SH_CROSS: {
        double th = hmi_json_num(hmi_json_get(props, "thickness"), 0.3);
        int lim = (ww < hh ? ww : hh);
        int t = (int)(th * lim + 0.5);
        int hx;

        if (t < 2) t = 2;
        if (t > lim) t = lim;
        hx = t / 2;
        vx[0] = cx - hx; vy[0] = 0;
        vx[1] = cx + hx; vy[1] = 0;
        vx[2] = cx + hx; vy[2] = cy - hx;
        vx[3] = ww;      vy[3] = cy - hx;
        vx[4] = ww;      vy[4] = cy + hx;
        vx[5] = cx + hx; vy[5] = cy + hx;
        vx[6] = cx + hx; vy[6] = hh;
        vx[7] = cx - hx; vy[7] = hh;
        vx[8] = cx - hx; vy[8] = cy + hx;
        vx[9] = 0;       vy[9] = cy + hx;
        vx[10] = 0;      vy[10] = cy - hx;
        vx[11] = cx - hx; vy[11] = cy - hx;
        n = 12;
        break;
    }
    case SH_ARROW:
    case SH_ARROW_THICK:
    case SH_ARROW_THIN: {
        /* 块箭头：杆 + 三角头，杆宽按 kind 定档（粗/中/细），head = 头部长度占箭头长度的比例 */
        const char* dir = hmi_json_str(hmi_json_get(props, "direction"), "r");
        double head = hmi_json_num(hmi_json_get(props, "head"),
                                   kind == SH_ARROW_THICK ? 0.35
                                   : kind == SH_ARROW_THIN ? 0.25 : 0.30);
        double shaft = (kind == SH_ARROW_THICK) ? 0.50
                       : (kind == SH_ARROW_THIN) ? 0.14 : 0.30;
        int horiz = (dir[0] == 'l' || dir[0] == 'r');
        int L = horiz ? ww : hh;          /* 沿箭头的长度 */
        int C = horiz ? hh : ww;          /* 横向宽度 */
        int hl, hs, mid = C / 2, base;

        if (ww < 8 || hh < 8) return -1;
        if (head < 0.05) head = 0.05;
        if (head > 0.80) head = 0.80;
        hl = (int)(head * (double)L + 0.5);
        if (hl < 3) hl = 3;
        if (hl > L - 3) hl = L - 3;
        hs = (int)(shaft * (double)C + 0.5);
        if (hs < 2) hs = 2;
        if (hs > C - 2) hs = C - 2;
        base = L - hl;                    /* 杆末端 = 头部起点 */
        if (horiz) {
            int x1 = (dir[0] == 'r') ? base : ww - base;
            int xt = (dir[0] == 'r') ? ww : 0;

            vx[0] = (dir[0] == 'r') ? 0 : ww;      vy[0] = mid - hs / 2;
            vx[1] = x1;                            vy[1] = mid - hs / 2;
            vx[2] = x1;                            vy[2] = 0;
            vx[3] = xt;                            vy[3] = mid;
            vx[4] = x1;                            vy[4] = hh;
            vx[5] = x1;                            vy[5] = mid + hs / 2;
            vx[6] = (dir[0] == 'r') ? 0 : ww;      vy[6] = mid + hs / 2;
        } else {
            int y1 = (dir[0] == 'u') ? base : hh - base;
            int yt = (dir[0] == 'u') ? 0 : hh;

            vx[0] = mid - hs / 2; vy[0] = (dir[0] == 'u') ? hh : 0;
            vx[1] = mid - hs / 2; vy[1] = y1;
            vx[2] = 0;            vy[2] = y1;
            vx[3] = mid;          vy[3] = yt;
            vx[4] = ww;           vy[4] = y1;
            vx[5] = mid + hs / 2; vy[5] = y1;
            vx[6] = mid + hs / 2; vy[6] = (dir[0] == 'u') ? hh : 0;
        }
        n = 7;
        break;
    }
    case SH_WEDGE: {
        /* 楔形与 MCGS 一致是平面五边形：左边矩形、右端上下两条斜边收成尖角 */
        double tip = hmi_json_num(hmi_json_get(props, "tip"), 0.5);
        int xt;

        if (tip < 0.05) tip = 0.05;
        if (tip > 0.95) tip = 0.95;
        xt = ww - (int)(tip * (double)ww + 0.5);
        if (xt < 1) xt = 1;
        vx[0] = 0;  vy[0] = 0;
        vx[1] = xt; vy[1] = 0;
        vx[2] = ww; vy[2] = cy;
        vx[3] = xt; vy[3] = hh;
        vx[4] = 0;  vy[4] = hh;
        n = 5;
        break;
    }
    default:
        return -1;
    }

    out->u.shape.poly_pts = calloc((size_t)n, sizeof(*out->u.shape.poly_pts));
    if (!out->u.shape.poly_pts) return -1;
    for (int i = 0; i < n; i++) {
        out->u.shape.poly_pts[i].x = (lv_value_precise_t)vx[i];
        out->u.shape.poly_pts[i].y = (lv_value_precise_t)vy[i];
    }
    out->u.shape.poly_n = n;
    return 0;
}

/* ── 复合块：callout（单一路径 + 圆角采样）、cube（多面 + 明暗） ── */

typedef struct { int x[SHAPE_PTS_MAX], y[SHAPE_PTS_MAX], n; } shape_ptbuf_t;

static void shape_pb_add(shape_ptbuf_t* b, int x, int y)
{
    if (b->n >= SHAPE_PTS_MAX) return;
    b->x[b->n] = x;
    b->y[b->n] = y;
    b->n++;
}

/* 圆角：以 (cx,cy) 为心、r 为半径，从 a0 到 a1 顺时针采样。
 * r=0 时圆心就是拐点，只落一个点——直角因此不用单独分支。 */
static void shape_pb_arc(shape_ptbuf_t* b, int cx, int cy, int r, int a0, int a1)
{
    int seg = (int)((double)r * 3.14159265358979 *
                    (double)(a1 > a0 ? a1 - a0 : a0 - a1) / 180.0 / 2.5 + 0.5);

    if (seg < 4) seg = 4;
    if (seg > 48) seg = 48;
    if (r <= 0) { shape_pb_add(b, cx, cy); return; }
    for (int i = 0; i <= seg; i++) {
        double a = (a0 + (double)(a1 - a0) * i / seg) * 3.14159265358979 / 180.0;

        shape_pb_add(b, cx + (int)lround(r * cos(a)), cy + (int)lround(r * sin(a)));
    }
}

/* 注释框：圆角矩形 + 一个角的尖角。尖角占用 box 内的 tail_size，
 * 不越出控件框（否则会被对象的裁剪区切掉）；整个轮廓是一条闭合路径，
 * 尖角根部因此不会出现内部分割线。 */
static int shape_gen_callout(const hmi_json_t* props, int ww, int hh, widget_t* out)
{
    const char* tail = hmi_json_str(hmi_json_get(props, "tail"), "bl");
    int ts = (int)hmi_json_int(hmi_json_get(props, "tail_size"), 12);
    int r = (int)hmi_json_int(hmi_json_get(props, "radius"), 0);
    int lim = (ww < hh ? ww : hh) / 2;
    int y0 = 0, y1 = hh;
    int tail_top, tail_bot;
    shape_ptbuf_t b;

    memset(&b, 0, sizeof(b));
    if (ts < 0) ts = 0;
    if (ts > lim) ts = lim;
    tail_top = (strcmp(tail, "tl") == 0 || strcmp(tail, "tr") == 0);
    tail_bot = (strcmp(tail, "bl") == 0 || strcmp(tail, "br") == 0);
    if (ts == 0) tail_top = tail_bot = 0;
    if (tail_top) y0 = ts;
    if (tail_bot) y1 = hh - ts;
    if (y1 - y0 < 2) return -1;

    /* 圆角半径受框内最短边约束，否则相邻两个弧会互相穿过 */
    if (r > (y1 - y0) / 2) r = (y1 - y0) / 2;
    if (r > ww / 2) r = ww / 2;
    if (r < 0) r = 0;

    if (!tail_top && !tail_bot) {                 /* 无尖角：普通圆角矩形 */
        shape_pb_arc(&b, r, y0 + r, r, 180, 270);
        shape_pb_arc(&b, ww - r, y0 + r, r, 270, 360);
        shape_pb_arc(&b, ww - r, y1 - r, r, 0, 90);
        shape_pb_arc(&b, r, y1 - r, r, 90, 180);
    } else if (tail_bot && strcmp(tail, "bl") == 0) {
        shape_pb_arc(&b, r, y0 + r, r, 180, 270);
        shape_pb_arc(&b, ww - r, y0 + r, r, 270, 360);
        shape_pb_arc(&b, ww - r, y1 - r, r, 0, 90);
        shape_pb_add(&b, ts, y1);
        shape_pb_add(&b, 0, hh);                  /* 尖角 */
    } else if (tail_bot) {                        /* br */
        shape_pb_arc(&b, r, y0 + r, r, 180, 270);
        shape_pb_arc(&b, ww - r, y0 + r, r, 270, 360);
        shape_pb_add(&b, ww, hh);                 /* 尖角 */
        shape_pb_add(&b, ww - ts, y1);
        shape_pb_arc(&b, r, y1 - r, r, 90, 180);
    } else if (strcmp(tail, "tl") == 0) {
        shape_pb_add(&b, 0, 0);                   /* 尖角 */
        shape_pb_add(&b, ts, y0);
        shape_pb_arc(&b, ww - r, y0 + r, r, 270, 360);
        shape_pb_arc(&b, ww - r, y1 - r, r, 0, 90);
        shape_pb_arc(&b, r, y1 - r, r, 90, 180);
    } else {                                      /* tr */
        shape_pb_arc(&b, r, y0 + r, r, 180, 270);
        shape_pb_add(&b, ww - ts, y0);
        shape_pb_add(&b, ww, 0);                  /* 尖角 */
        shape_pb_arc(&b, ww - r, y1 - r, r, 0, 90);
        shape_pb_arc(&b, r, y1 - r, r, 90, 180);
    }

    out->u.shape.poly_pts = calloc((size_t)b.n, sizeof(*out->u.shape.poly_pts));
    if (!out->u.shape.poly_pts) return -1;
    for (int i = 0; i < b.n; i++) {
        out->u.shape.poly_pts[i].x = (lv_value_precise_t)b.x[i];
        out->u.shape.poly_pts[i].y = (lv_value_precise_t)b.y[i];
    }
    out->u.shape.poly_n = b.n;
    return 0;
}

/* 立体件明暗：k>0 提亮、k<0 压暗，幅度按 |k| */
static lv_color_t shape_shade(lv_color_t c, double k)
{
    int lvl = (int)(k * 100.0 + (k >= 0 ? 0.5 : -0.5));

    if (lvl > 110) lvl = 110;
    if (lvl < -110) lvl = -110;
    return lvl >= 0 ? lv_color_lighten(c, (lv_opa_t)lvl)
                    : lv_color_darken(c, (lv_opa_t)(-lvl));
}

/* 受光方向 → 三个可见面的明暗系数。把 light 当成投影里的方向向量
 * (±0.8, ∓0.5, 0.33)：正面（朝观察者）吃 z 分量，顶面吃 y、右侧面吃 x。
 * tc/bc 是正上/正下的中间方向，左右两面同亮同暗。 */
static void shape_light_k(const char* light, double* k_front, double* k_top,
                          double* k_right)
{
    int top = 0, right = 0, mid = 0;

    if (strcmp(light, "tl") == 0)      { top = 1;  right = -1; }
    else if (strcmp(light, "tr") == 0) { top = 1;  right = 1; }
    else if (strcmp(light, "bl") == 0) { top = -1; right = -1; }
    else if (strcmp(light, "br") == 0) { top = -1; right = 1; }
    else if (strcmp(light, "tc") == 0) { top = 1;  mid = 1; }
    else if (strcmp(light, "bc") == 0) { top = -1; mid = 1; }
    else                               { top = 1;  right = -1; }   /* 缺省 tl */

    *k_front = 0.33;
    *k_top = top * 0.50;
    *k_right = mid ? 0.0 : (double)right * 0.80;
}

/* 立方体：正面 + 顶面 + 右侧面，深度沿对角线挤出 */
static int shape_gen_cube(const hmi_json_t* props, int ww, int hh, widget_t* out,
                          lv_color_t base)
{
    double depth = hmi_json_num(hmi_json_get(props, "depth"), 0.3);
    const char* light = hmi_json_str(hmi_json_get(props, "light"), "tl");
    int lim = (ww < hh ? ww : hh) / 2;
    int d = (int)(depth * (double)(ww < hh ? ww : hh) + 0.5);
    double kf, kt, kr;
    int pt[12][2];

    if (d < 2) d = 2;
    if (d > lim) d = lim;
    if (ww - d < 2 || hh - d < 2) return -1;

    pt[0][0] = 0;      pt[0][1] = d;                    /* 正面 */
    pt[1][0] = ww - d; pt[1][1] = d;
    pt[2][0] = ww - d; pt[2][1] = hh;
    pt[3][0] = 0;      pt[3][1] = hh;
    pt[4][0] = 0;      pt[4][1] = d;                    /* 顶面 */
    pt[5][0] = d;      pt[5][1] = 0;
    pt[6][0] = ww;     pt[6][1] = 0;
    pt[7][0] = ww - d; pt[7][1] = d;
    pt[8][0] = ww - d; pt[8][1] = d;                    /* 右侧面 */
    pt[9][0] = ww;     pt[9][1] = 0;
    pt[10][0] = ww;    pt[10][1] = hh - d;
    pt[11][0] = ww - d; pt[11][1] = hh;

    out->u.shape.poly_pts = calloc(12, sizeof(*out->u.shape.poly_pts));
    if (!out->u.shape.poly_pts) return -1;
    for (int i = 0; i < 12; i++) {
        out->u.shape.poly_pts[i].x = (lv_value_precise_t)pt[i][0];
        out->u.shape.poly_pts[i].y = (lv_value_precise_t)pt[i][1];
    }
    out->u.shape.poly_n = 12;
    for (int f = 0; f < 3; f++) {
        out->u.shape.face_begin[f] = f * 4;
        out->u.shape.face_len[f] = 4;
    }
    shape_light_k(light, &kf, &kt, &kr);
    out->u.shape.face_fill[0] = shape_shade(base, kf);
    out->u.shape.face_fill[1] = shape_shade(base, kt);
    out->u.shape.face_fill[2] = shape_shade(base, kr);
    out->u.shape.nfaces = 3;
    return 0;
}

/* ── 工艺件：管道 4 件 / 罐形 / 平面 3 件 ──
 * 视觉语言对齐 McgsPro 帮助里的图元图标：管道 = 深管壁 + 浅通道 + 居中流动带；
 * 平面 = 四周斜切边 + 内板，斜切边的明暗由 light 决定。 */

/* 把一组顶点缓冲登记成控件的多个面 */
static int shape_put_faces(widget_t* out, shape_ptbuf_t* bufs,
                           const lv_color_t* fills, int nf)
{
    int total = 0;

    if (nf < 1 || nf > SHAPE_FACES_MAX) return -1;
    for (int f = 0; f < nf; f++) {
        if (bufs[f].n < 3 || bufs[f].n > SHAPE_PTS_MAX) return -1;
        total += bufs[f].n;
    }
    out->u.shape.poly_pts = calloc((size_t)total, sizeof(*out->u.shape.poly_pts));
    if (!out->u.shape.poly_pts) return -1;
    total = 0;
    for (int f = 0; f < nf; f++) {
        out->u.shape.face_begin[f] = total;
        out->u.shape.face_len[f] = bufs[f].n;
        out->u.shape.face_fill[f] = fills[f];
        for (int i = 0; i < bufs[f].n; i++) {
            out->u.shape.poly_pts[total].x = (lv_value_precise_t)bufs[f].x[i];
            out->u.shape.poly_pts[total].y = (lv_value_precise_t)bufs[f].y[i];
            total++;
        }
    }
    out->u.shape.poly_n = total;
    out->u.shape.nfaces = nf;
    return 0;
}

static void shape_pb_rect(shape_ptbuf_t* b, int x, int y, int w, int h)
{
    b->n = 0;
    shape_pb_add(b, x, y);
    shape_pb_add(b, x + w, y);
    shape_pb_add(b, x + w, y + h);
    shape_pb_add(b, x, y + h);
}

/* 椭圆弧：封头用，两轴半径可不同（shape_pb_arc 是正圆） */
static void shape_pb_earc(shape_ptbuf_t* b, int cx, int cy, int rx, int ry,
                          int a0, int a1)
{
    /* 周长近似 pi*sqrt(2*(rx^2+ry^2))，每段 2.5px */
    int seg = (int)(3.14159265358979 * sqrt(((double)rx * rx + (double)ry * ry) / 2.0) *
                    (double)(a1 > a0 ? a1 - a0 : a0 - a1) / 180.0 / 2.5 + 0.5);

    if (seg < 4) seg = 4;
    if (seg > 64) seg = 64;
    if (rx <= 0 || ry <= 0) { shape_pb_add(b, cx, cy); return; }
    for (int i = 0; i <= seg; i++) {
        double a = (a0 + (double)(a1 - a0) * i / seg) * 3.14159265358979 / 180.0;

        shape_pb_add(b, cx + (int)lround(rx * cos(a)), cy + (int)lround(ry * sin(a)));
    }
}

/* 圆角 L 形管道轮廓：从框顶进入、在左下转弯、向右穿出。
 * 外缘距框边 off、管宽 T、外侧弯曲半径 Ro（内侧半径 = Ro - T）。 */
static int shape_l_path(shape_ptbuf_t* b, int ww, int hh, int T, int off, int Ro)
{
    int x0 = off, y0 = off, x1 = ww - off, y1 = hh - off;
    int cx, cy;

    if (T < 2 || Ro < T || x1 - x0 < T + 2 || y1 - y0 < T + 2) return -1;
    if (Ro > y1 - y0 || Ro > x1 - x0) return -1;
    cx = x0 + Ro;
    cy = y1 - Ro;
    b->n = 0;
    shape_pb_add(b, x0, y0);                    /* 竖管外缘（顶端） */
    shape_pb_add(b, x0, cy);                    /* 到弯起点 */
    shape_pb_arc(b, cx, cy, Ro, 180, 90);       /* 外弯 */
    shape_pb_add(b, x1, y1);                    /* 沿底边到横管端头 */
    shape_pb_add(b, x1, y1 - T);                /* 端头竖边 */
    shape_pb_arc(b, cx, cy, Ro - T, 90, 180);   /* 内弯 */
    shape_pb_add(b, x0 + T, y0);                /* 竖管内缘（顶端） */
    return b->n;
}

/* 直角 L 形轮廓：接头用，转角是尖的 */
static int shape_l_sharp(shape_ptbuf_t* b, int ww, int hh, int T, int off)
{
    int x0 = off, y0 = off, x1 = ww - off, y1 = hh - off;

    if (T < 2 || x1 - x0 < T + 2 || y1 - y0 < T + 2) return -1;
    b->n = 0;
    shape_pb_add(b, x0, y0);
    shape_pb_add(b, x0, y1);
    shape_pb_add(b, x1, y1);
    shape_pb_add(b, x1, y1 - T);
    shape_pb_add(b, x0 + T, y1 - T);
    shape_pb_add(b, x0 + T, y0);
    return b->n;
}

/* 绕中心旋转的矩形（阀芯/阀板用）：deg 为与水平方向的夹角 */
static void shape_pb_rot_rect(shape_ptbuf_t* b, int cx, int cy, int len, int th,
                              double deg)
{
    double a = deg * 3.14159265358979 / 180.0;
    double ca = cos(a), sa = sin(a);
    double hl = (double)len / 2.0, ht = (double)th / 2.0;

    b->n = 0;
    shape_pb_add(b, (int)lround(cx + ca * -hl - sa * -ht),
                 (int)lround(cy + sa * -hl + ca * -ht));
    shape_pb_add(b, (int)lround(cx + ca * hl - sa * -ht),
                 (int)lround(cy + sa * hl + ca * -ht));
    shape_pb_add(b, (int)lround(cx + ca * hl - sa * ht),
                 (int)lround(cy + sa * hl + ca * ht));
    shape_pb_add(b, (int)lround(cx + ca * -hl - sa * ht),
                 (int)lround(cy + sa * -hl + ca * ht));
}

/* 阀门 3 件：球阀 / 蝶阀 / 闸阀
 * 统一画法：中间一条流通带（管体 + 内腔）+ 上方手轮杆，阀芯按 open 变位：
 *   球阀 = 圆阀芯，通道随 open 从竖直转到水平；
 *   蝶阀 = 阀板随 open 从竖直（堵住）转到水平（全开）；
 *   闸阀 = 阀板随 open 上提。
 * open 是 0..1 的静态开度，不做流动动画（那是 pipe 控件的事）。 */
static int shape_gen_valve(const hmi_json_t* props, int ww, int hh, widget_t* out,
                           lv_color_t base, int mode)
{
    double open = hmi_json_num(hmi_json_get(props, "open"), 0.5);
    int bh, y0, wt, cx, cy, nf = 0;
    shape_ptbuf_t b[SHAPE_FACES_MAX];
    lv_color_t f[SHAPE_FACES_MAX];

    if (ww < 12 || hh < 12) return -1;
    if (open < 0) open = 0;
    if (open > 1) open = 1;
    bh = (int)((double)hh * 0.42 + 0.5);
    if (bh < 10) bh = 10;
    if (bh > hh) bh = hh;
    y0 = (hh - bh) / 2;
    wt = bh / 5;
    if (wt < 2) wt = 2;
    cx = ww / 2;
    cy = hh / 2;
    memset(b, 0, sizeof(b));

    /* 管体 + 内腔（内腔压暗，视觉上是"通的"） */
    shape_pb_rect(&b[nf], 0, y0, ww, bh);
    f[nf++] = base;
    shape_pb_rect(&b[nf], 0, y0 + wt, ww, bh - 2 * wt);
    f[nf++] = shape_shade(base, -0.55);

    if (mode == 0) {                       /* 球阀 */
        int r = (bh + wt) / 2;
        int plen, pth;

        if (r > hh / 2) r = hh / 2;
        if (r < 4) return -1;
        shape_pb_arc(&b[nf], cx, cy, r, 0, 360);
        f[nf++] = shape_shade(base, 0.30);
        /* 通道：关=竖直（垂直于流向），全开=水平 */
        plen = r;
        pth = r / 2;
        if (pth < 2) pth = 2;
        shape_pb_rot_rect(&b[nf], cx, cy, plen, pth, 90.0 * (1.0 - open));
        f[nf++] = shape_shade(base, -0.80);
    } else if (mode == 1) {                /* 蝶阀 */
        int plen = (bh * 9) / 10, pth = bh / 8;

        if (pth < 3) pth = 3;
        shape_pb_rot_rect(&b[nf], cx, cy, plen, pth, 90.0 * (1.0 - open));
        f[nf++] = shape_shade(base, 0.40);
    } else {                               /* 闸阀 */
        int ph = (bh * 19) / 20, pw = bh / 3;
        int y_plate;

        if (pw < 5) pw = 5;
        /* 先画杆再画板：板要盖在杆上，否则全开时只见一根粗杆 */
        shape_pb_rect(&b[nf], cx - 2, y0 - hh / 6, 4, hh / 6 + ph / 2);
        f[nf++] = shape_shade(base, 0.15);
        /* open=0 时阀板压在流通带正中，open=1 时提到管体之上 */
        y_plate = y0 + (bh - ph) / 2 - (int)(open * (double)ph * 0.95);
        shape_pb_rect(&b[nf], cx - pw / 2, y_plate, pw, ph);
        f[nf++] = shape_shade(base, 0.40);
    }

    /* 手轮杆：从管体上沿往上伸一小段（闸阀的杆已单独画过） */
    if (mode != 2) {
        shape_pb_rect(&b[nf], cx - 2, y0 - hh / 6, 4, hh / 6);
        f[nf++] = shape_shade(base, 0.15);
    }

    return shape_put_faces(out, b, f, nf);
}

/* 直管道：管壁 + 通道 + 流动带三层。wall 按管径（短边）算 */
static int shape_gen_pipe(const hmi_json_t* props, int ww, int hh, widget_t* out,
                          lv_color_t base, int vertical)
{
    double wall = hmi_json_num(hmi_json_get(props, "wall"), 0.15);
    int dia = vertical ? ww : hh;
    int wt = (int)(wall * (double)dia + 0.5);
    int ch, band;
    shape_ptbuf_t b[3];
    lv_color_t f[3];

    if (dia < 8) return -1;
    if (wt < 1) wt = 1;
    if (wt > dia / 2 - 2) wt = dia / 2 - 2;
    if (wt < 1) return -1;
    ch = dia - 2 * wt;
    if (ch < 4) return -1;
    band = ch / 3;
    memset(b, 0, sizeof(b));
    if (!vertical) {
        shape_pb_rect(&b[0], 0, 0, ww, hh);
        shape_pb_rect(&b[1], 0, wt, ww, ch);
        shape_pb_rect(&b[2], 0, wt + (ch - band) / 2, ww, band);
    } else {
        shape_pb_rect(&b[0], 0, 0, ww, hh);
        shape_pb_rect(&b[1], wt, 0, ch, hh);
        shape_pb_rect(&b[2], wt + (ch - band) / 2, 0, band, hh);
    }
    f[0] = base;
    f[1] = shape_shade(base, 0.66);
    f[2] = shape_shade(base, 0.12);
    return shape_put_faces(out, b, f, 3);
}

/* 弯曲管道：三层 L 形轮廓，bend 定外弯半径 */
static int shape_gen_elbow(const hmi_json_t* props, int ww, int hh, widget_t* out,
                           lv_color_t base)
{
    double wall = hmi_json_num(hmi_json_get(props, "wall"), 0.15);
    double bend = hmi_json_num(hmi_json_get(props, "bend"), 0.5);
    int M = ww < hh ? ww : hh;
    int T, wt, ch, band, bo, Ro, lim;
    shape_ptbuf_t b[3];
    lv_color_t f[3];

    if (M < 20) return -1;
    T = (int)(2.0 * wall * (double)M + 0.5);
    if (T < 6) T = 6;
    if (T > M / 2) T = M / 2;
    wt = (int)(wall * (double)T + 0.5);
    if (wt < 1) wt = 1;
    ch = T - 2 * wt;
    if (ch < 4) return -1;
    band = ch / 3;
    bo = wt + (ch - band) / 2;
    lim = M - 3 - 2 * wt;              /* 给内层轮廓留出壁厚 */
    Ro = (int)(bend * (double)M + 0.5);
    if (Ro < T + 2) Ro = T + 2;
    if (Ro > lim) Ro = lim;
    if (Ro - bo < band + 1) return -1;
    memset(b, 0, sizeof(b));
    if (shape_l_path(&b[0], ww, hh, T, 0, Ro) < 0) return -1;
    if (shape_l_path(&b[1], ww, hh, ch, wt, Ro - wt) < 0) return -1;
    if (shape_l_path(&b[2], ww, hh, band, bo, Ro - bo) < 0) return -1;
    f[0] = base;
    f[1] = shape_shade(base, 0.66);
    f[2] = shape_shade(base, 0.12);
    return shape_put_faces(out, b, f, 3);
}

/* 管道接头：直角 L 形管 + 两圈法兰 + 法线上的螺栓（flaps 个，方形头） */
static int shape_gen_joint(const hmi_json_t* props, int ww, int hh, widget_t* out,
                           lv_color_t base)
{
    int flaps = (int)hmi_json_int(hmi_json_get(props, "flaps"), 4);
    int M = ww < hh ? ww : hh;
    int T, wt, ch, band, bo, fw, bs, nf, half;
    shape_ptbuf_t b[SHAPE_FACES_MAX];
    lv_color_t f[SHAPE_FACES_MAX];

    if (M < 24) return -1;
    if (flaps < 1) flaps = 1;
    if (flaps > 8) flaps = 8;
    T = M / 2;
    wt = T / 6;
    if (wt < 1) wt = 1;
    ch = T - 2 * wt;
    if (ch < 4) return -1;
    band = ch / 3;
    bo = wt + (ch - band) / 2;
    memset(b, 0, sizeof(b));

    if (shape_l_sharp(&b[0], ww, hh, T, 0) < 0) return -1;
    if (shape_l_sharp(&b[1], ww, hh, ch, wt) < 0) return -1;
    if (shape_l_sharp(&b[2], ww, hh, band, bo) < 0) return -1;
    f[0] = base;
    f[1] = shape_shade(base, 0.66);
    f[2] = shape_shade(base, 0.12);

    fw = T / 3;
    if (fw < 3) fw = 3;
    shape_pb_rect(&b[3], 0, hh * 18 / 100, T, fw);
    shape_pb_rect(&b[4], ww * 55 / 100, hh - T, fw, T);
    f[3] = shape_shade(base, 0.45);
    f[4] = f[3];

    bs = T / 7;
    if (bs < 2) bs = 2;
    nf = 5;
    half = flaps - flaps / 2;
    for (int k = 0; k < flaps && nf < SHAPE_FACES_MAX; k++) {
        if (k < half) {
            int x = T * (k + 1) / (half + 1) - bs / 2;
            int y = hh * 18 / 100 + fw / 2 - bs / 2;

            shape_pb_rect(&b[nf], x, y, bs, bs);
        } else {
            int n = flaps - half, idx = k - half;
            int x = ww * 55 / 100 + fw / 2 - bs / 2;
            int y = hh - T + T * (idx + 1) / (n + 1) - bs / 2;

            shape_pb_rect(&b[nf], x, y, bs, bs);
        }
        f[nf] = shape_shade(base, 0.9);
        nf++;
    }
    return shape_put_faces(out, b, f, nf);
}

/* 罐形：立式罐（上封头可选、下封头、壁厚内腔）。契约参数是 wall/flare/top，
 * 所以这里画立式；McgsPro 的罐形图标是卧式带液位线，两者取向不同。 */
static int shape_gen_tank(const hmi_json_t* props, int ww, int hh, widget_t* out,
                          lv_color_t base)
{
    double wall = hmi_json_num(hmi_json_get(props, "wall"), 0.08);
    double flare = hmi_json_num(hmi_json_get(props, "flare"), 0.15);
    int top = (int)hmi_json_int(hmi_json_get(props, "top"), 1);
    int M = ww < hh ? ww : hh;
    int fl = (int)(flare * (double)hh + 0.5);
    int wt = (int)(wall * (double)M + 0.5);
    shape_ptbuf_t b[2];
    lv_color_t f[2];

    if (ww < 10 || hh < 16) return -1;
    if (fl < 4) fl = 4;
    if (fl > hh / 2 - 2) fl = hh / 2 - 2;
    if (wt < 1) wt = 1;
    if (wt > ww / 2 - 2) return -1;
    if (wt > fl - 2) wt = fl - 2;    /* 封头再薄也要留出内腔，不能把罐体压没 */
    if (wt < 1 || fl - wt < 2) return -1;
    memset(b, 0, sizeof(b));

    /* 外轮廓 */
    if (top) shape_pb_earc(&b[0], ww / 2, fl, ww / 2, fl, 180, 360);
    else { shape_pb_add(&b[0], 0, fl); shape_pb_add(&b[0], ww, fl); }
    shape_pb_add(&b[0], ww, hh - fl);
    shape_pb_earc(&b[0], ww / 2, hh - fl, ww / 2, fl, 0, 180);

    /* 内腔：整体内缩一个壁厚 */
    if (top) shape_pb_earc(&b[1], ww / 2, fl + wt / 2, ww / 2 - wt, fl - wt, 180, 360);
    else { shape_pb_add(&b[1], wt, fl + 1); shape_pb_add(&b[1], ww - wt, fl + 1); }
    shape_pb_add(&b[1], ww - wt, hh - fl - 1);
    shape_pb_earc(&b[1], ww / 2, hh - fl - wt / 2, ww / 2 - wt, fl - wt, 0, 180);

    f[0] = base;
    f[1] = shape_shade(base, 0.55);
    return shape_put_faces(out, b, f, 2);
}

/* 斜切边受光：edge 0=上 1=右 2=下 3=左；invert 用于凹面（内壁背光） */
static double shape_edge_k(const char* light, int edge, int invert)
{
    double kt = (light[0] == 't') ? 0.45 : -0.45;
    double kr = 0.0, k;

    if (light[1] == 'l') kr = -0.45;          /* 光在左 → 右边暗 */
    else if (light[1] == 'r') kr = 0.45;      /* 光在右 → 右边亮 */
    switch (edge) {
    case 0: k = kt; break;
    case 1: k = kr; break;
    case 2: k = -kt; break;
    default: k = -kr; break;
    }
    return invert ? -k : k;
}

/* 平面三件：凹/凸面 = 四周斜切边 + 内板；凹槽 = 凸面再挖一条横槽 */
static int shape_gen_plane(const hmi_json_t* props, int ww, int hh, widget_t* out,
                           lv_color_t base, int mode)
{
    const char* light = hmi_json_str(hmi_json_get(props, "light"), "tc");
    double groove_w = hmi_json_num(hmi_json_get(props, "groove_w"), 0.3);
    int M = ww < hh ? ww : hh;
    int bv = M * 14 / 100;
    int concave = (mode == 0);
    int nf = 5;
    shape_ptbuf_t b[SHAPE_FACES_MAX];
    lv_color_t f[SHAPE_FACES_MAX];

    if (M < 14) return -1;
    if (bv < 3) bv = 3;
    if (bv > M / 3) bv = M / 3;
    memset(b, 0, sizeof(b));

    /* 四条斜切边做成梯形，四角是斜接的（矩形会在角上叠成台阶） */
    b[0].n = 0;
    shape_pb_add(&b[0], 0, 0);      shape_pb_add(&b[0], ww, 0);
    shape_pb_add(&b[0], ww - bv, bv); shape_pb_add(&b[0], bv, bv);
    b[1].n = 0;
    shape_pb_add(&b[1], ww, 0);     shape_pb_add(&b[1], ww, hh);
    shape_pb_add(&b[1], ww - bv, hh - bv); shape_pb_add(&b[1], ww - bv, bv);
    b[2].n = 0;
    shape_pb_add(&b[2], ww, hh);    shape_pb_add(&b[2], 0, hh);
    shape_pb_add(&b[2], bv, hh - bv); shape_pb_add(&b[2], ww - bv, hh - bv);
    b[3].n = 0;
    shape_pb_add(&b[3], 0, 0);      shape_pb_add(&b[3], 0, hh);
    shape_pb_add(&b[3], bv, hh - bv); shape_pb_add(&b[3], bv, bv);
    shape_pb_rect(&b[4], bv, bv, ww - 2 * bv, hh - 2 * bv);
    for (int e = 0; e < 4; e++)
        f[e] = shape_shade(base, shape_edge_k(light, e, concave));
    f[4] = shape_shade(base, concave ? -0.25 : 0.22);

    if (mode == 2) {                          /* 凹槽：横槽 + 上暗下亮的内壁 */
        int gw = (int)(groove_w * (double)hh + 0.5);
        int gy0, gy1, wl;

        if (gw < 4) gw = 4;
        gy0 = hh / 2 - gw / 2;
        gy1 = gy0 + gw;
        if (gy0 < bv + 2) gy0 = bv + 2;
        if (gy1 > hh - bv - 2) gy1 = hh - bv - 2;
        wl = gw / 4;
        if (wl < 2) wl = 2;
        if (gy1 - gy0 < wl + 3) return shape_put_faces(out, b, f, 5);
        shape_pb_rect(&b[5], bv + 1, gy0 - wl, ww - 2 * bv - 2, wl);
        shape_pb_rect(&b[6], bv + 1, gy1, ww - 2 * bv - 2, wl);
        shape_pb_rect(&b[7], bv + 1, gy0, ww - 2 * bv - 2, gy1 - gy0);
        f[5] = shape_shade(base, shape_edge_k(light, 0, 1));
        f[6] = shape_shade(base, shape_edge_k(light, 2, 1));
        f[7] = shape_shade(base, -0.30);
        nf = 8;
    }
    return shape_put_faces(out, b, f, nf);
}

/* ── 三维三件：用分块渐变近似光照，light 定高光方向（不做真光照） ── */

/* light → 高光位置（0..1 比例，屏幕坐标 y 向下） */
static void shape_light_uv(const char* light, double* u, double* v)
{
    *u = (light[1] == 'l') ? 0.28 : (light[1] == 'r') ? 0.72 : 0.5;
    *v = (light[0] == 't') ? 0.28 : (light[0] == 'b') ? 0.72 : 0.5;
}

static void shape_pb_ellipse(shape_ptbuf_t* b, int cx, int cy, int rx, int ry)
{
    int seg = (int)(2.0 * 3.14159265358979 *
                    sqrt(((double)rx * rx + (double)ry * ry) / 2.0) / 2.5 + 0.5);

    if (seg < 24) seg = 24;
    if (seg > 120) seg = 120;
    b->n = 0;
    if (rx < 1 || ry < 1) { shape_pb_add(b, cx, cy); return; }
    for (int i = 0; i < seg; i++) {
        double a = 2.0 * 3.14159265358979 * (double)i / (double)seg;

        shape_pb_add(b, cx + (int)lround(rx * cos(a)), cy + (int)lround(ry * sin(a)));
    }
}

/* 三维圆球：一圈圈内缩的椭圆，圆心朝高光方向偏移，越靠里越亮 */
static int shape_gen_ball3d(const hmi_json_t* props, int ww, int hh, widget_t* out,
                            lv_color_t base)
{
    const char* light = hmi_json_str(hmi_json_get(props, "light"), "tl");
    enum { N = 12 };
    shape_ptbuf_t b[N];
    lv_color_t f[N];
    int cx = ww / 2, cy = hh / 2, rx = ww / 2, ry = hh / 2;
    double u, v, hx, hy;

    if (ww < 10 || hh < 10) return -1;
    shape_light_uv(light, &u, &v);
    hx = u * (double)ww;
    hy = v * (double)hh;
    memset(b, 0, sizeof(b));
    for (int k = 0; k < N; k++) {
        double s = 1.0 - (double)k / N;           /* 最外层 = 1 */
        double t = (double)k / (N - 1);           /* 0 = 边缘，1 = 高光中心 */
        int ecx = cx + (int)((hx - cx) * (1.0 - s) * 0.9);
        int ecy = cy + (int)((hy - cy) * (1.0 - s) * 0.9);

        shape_pb_ellipse(&b[k], ecx, ecy, (int)(rx * s), (int)(ry * s));
        f[k] = shape_shade(base, -0.60 + 1.45 * t);
    }
    out->u.shape.stroke_first_only = 1;
    return shape_put_faces(out, b, f, N);
}

/* 三维圆环：整圈打底（兼轮廓）+ 12 段按角度取明暗 */
static int shape_gen_torus3d(const hmi_json_t* props, int ww, int hh, widget_t* out,
                             lv_color_t base)
{
    const char* light = hmi_json_str(hmi_json_get(props, "light"), "tl");
    double thick = hmi_json_num(hmi_json_get(props, "thickness"), 0.25);
    enum { N = 14 };
    shape_ptbuf_t b[N + 1];
    lv_color_t f[N + 1];
    int cx = ww / 2, cy = hh / 2;
    int R = (ww < hh ? ww : hh) / 2;
    int t = (int)(thick * (double)R + 0.5);
    int Ri = R - t;
    double u, v, la;

    if (R < 12 || Ri < 4 || t < 2) return -1;
    shape_light_uv(light, &u, &v);
    la = atan2(v - 0.5, u - 0.5);
    memset(b, 0, sizeof(b));

    /* 面 0：外圆 + 内圆反向（环带），填充暗色兼作描边 */
    b[0].n = 0;
    for (int i = 0; i < 48; i++) {
        double a = 2.0 * 3.14159265358979 * (double)i / 48.0;

        shape_pb_add(&b[0], cx + (int)lround(R * cos(a)), cy + (int)lround(R * sin(a)));
    }
    for (int i = 35; i >= 0; i--) {
        double a = 2.0 * 3.14159265358979 * (double)i / 36.0;

        shape_pb_add(&b[0], cx + (int)lround(Ri * cos(a)), cy + (int)lround(Ri * sin(a)));
    }
    f[0] = shape_shade(base, -0.55);

    for (int k = 0; k < N; k++) {
        double a0 = 2.0 * 3.14159265358979 * (double)k / N;
        double a1 = 2.0 * 3.14159265358979 * (double)(k + 1) / N;
        double facing = cos((a0 + a1) / 2.0 - la);

        b[1 + k].n = 0;
        for (int i = 0; i <= 2; i++) {
            double a = a0 + (a1 - a0) * (double)i / 2.0;

            shape_pb_add(&b[1 + k], cx + (int)lround(R * cos(a)),
                         cy + (int)lround(R * sin(a)));
        }
        for (int i = 2; i >= 0; i--) {
            double a = a0 + (a1 - a0) * (double)i / 2.0;

            shape_pb_add(&b[1 + k], cx + (int)lround(Ri * cos(a)),
                         cy + (int)lround(Ri * sin(a)));
        }
        f[1 + k] = shape_shade(base, -0.30 + 0.95 * facing);
    }
    out->u.shape.stroke_first_only = 1;
    return shape_put_faces(out, b, f, N + 1);
}

/* 三维椎体：顶点 + 底面前半弧的轮廓打底，再按角度铺三角分块 */
static int shape_gen_cone3d(const hmi_json_t* props, int ww, int hh, widget_t* out,
                            lv_color_t base)
{
    const char* light = hmi_json_str(hmi_json_get(props, "light"), "tl");
    enum { N = 12 };
    shape_ptbuf_t b[N + 1];
    lv_color_t f[N + 1];
    int cx = ww / 2;
    int rx, ry, ay = 1, by;
    double u, v, la;

    if (ww < 14 || hh < 16) return -1;
    rx = ww / 2 - 1;
    ry = rx / 3;
    if (ry < 3) ry = 3;
    by = hh - ry - 1;
    if (by < ay + 8) return -1;
    shape_light_uv(light, &u, &v);
    la = atan2(v - 0.5, u - 0.5);
    memset(b, 0, sizeof(b));

    b[0].n = 0;                                   /* 面 0：整体轮廓 */
    shape_pb_add(&b[0], cx, ay);
    shape_pb_earc(&b[0], cx, by, rx, ry, 0, 180);
    f[0] = shape_shade(base, -0.55);

    for (int k = 0; k < N; k++) {
        double a0 = 3.14159265358979 * (double)k / N;
        double a1 = 3.14159265358979 * (double)(k + 1) / N;
        double facing = cos((a0 + a1) / 2.0 - la);

        b[1 + k].n = 0;
        shape_pb_add(&b[1 + k], cx, ay);
        shape_pb_add(&b[1 + k], cx + (int)lround(rx * cos(a0)),
                     by + (int)lround(ry * sin(a0)));
        shape_pb_add(&b[1 + k], cx + (int)lround(rx * cos(a1)),
                     by + (int)lround(ry * sin(a1)));
        f[1 + k] = shape_shade(base, -0.25 + 0.95 * facing);
    }
    out->u.shape.stroke_first_only = 1;
    return shape_put_faces(out, b, f, N + 1);
}

/* 扇形 = 圆心 + 弧上的点：弧点单独闭合只是一个弓形，必须补圆心才是楔形 */
static int shape_pie_close(int ww, int hh, widget_t* out)
{
    int n = out->u.shape.poly_n;
    int keep = n;
    lv_point_precise_t* p;

    if (keep > SHAPE_PTS_MAX - 2) keep = SHAPE_PTS_MAX - 2;
    p = calloc((size_t)keep + 2, sizeof(*p));
    if (!p) return -1;
    p[0].x = (lv_value_precise_t)(ww / 2);
    p[0].y = (lv_value_precise_t)(hh / 2);
    for (int i = 0; i < keep; i++) {
        /* 弧点超过上限时按比例抽稀，两端保留 */
        int src = (keep == n) ? i : (int)((long)i * (n - 1) / (keep - 1));

        p[i + 1] = out->u.shape.poly_pts[src];
    }
    p[keep + 1] = p[0];
    free(out->u.shape.poly_pts);
    out->u.shape.poly_pts = p;
    out->u.shape.poly_n = keep + 2;
    return 0;
}

/* 自绘图元共用的对象：透明底 + DRAW_MAIN 回调；顶点坐标已相对控件左上角 */
static int shape_make_custom(long x, long y, long ww, long hh, widget_t* out)
{
    lv_obj_t* o = lv_obj_create(ui_cur_parent());

    if (!o) return -1;
    out->u.shape.poly_closed = 1;      /* 走到这里的都是闭合图元；折线走 lv_line */
    ui_place(o, x, y, ww, hh);
    lv_obj_set_style_bg_opa(o, LV_OPA_TRANSP, 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_event_cb(o, shape_draw_cb, LV_EVENT_DRAW_MAIN, out);
    out->u.shape.shape_is_custom = 1;
    out->obj = o;
    out->type = W_SHAPE;
    return 0;
}

/* 椭圆/扇形按角度采样成多边形点（LVGL 没有椭圆绘制原语） */
static int shape_gen_ellipse(int ww, int hh, int a0, int a1, widget_t* out)
{
    int span = (a1 > a0) ? a1 - a0 : a0 - a1;
    int n = (int)(3.14159265358979 * sqrt(((double)(ww / 2) * (ww / 2) +
                                           (double)(hh / 2) * (hh / 2)) / 2.0) *
                  (double)span / 180.0 / 2.5 + 0.5);

    if (n < 3) n = 3;
    if (n > SHAPE_PTS_MAX - 1) n = SHAPE_PTS_MAX - 1;
    out->u.shape.poly_pts = calloc((size_t)n + 1, sizeof(*out->u.shape.poly_pts));
    if (!out->u.shape.poly_pts) return -1;
    for (int i = 0; i <= n; i++) {
        double a = (double)(a0 + (a1 - a0) * i / n) * 3.14159265358979 / 180.0;
        /* 必须四舍五入：截断在负半轴会少 1px，左右不对称、看着像被压扁 */
        out->u.shape.poly_pts[i].x = (lv_value_precise_t)(ww / 2 + lround((double)ww / 2 * cos(a)));
        out->u.shape.poly_pts[i].y = (lv_value_precise_t)(hh / 2 + lround((double)hh / 2 * sin(a)));
    }
    out->u.shape.poly_n = n + 1;
    return 0;
}

/* 进度弧：把绑定值折算成 INDICATOR 段的长度。
 *
 * 三个必须守住的点：
 *   1. 第一次被调用才把 INDICATOR 的弧宽从 0 打开 —— 没有 value 绑定的图元一个像素都不动；
 *   2. 比例为 0 时把 INDICATOR 置透明而不是"起止角相等" —— LVGL 里 start==end 是**整圈**，
 *      值掉到量程下限反而会画出满环；
 *   3. 角度交给 lv_arc_set_value 自己算（它按 bg_angle_start..bg_end 用 lv_map 折算），
 *      不要自己 set_angles —— 自己算的那一版在 0..360 的弧上"看着对"，
 *      换一组角度就对不上（弧长与比例不单调），而 LVGL 的归一化规则不好在外部复刻。
 *
 * 角度已在 figure_build 里规范化到 [0,360)（负角必须先平移，否则掩码会把它夹到 0），
 * 所以这里拿到的 bg 角度是跨 0 也能用的那一对。
 */
void ui_arc_set_value(widget_t* w, double v)
{
    wt_shape_t* s;
    double ratio;
    int lo, hi;

    if (!w || w->type != W_SHAPE || !w->obj) return;
    s = &w->u.shape;
    if (s->arc_w <= 0) return;              /* 不是 arc 图元：静默跳过 */
    lo = w->min;
    hi = w->max;
    if (hi <= lo) hi = lo + 1;              /* 量程非法时退化成"全满"，不让除法爆掉 */
    ratio = (v - (double)lo) / (double)(hi - lo);
    if (ratio < 0) ratio = 0.0;
    if (ratio > 1) ratio = 1.0;

    if (!s->arc_progress) {
        s->arc_progress = 1;
        lv_obj_set_style_arc_width(w->obj, s->arc_w, LV_PART_INDICATOR);
    }
    if (ratio <= 0.0) {
        lv_obj_set_style_arc_opa(w->obj, LV_OPA_TRANSP, LV_PART_INDICATOR);
        return;
    }
    lv_obj_set_style_arc_opa(w->obj, LV_OPA_COVER, LV_PART_INDICATOR);
    lv_arc_set_range(w->obj, lo, hi);
    lv_arc_set_value(w->obj, (int32_t)(v >= 0 ? v + 0.5 : v - 0.5));
}

int figure_build(const hmi_json_t* props, long x, long y, long ww, long hh,
                 widget_t* out)
{
    const char* kind_s = hmi_json_str(hmi_json_get(props, "kind"), "rect");
    const char* fill = hmi_json_str(hmi_json_get(props, "fill_color"), "#243240");
    const char* border = hmi_json_str(hmi_json_get(props, "border_color"), "#3A4A5A");
    int bw = (int)hmi_json_int(hmi_json_get(props, "border_width"), 0);
    int radius = (int)hmi_json_int(hmi_json_get(props, "radius"), 0);
    int fopa = (int)hmi_json_int(hmi_json_get(props, "fill_opa"), 255);
    int kind = -1;
    lv_obj_t* o;

    for (size_t i = 0; i < sizeof(s_shape_kinds) / sizeof(s_shape_kinds[0]); i++)
        if (strcmp(s_shape_kinds[i].name, kind_s) == 0) { kind = s_shape_kinds[i].kind; break; }
    if (kind < 0) return -1;
    if (!ui_color_valid(fill) || !ui_color_valid(border)) return -1;
    if (fopa < 0) fopa = 0;
    if (fopa > 255) fopa = 255;

    out->u.shape.shape_fill = ui_color_hex(fill);
    out->u.shape.shape_border = ui_color_hex(border);
    out->u.shape.shape_border_w = bw;
    out->u.shape.shape_fill_opa = fopa;
    out->u.shape.have_fill = (hmi_json_get(props, "fill_color") != NULL) && fopa > 0;

    /* arc 用 lv_arc：环段而不是填充 */
    if (kind == SH_ARC) {
        int a0 = (int)hmi_json_int(hmi_json_get(props, "angle_start"), 0);
        int a1 = (int)hmi_json_int(hmi_json_get(props, "angle_end"), 360);
        int aw = bw > 0 ? bw : 4;
        /* 圆弧是描边图元：面板上的「描边色」就是它的颜色。只在显式给了 border_color
         * 时才用它 —— 只写 fill_color 的手写工程观感不变。 */
        const char* stroke = hmi_json_get(props, "border_color") ? border : fill;
        /* 进度段（INDICATOR）的颜色取 fill_color；没显式给就用环色（同色 = 看不出进度，
         * 但手写工程里只写了一个颜色的图元观感不变）。 */
        const char* prog = hmi_json_get(props, "fill_color") ? fill : stroke;

        /* 角度必须先自己规范化：契约允许 -360..720，而 lv_draw_sw_mask 会把 <0 的角
         * **夹到 0**（不是加 360）⇒ 直接传 -210..30 画出来是 30° 而不是 240°。
         * 平移不改跨度；跨 0（start > end）由掩码的 delta = 360-start+end 处理。
         * 跨度正好 360 时要留成 a0+360，否则归一成 start==end 会一根线都不画。 */
        while (a0 < 0) { a0 += 360; a1 += 360; }
        while (a1 < 0) a1 += 360;
        if (a1 - a0 > 360) a1 = a0 + 360;              /* 超过一圈按一圈 */
        while (a0 >= 360) { a0 -= 360; a1 -= 360; }
        if (a1 > 360 && a1 - a0 < 360) a1 -= 360;

        o = lv_arc_create(ui_cur_parent());
        if (!o) return -1;
        ui_place(o, x, y, ww, hh);
        lv_arc_set_bg_angles(o, a0, a1);
        lv_arc_set_angles(o, a0, a1);
        lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_arc_width(o, aw, LV_PART_MAIN);
        lv_obj_set_style_arc_color(o, ui_color_hex(stroke), LV_PART_MAIN);
        /* 进度段默认不画：只有 bind.value 出现过才打开（ui_arc_set_value），
         * 所以没有 value 绑定的老工程与"只画背景环"的旧观感逐像素相同。 */
        lv_obj_set_style_arc_width(o, 0, LV_PART_INDICATOR);
        lv_obj_set_style_arc_color(o, ui_color_hex(prog), LV_PART_INDICATOR);
        lv_obj_set_style_bg_opa(o, LV_OPA_TRANSP, LV_PART_KNOB);
        out->u.shape.arc_w = aw;
        out->u.shape.arc_progress = 0;
        /* 量程只服务进度段；弧的几何（角度）仍由 angle_start/angle_end 决定 */
        out->min = (int)hmi_json_int(hmi_json_get(props, "min"), 0);
        out->max = (int)hmi_json_int(hmi_json_get(props, "max"), 100);
        out->obj = o;
        out->type = W_SHAPE;
        return 0;
    }

    /* 直线：带 points 就是**真线段**（两点，可斜向）；不带仍是"填充的窄矩形"（兼容旧工程）。
     * 编辑器按点画出来的直线一定带 points —— 否则拖端点的结果会与产物对不上。 */
    if (kind == SH_LINE) {
        const hmi_json_t* arr = hmi_json_get(props, "points");

        if (hmi_json_type(arr) == HMI_JSON_ARR && hmi_json_count(arr) >= 2 &&
            hmi_json_count(arr) <= SHAPE_PTS_MAX) {
            if (shape_read_points(props, out) != 0) return -1;
            o = lv_line_create(ui_cur_parent());
            if (!o) return -1;
            ui_place(o, x, y, ww, hh);
            lv_obj_set_style_line_width(o, bw > 0 ? bw : 2, 0);
            /* 描边色优先（面板上的「边框色/线色」），只给了 fill_color 的手写工程仍按原色画 */
            lv_obj_set_style_line_color(o, ui_color_hex(hmi_json_get(props, "border_color") ? border : fill), 0);
            lv_obj_set_style_line_rounded(o, true, 0);
            lv_line_set_points(o, out->u.shape.poly_pts, (uint32_t)out->u.shape.poly_n);
            HMI_LOG("figure: line with %d points", out->u.shape.poly_n);
            out->obj = o;
            out->type = W_SHAPE;
            return 0;
        }
    }

    /* 折线：直接用 lv_line */
    if (kind == SH_POLYLINE) {
        if (shape_read_points(props, out) != 0) return -1;
        o = lv_line_create(ui_cur_parent());
        if (!o) return -1;
        ui_place(o, x, y, ww, hh);
        lv_obj_set_style_line_width(o, bw > 0 ? bw : 2, 0);
        lv_obj_set_style_line_color(o, ui_color_hex(border), 0);
        lv_obj_set_style_line_rounded(o, true, 0);
        lv_line_set_points(o, out->u.shape.poly_pts, (uint32_t)out->u.shape.poly_n);
        out->obj = o;
        out->type = W_SHAPE;
        return 0;
    }

    /* 多边形 / 椭圆 / 扇形：自绘 */
    if (kind == SH_POLYGON || kind == SH_ELLIPSE || kind == SH_PIE) {
        if (kind == SH_POLYGON) {
            if (shape_read_points(props, out) != 0) return -1;
        } else {
            int a0 = (int)hmi_json_int(hmi_json_get(props, "angle_start"), 0);
            int a1 = (int)hmi_json_int(hmi_json_get(props, "angle_end"), 360);

            if (shape_gen_ellipse((int)ww, (int)hh, a0, a1, out) != 0) return -1;
            if (kind == SH_PIE && shape_pie_close((int)ww, (int)hh, out) != 0) return -1;
        }
        return shape_make_custom(x, y, ww, hh, out);
    }

    /* 多边形族 / 星形 / 十字 / 楔形：顶点由 kind 参数生成，同一条自绘路径 */
    if ((kind >= SH_PARALLELOGRAM && kind <= SH_CROSS) || kind == SH_WEDGE ||
        (kind >= SH_ARROW && kind <= SH_ARROW_THIN)) {
        if (shape_gen_poly(kind, props, (int)ww, (int)hh, out) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    }

    /* 复合块：注释框是单一路径；立方体逐面填充 */
    if (kind == SH_CALLOUT) {
        if (shape_gen_callout(props, (int)ww, (int)hh, out) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    }
    if (kind == SH_CUBE) {
        if (shape_gen_cube(props, (int)ww, (int)hh, out, out->u.shape.shape_fill) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    }

    /* 三维三件：分块渐变近似光照，light 定高光方向 */
    switch (kind) {
    case SH_BALL3D:
        if (shape_gen_ball3d(props, (int)ww, (int)hh, out, out->u.shape.shape_fill) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_TORUS3D:
        if (shape_gen_torus3d(props, (int)ww, (int)hh, out, out->u.shape.shape_fill) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_CONE3D:
        if (shape_gen_cone3d(props, (int)ww, (int)hh, out, out->u.shape.shape_fill) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    default:
        break;
    }

    /* 工艺件：管道 4 件 / 罐形 / 平面 3 件 */
    switch (kind) {
    case SH_PIPE_H:
    case SH_PIPE_V:
        if (shape_gen_pipe(props, (int)ww, (int)hh, out, out->u.shape.shape_fill,
                           kind == SH_PIPE_V) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_PIPE_ELBOW:
        if (shape_gen_elbow(props, (int)ww, (int)hh, out, out->u.shape.shape_fill) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_PIPE_JOINT:
        if (shape_gen_joint(props, (int)ww, (int)hh, out, out->u.shape.shape_fill) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_TANK:
        if (shape_gen_tank(props, (int)ww, (int)hh, out, out->u.shape.shape_fill) != 0) return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_VALVE_BALL:
        if (shape_gen_valve(props, (int)ww, (int)hh, out, out->u.shape.shape_fill, 0) != 0)
            return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_VALVE_BUTTERFLY:
        if (shape_gen_valve(props, (int)ww, (int)hh, out, out->u.shape.shape_fill, 1) != 0)
            return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_VALVE_GATE:
        if (shape_gen_valve(props, (int)ww, (int)hh, out, out->u.shape.shape_fill, 2) != 0)
            return -1;
        return shape_make_custom(x, y, ww, hh, out);
    case SH_PLANE_CONCAVE:
    case SH_PLANE_CONVEX:
    case SH_PLANE_GROOVE: {
        int mode = (kind == SH_PLANE_CONCAVE) ? 0
                 : (kind == SH_PLANE_CONVEX) ? 1 : 2;

        if (shape_gen_plane(props, (int)ww, (int)hh, out, out->u.shape.shape_fill, mode) != 0)
            return -1;
        return shape_make_custom(x, y, ww, hh, out);
    }
    default:
        break;
    }

    /* rect / round_rect / circle / container：样式即可 */
    o = lv_obj_create(ui_cur_parent());
    if (!o) return -1;
    ui_place(o, x, y, ww, hh);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_style_bg_color(o, ui_color_hex(fill), 0);
    lv_obj_set_style_bg_opa(o, out->u.shape.have_fill ? (lv_opa_t)fopa : LV_OPA_TRANSP, 0);
    lv_obj_set_style_radius(o, radius, 0);
    lv_obj_set_style_border_color(o, ui_color_hex(border), 0);
    lv_obj_set_style_border_width(o, (int32_t)bw, 0);
    if (kind == SH_CIRCLE) lv_obj_set_style_radius(o, LV_RADIUS_CIRCLE, 0);
    else if (kind == SH_LINE) {
        /* 兼容原契约：kind=line 是一条细线（填充的窄矩形），不是折线 */
        lv_obj_set_style_border_width(o, 0, 0);
        lv_obj_set_style_radius(o, 0, 0);
    } else if (kind == SH_ROUND_RECT) lv_obj_set_style_radius(o, radius > 0 ? radius : 8, 0);
    else if (kind == SH_CONTAINER) {
        lv_obj_set_style_bg_opa(o, LV_OPA_TRANSP, 0);
        if (bw == 0) lv_obj_set_style_border_width(o, 1, 0);
    }
    out->obj = o;
    out->type = W_SHAPE;
    return 0;
}
