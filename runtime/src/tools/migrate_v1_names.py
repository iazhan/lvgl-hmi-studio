#!/usr/bin/env python3
"""一次性迁移：把工程文件里的旧属性名改成契约 v1 的名字（ADR-0006 定的一次性改名）。

  props.bg / page.bg      → bg_color
  props.fill              → fill_color
  曲线 props.points        → span_s（秒）= points * sample_ms / 1000
  曲线 props.history_points→ history_s（秒）
  history_curve span_min  → range: {last_min: N}

用法：python3 tools/migrate_v1_names.py <工程目录>...
"""
import json
import os
import sys


def convert_widget(w):
    props = w.get('props') or {}
    if 'bg' in props:
        props['bg_color'] = props.pop('bg')
    if 'fill' in props:
        props['fill_color'] = props.pop('fill')
    if 'points' in props and isinstance(props['points'], int):
        sample = props.get('sample_ms', 1000)
        props['span_s'] = max(1, int(round(props['points'] * sample / 1000.0)))
        del props['points']
    if 'history_points' in props:
        sample = props.get('sample_ms', 1000)
        props['history_s'] = max(0, int(round(props.pop('history_points') * sample / 1000.0)))
    if 'span_min' in props:
        props['range'] = {'last_min': props.pop('span_min')}
    if 'start_angle' in props:
        props['angle_start'] = props.pop('start_angle')
    if 'end_angle' in props:
        props['angle_end'] = props.pop('end_angle')
    pressed = props.get('pressed')
    if isinstance(pressed, dict) and 'bg' in pressed:
        pressed['bg_color'] = pressed.pop('bg')
    for c in props.get('children', []) or []:
        convert_widget(c)


def main():
    n = 0
    for root in sys.argv[1:]:
        pages = os.path.join(root, 'pages')
        if not os.path.isdir(pages):
            continue
        for name in sorted(os.listdir(pages)):
            if not name.endswith('.json'):
                continue
            p = os.path.join(pages, name)
            d = json.load(open(p, encoding='utf-8'))
            changed = False
            if 'bg' in d:
                d['bg_color'] = d.pop('bg')
                changed = True
            for w in d.get('widgets', []):
                before = json.dumps(w, sort_keys=True, ensure_ascii=False)
                convert_widget(w)
                if json.dumps(w, sort_keys=True, ensure_ascii=False) != before:
                    changed = True
            if changed:
                with open(p, 'w', encoding='utf-8') as f:
                    json.dump(d, f, ensure_ascii=False, indent=2)
                    f.write('\n')
                n += 1
                print('改了', p)
    print('共 %d 个页面文件' % n)


if __name__ == '__main__':
    main()
