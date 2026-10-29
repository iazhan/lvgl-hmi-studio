/* 生成物：由 tools/gen_schema.py 从 spec/widget-schema.json 生成，不要手改。 */
#ifndef HMI_WIDGETS_SCHEMA_H
#define HMI_WIDGETS_SCHEMA_H

typedef struct {
    const char* id;
    const char* const* props;
} schema_props_t;

static const char* const SC_COMMON[] = { "x", "y", "w", "h", "visible", "enabled", "opacity", "rotation", "mirror_x", "mirror_y", "z", "bg_color", "border_color", "border_width", "radius", "perm", "tooltip", "states", NULL };

static const char* const SC_FIGURE_COMMON[] = { "fill_color", "fill_opa", "border_color", "border_width", "radius", "points", "angle_start", "angle_end", "shade", "static", NULL };

static const char* const SC_LABEL[] = { "valign", "text", "font_size", "color", "align", "font_style", "line_spacing", "letter_spacing", "long_mode", "wrap", "decimals", NULL };
static const char* const SC_VALUE[] = { "valign", "decimals", "unit", "prefix", "suffix", "align", "font_size", "color", NULL };
static const char* const SC_ENUM_TEXT[] = { "valign", "map", "text", "align", "font_size", "color", NULL };
static const char* const SC_BUTTON[] = { "text", "font_size", "color", "pressed", "hold_ms", "long_press_ms", "off_style", "confirm", NULL };
static const char* const SC_SWITCH[] = { "color_on", "color_off", "text_on", "text_off", "knob_color", NULL };
static const char* const SC_CHECKBOX[] = { "text", "font_size", "color", "checked", "box_color", NULL };
static const char* const SC_SPINNER[] = { "color", "arc_width", "arc_len", "speed_ms", NULL };
static const char* const SC_KEYBOARD[] = { "target", "title", NULL };
static const char* const SC_TABVIEW[] = { "tabs", "tab_pos", "tab_h", NULL };
static const char* const SC_KNOB[] = { "min", "max", "step", "color", "arc_width", "decimals", "show_value", NULL };
static const char* const SC_LAMP[] = { "color", "ranges", "size", "brightness", NULL };
static const char* const SC_BAR[] = { "direction", "min", "max", "color", "ranges", "target", "show_percent", "show_value", "decimals", NULL };
static const char* const SC_GAUGE[] = { "style", "min", "max", "color", "ticks", "tick_len", "arc_width", "ranges", "needle_len", "needle_width", "center_radius", "decimals", "show_value", "label_pos", NULL };
static const char* const SC_INPUT[] = { "mode", "min", "max", "decimals", "max_len", "step", "commit", "font_size", "align", NULL };
static const char* const SC_SLIDER[] = { "min", "max", "step", "color", "show_value", NULL };
static const char* const SC_COMBOBOX[] = { "options", "options_tag", "font_size", "color", "selected", NULL };
static const char* const SC_IMAGE[] = { "src", "scale", NULL };
static const char* const SC_SHAPE[] = { "kind", "children", NULL };
static const char* const SC_STATE_IMAGE[] = { "images", NULL };
static const char* const SC_TABLE[] = { "row_h", "col_w", "rows", "cols", "header", "font_size", "cells", NULL };
static const char* const SC_QRCODE[] = { "text", "dark_color", "light_color", NULL };
static const char* const SC_PIPE[] = { "direction", "flow_dir", "points", "wall", "width", "color", "channel_color", "wall_color", "flow_ms", NULL };
static const char* const SC_FRAME_ANIM[] = { "frames", "interval_ms", "loop", NULL };
static const char* const SC_REALTIME_CURVE[] = { "curves", "sample_ms", "span_s", "history_s", "y_min", "y_max", "y2_min", "y2_max", "y_decimals", "y_digits", "y2_decimals", "y2_digits", "y_div", "y_minor", "x_div", "x_minor", "x_format", "x_label_step", "grid", "legend", "axis_color", "grid_color", "tick_color", "label_color", "bg_color", "border_color", "font_size", "show_axis", "show_ticks", "show_labels", "direction", NULL };
static const char* const SC_HISTORY_CURVE[] = { "group", "range", "refresh_ms", "curves", "span_s", "x_format", "x_label_step", "y_min", "y_max", "y2_min", "y2_max", "y_decimals", "y_digits", "y2_decimals", "y2_digits", "y_div", "y_minor", "x_div", "x_minor", "grid", "legend", "show_axis", "show_ticks", "show_labels", "direction", "font_size", "axis_color", "grid_color", "tick_color", "label_color", "bg_color", "border_color", NULL };
static const char* const SC_XY_CURVE[] = { "x", "y", "sample_ms", "span_s", "dot", "line", "x_min", "x_max", "y_min", "y_max", "grid", "x_div", "y_div", "x_minor", "y_minor", "x_decimals", "y_decimals", "font_size", "axis_color", "grid_color", "tick_color", "label_color", NULL };
static const char* const SC_HISTORY_TABLE[] = { "source", "columns", "row_height", "max_rows", "font_size", "scrollbar", "refresh_ms", NULL };
static const char* const SC_ALARM_BAR[] = { "source", "only_unacked", "scroll", "scroll_ms", "scroll_chars", "blink", "blink_ms", "show_date", "show_time", "date_format", "time_format", "order", "normal_text", "font_size", "color", NULL };
static const char* const SC_ALARM_LIST[] = { "source", "mode", "filter", "columns", "group_by", "ack_on", "blink", "blink_ms", "order", "grid", "max_rows", "row_height", "scrollbar", "refresh_ms", "date_format", "time_format", "font_size", NULL };
static const char* const SC_RECIPE_TABLE[] = { "source", "columns", "show_unit", "row_height", "scrollbar", "font_size", NULL };
static const char* const SC_CHART[] = { "kind", "series", "show_value", "show_legend", "y_min", "y_max", "y_decimals", NULL };
static const char* const SC_MESSAGE[] = { "text", "level", "auto_hide_ms", "align", "marquee", NULL };
static const char* const SC_ROLLER[] = { "options", "visible_rows", "font_size", NULL };
static const char* const SC_ASCII_DISPLAY[] = { "valign", "max_len", "encoding", "align", "font_size", "color", NULL };

static const char* const SF_RECT[] = { NULL, NULL };
static const char* const SF_ROUND_RECT[] = { NULL, NULL };
static const char* const SF_CIRCLE[] = { NULL, NULL };
static const char* const SF_ELLIPSE[] = { NULL, NULL };
static const char* const SF_ARC[] = { "min", "max", NULL };
static const char* const SF_PIE[] = { NULL, NULL };
static const char* const SF_POLYGON[] = { NULL, NULL };
static const char* const SF_POLYLINE[] = { NULL, NULL };
static const char* const SF_LINE[] = { NULL, NULL };
static const char* const SF_CONTAINER[] = { "children", "layout", NULL };
static const char* const SF_PARALLELOGRAM[] = { "skew", NULL };
static const char* const SF_TRAPEZOID[] = { "top_w", NULL };
static const char* const SF_DIAMOND[] = { NULL, NULL };
static const char* const SF_OCTAGON[] = { NULL, NULL };
static const char* const SF_HEXAGON[] = { NULL, NULL };
static const char* const SF_TRIANGLE[] = { NULL, NULL };
static const char* const SF_RIGHT_TRIANGLE[] = { "flip", NULL };
static const char* const SF_STAR5[] = { "inner_ratio", NULL };
static const char* const SF_STAR[] = { "spikes", "inner_ratio", NULL };
static const char* const SF_CROSS[] = { "thickness", NULL };
static const char* const SF_CALLOUT[] = { "tail", "tail_size", NULL };
static const char* const SF_CUBE[] = { "depth", "light", NULL };
static const char* const SF_WEDGE[] = { "tip", NULL };
static const char* const SF_TANK[] = { "wall", "flare", "top", NULL };
static const char* const SF_PIPE_H[] = { "wall", NULL };
static const char* const SF_PIPE_V[] = { "wall", NULL };
static const char* const SF_PIPE_ELBOW[] = { "bend", "wall", NULL };
static const char* const SF_PIPE_JOINT[] = { "flaps", NULL };
static const char* const SF_VALVE_BALL[] = { "open", NULL };
static const char* const SF_VALVE_BUTTERFLY[] = { "open", NULL };
static const char* const SF_VALVE_GATE[] = { "open", NULL };
static const char* const SF_PLANE_CONCAVE[] = { "light", NULL };
static const char* const SF_PLANE_CONVEX[] = { "light", NULL };
static const char* const SF_PLANE_GROOVE[] = { "groove_w", NULL };
static const char* const SF_ARROW[] = { "head", "direction", NULL };
static const char* const SF_ARROW_THICK[] = { "head", "direction", NULL };
static const char* const SF_ARROW_THIN[] = { "head", "direction", NULL };
static const char* const SF_BALL3D[] = { "light", NULL };
static const char* const SF_TORUS3D[] = { "thickness", "light", NULL };
static const char* const SF_CONE3D[] = { "light", NULL };

static const schema_props_t SC_WIDGETS[] = {
    { "label", SC_LABEL },
    { "value", SC_VALUE },
    { "enum_text", SC_ENUM_TEXT },
    { "button", SC_BUTTON },
    { "switch", SC_SWITCH },
    { "checkbox", SC_CHECKBOX },
    { "spinner", SC_SPINNER },
    { "keyboard", SC_KEYBOARD },
    { "tabview", SC_TABVIEW },
    { "knob", SC_KNOB },
    { "lamp", SC_LAMP },
    { "bar", SC_BAR },
    { "gauge", SC_GAUGE },
    { "input", SC_INPUT },
    { "slider", SC_SLIDER },
    { "combobox", SC_COMBOBOX },
    { "image", SC_IMAGE },
    { "shape", SC_SHAPE },
    { "state_image", SC_STATE_IMAGE },
    { "table", SC_TABLE },
    { "qrcode", SC_QRCODE },
    { "pipe", SC_PIPE },
    { "frame_anim", SC_FRAME_ANIM },
    { "realtime_curve", SC_REALTIME_CURVE },
    { "history_curve", SC_HISTORY_CURVE },
    { "xy_curve", SC_XY_CURVE },
    { "history_table", SC_HISTORY_TABLE },
    { "alarm_bar", SC_ALARM_BAR },
    { "alarm_list", SC_ALARM_LIST },
    { "recipe_table", SC_RECIPE_TABLE },
    { "chart", SC_CHART },
    { "message", SC_MESSAGE },
    { "roller", SC_ROLLER },
    { "ascii_display", SC_ASCII_DISPLAY },
};

static const schema_props_t SC_FIGURES[] = {
    { "rect", SF_RECT },
    { "round_rect", SF_ROUND_RECT },
    { "circle", SF_CIRCLE },
    { "ellipse", SF_ELLIPSE },
    { "arc", SF_ARC },
    { "pie", SF_PIE },
    { "polygon", SF_POLYGON },
    { "polyline", SF_POLYLINE },
    { "line", SF_LINE },
    { "container", SF_CONTAINER },
    { "parallelogram", SF_PARALLELOGRAM },
    { "trapezoid", SF_TRAPEZOID },
    { "diamond", SF_DIAMOND },
    { "octagon", SF_OCTAGON },
    { "hexagon", SF_HEXAGON },
    { "triangle", SF_TRIANGLE },
    { "right_triangle", SF_RIGHT_TRIANGLE },
    { "star5", SF_STAR5 },
    { "star", SF_STAR },
    { "cross", SF_CROSS },
    { "callout", SF_CALLOUT },
    { "cube", SF_CUBE },
    { "wedge", SF_WEDGE },
    { "tank", SF_TANK },
    { "pipe_h", SF_PIPE_H },
    { "pipe_v", SF_PIPE_V },
    { "pipe_elbow", SF_PIPE_ELBOW },
    { "pipe_joint", SF_PIPE_JOINT },
    { "valve_ball", SF_VALVE_BALL },
    { "valve_butterfly", SF_VALVE_BUTTERFLY },
    { "valve_gate", SF_VALVE_GATE },
    { "plane_concave", SF_PLANE_CONCAVE },
    { "plane_convex", SF_PLANE_CONVEX },
    { "plane_groove", SF_PLANE_GROOVE },
    { "arrow", SF_ARROW },
    { "arrow_thick", SF_ARROW_THICK },
    { "arrow_thin", SF_ARROW_THIN },
    { "ball3d", SF_BALL3D },
    { "torus3d", SF_TORUS3D },
    { "cone3d", SF_CONE3D },
};

/* 控件依赖的工程能力：manifest.features 必须声明，否则建页期报错 */
static const struct { const char* id; const char* feature; } SC_WIDGET_REQUIRES[] = {
    { "state_image", "widgets_ext" },
    { "table", "table" },
    { "qrcode", "widgets_ext" },
    { "pipe", "pipe" },
    { "frame_anim", "widgets_ext" },
    { "realtime_curve", "curves" },
    { "history_curve", "curves" },
    { "xy_curve", "curves" },
    { "history_table", "table" },
    { "alarm_bar", "alarm" },
    { "alarm_list", "alarm" },
    { "recipe_table", "recipe" },
    { "chart", "widgets_ext" },
    { "message", "widgets_ext" },
    { "roller", "widgets_ext" },
    { "ascii_display", "widgets_ext" },
};

#endif /* HMI_WIDGETS_SCHEMA_H */
