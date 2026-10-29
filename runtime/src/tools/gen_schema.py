#!/usr/bin/env python3
"""从 spec/widget-schema.json 生成两样东西（schema 是唯一真源，这里只做投影）：

  docs/chenge-rt/37-控件属性表.md        人读的属性表（不手改）
  src/ui/widgets_schema.h                   loader 用的属性白名单（不手改）

用法：
  python3 tools/gen_schema.py            生成（覆盖）
  python3 tools/gen_schema.py --check    只比对，不一致就退出码 1（回归里用）

约定见 docs/adr/0004（schema 为唯一真源）与 docs/adr/0006（冻结口径）。
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RT = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(RT))
SCHEMA = os.path.join(REPO, 'spec', 'widget-schema.json')
DOC = os.path.join(REPO, 'docs', 'chenge-rt', '37-控件属性表.md')
HDR = os.path.join(RT, 'src', 'ui', 'widgets_schema.h')

BANNER = ('<!-- 本文件由 chenge-hmi/chenge-runtime/tools/gen_schema.py 从 '
          'spec/widget-schema.json 生成，不要手改。 -->')


def prop_line(name, p):
    t = p.get('type', '?')
    bits = [t]
    if p.get('default') is not None:
        d = p['default']
        bits.append('缺省 `%s`' % json.dumps(d, ensure_ascii=False))
    if p.get('min') is not None or p.get('max') is not None:
        lo = p.get('min', '')
        hi = p.get('max', '')
        bits.append('%s..%s' % (lo, hi))
    if p.get('enum'):
        bits.append(' / '.join('`%s`' % e for e in p['enum']))
    if p.get('status') == 'planned':
        bits.append('**未实现**')
    if p.get('requires'):
        bits.append('需 `%s`' % p['requires'])
    desc = p.get('desc', '')
    return '| `%s` | %s | %s |' % (name, '，'.join(bits), desc)


def gen_doc(schema):
    out = [BANNER, '',
           '# 37 附：控件与图元属性表（生成物）', '',
           '本表由 `spec/widget-schema.json` 生成。契约冻结口径见 `docs/adr/0006`，'
           '每一条为什么这么定见 `docs/chenge-rt/44`。', '',
           '共 **%d 种控件**、**%d 种图元**、**%d 项公共属性**。' % (
               schema['counts']['widgets'], schema['counts']['figures'],
               schema['counts']['common']), '']

    out += ['## 公共属性（所有控件与图元都有）', '',
            '| 属性 | 类型/范围 | 说明 |', '| --- | --- | --- |']
    for name, p in schema['common'].items():
        out.append(prop_line(name, p))
    out.append('')

    groups = {}
    for wid, w in schema['widgets'].items():
        groups.setdefault(w.get('group', '其它'), []).append(wid)

    for g in ('基础', '扩展', '数据'):
        if g not in groups:
            continue
        out += ['## %s控件（%d 种）' % (g, len(groups[g])), '']
        for wid in groups[g]:
            w = schema['widgets'][wid]
            flag = '' if w.get('status') != 'planned' else '（**未实现**）'
            out += ['### `%s` %s%s' % (wid, w['title'], flag), '']
            if w.get('requires'):
                out += ['需 `%s` 能力：用它的工程必须在 `manifest.features` 里声明，否则建页期报错。' % w['requires'], '']
            if w.get('props'):
                out += ['| 属性 | 类型/范围 | 说明 |', '| --- | --- | --- |']
                for name, p in w['props'].items():
                    out.append(prop_line(name, p))
                out.append('')
            if w.get('bind'):
                out.append('可绑定：' + '、'.join('`%s`' % b for b in w['bind']))
                out.append('')
            if w.get('events'):
                out.append('事件：' + '、'.join('`%s`' % e for e in w['events']))
                out.append('')

    out += ['## 图元通用属性（37 种共用）', '',
            '| 属性 | 类型/范围 | 说明 |', '| --- | --- | --- |']
    for name, p in schema['figure_common'].items():
        out.append(prop_line(name, p))
    out.append('')
    out += ['## 图元种类与专属参数', '',
            '| kind | 名称 | 专属参数 |', '| --- | --- | --- |']
    for kind, f in schema['figures'].items():
        extra = '、'.join('`%s`' % k for k in f.get('props', {})) or '—'
        out.append('| `%s` | %s | %s |' % (kind, f['title'], extra))
    out.append('')
    for kind, f in schema['figures'].items():
        if not f.get('props'):
            continue
        out += ['### `%s` %s 的专属参数' % (kind, f['title']), '',
                '| 属性 | 类型/范围 | 说明 |', '| --- | --- | --- |']
        for name, p in f['props'].items():
            out.append(prop_line(name, p))
        out.append('')

    out += ['## 事件', '', '| 事件 | 说明 |', '| --- | --- |']
    for k, v in schema['events'].items():
        out.append('| `%s` | %s |' % (k, v['desc']))
    out += ['', '## 动作', '', '| 动作 | 状态 | 参数 | 说明 |', '| --- | --- | --- | --- |']
    for k, v in schema['actions'].items():
        st = '已实现' if v.get('status') == 'done' else '**未实现**（需 %s）' % v.get('requires', '?')
        out.append('| `%s` | %s | %s | %s |' % (
            k, st, '、'.join(v.get('params', [])) or '—', v['desc']))
    out += ['', '明确不支持：']
    for k, why in schema['actions_not_supported'].items():
        out.append('- `%s`：%s' % (k, why))
    out.append('')
    out += ['## 变量（tag）', '', '| 字段 | 类型/范围 | 说明 |', '| --- | --- | --- |']
    for name, p in schema['tag'].items():
        out.append('| `%s` | %s | %s |' % (
            name, p.get('type', ''), p.get('desc', '')))
    out.append('')
    return '\n'.join(out)


def c_array(name, keys):
    items = ', '.join('"%s"' % k for k in keys) or 'NULL'
    return 'static const char* const %s[] = { %s, NULL };\n' % (name, items)


def gen_header(schema):
    out = ['/* 生成物：由 tools/gen_schema.py 从 spec/widget-schema.json 生成，不要手改。 */\n',
           '#ifndef HMI_WIDGETS_SCHEMA_H\n#define HMI_WIDGETS_SCHEMA_H\n\n',
           'typedef struct {\n    const char* id;\n    const char* const* props;\n',
           '} schema_props_t;\n\n']
    out.append(c_array('SC_COMMON', list(schema['common'].keys())))
    out.append('\n')
    out.append(c_array('SC_FIGURE_COMMON', list(schema['figure_common'].keys())))
    out.append('\n')
    names = []
    for wid, w in schema['widgets'].items():
        arr = 'SC_%s' % wid.upper()
        names.append((wid, arr))
        out.append(c_array(arr, list(w.get('props', {}).keys())))
    out.append('\n')
    fnames = []
    for kind, f in schema['figures'].items():
        arr = 'SF_%s' % kind.upper()
        fnames.append((kind, arr))
        out.append(c_array(arr, list(f.get('props', {}).keys())))
    out.append('\nstatic const schema_props_t SC_WIDGETS[] = {\n')
    for wid, arr in names:
        out.append('    { "%s", %s },\n' % (wid, arr))
    out.append('};\n\nstatic const schema_props_t SC_FIGURES[] = {\n')
    for kind, arr in fnames:
        out.append('    { "%s", %s },\n' % (kind, arr))
    out.append('};\n')
    reqs = [(wid, w['requires']) for wid, w in schema['widgets'].items() if w.get('requires')]
    out.append('\n/* 控件依赖的工程能力：manifest.features 必须声明，否则建页期报错 */\n')
    out.append('static const struct { const char* id; const char* feature; } '
               'SC_WIDGET_REQUIRES[] = {\n')
    for wid, feat in reqs:
        out.append('    { "%s", "%s" },\n' % (wid, feat))
    out.append('};\n\n#endif /* HMI_WIDGETS_SCHEMA_H */\n')
    return ''.join(out)


def editor_copy_paths():
    """编辑器（另一个仓库）里那份**嵌入副本**的可能位置。

    导出器用 `ChengeSchema.AllowedProps` 按这份副本**过滤** props（不在副本里的键
    会被丢掉并告警），所以副本一旦落后于 spec，新属性就永远进不了产物 ——
    设计画布看到的现象是"模型里有顶点、画布上却按老样子画"（pipe.points 踩过）。
    """
    env = os.environ.get('CHENGE_EDITOR_COPY')
    if env:
        return [env]
    studio = os.path.abspath(os.path.join(REPO, '..', 'lvgl-hmi-studio'))
    return [os.path.join(studio, 'src', 'HmiEditor.Export.Chenge', 'Schema', 'widget-schema.json')]


def check_editor_copy(schema):
    """比对编辑器副本与 spec 的**属性键集**（描述文字不参与比对）。"""
    path = next((p for p in editor_copy_paths() if os.path.exists(p)), None)
    if path is None:
        print('编辑器嵌入副本未找到（外部仓库），跳过')
        return 0
    try:
        copy = json.load(open(path, encoding='utf-8'))
    except (OSError, ValueError) as e:
        print('!! 读不了编辑器副本 %s：%s' % (path, e))
        return 1
    bad = []
    for section in ('common', 'figure_common'):
        a = set(schema.get(section, {}))
        b = set(copy.get(section, {}))
        if a != b:
            bad.append('%s: 缺 %s / 多 %s' % (section, sorted(a - b), sorted(b - a)))
    for wtype, w in schema['widgets'].items():
        a = set(w.get('props', {}))
        b = set(copy.get('widgets', {}).get(wtype, {}).get('props', {}))
        if a != b:
            bad.append('widgets.%s: 缺 %s / 多 %s' % (wtype, sorted(a - b), sorted(b - a)))
    for ftype, f in schema.get('figures', {}).items():
        a = set(f.get('props', {}))
        b = set(copy.get('figures', {}).get(ftype, {}).get('props', {}))
        if a != b:
            bad.append('figures.%s: 缺 %s / 多 %s' % (ftype, sorted(a - b), sorted(b - a)))
    if bad:
        for line in bad[:20]:
            print('!! 编辑器副本落后于 spec —— %s' % line)
        print('  把 spec/widget-schema.json 覆盖到 %s' % path)
        return 1
    print('编辑器嵌入副本与 spec 的属性键一致')
    return 0


def main():
    check = '--check' in sys.argv
    schema = json.load(open(SCHEMA, encoding='utf-8'))
    doc = gen_doc(schema)
    hdr = gen_header(schema)

    bad = []
    for path, content in ((DOC, doc), (HDR, hdr)):
        old = open(path, encoding='utf-8').read() if os.path.exists(path) else None
        if check:
            if old != content:
                bad.append(path)
        else:
            with open(path, 'w', encoding='utf-8') as f:
                f.write(content)

    if check:
        if bad:
            for p in bad:
                print('!! %s 与 schema 不一致，跑 tools/gen_schema.py 重新生成' % p)
            return 1
        rc = check_editor_copy(schema)
        if rc != 0:
            return rc
        print('schema 与生成物一致')
        return 0
    print('已生成 %s 与 %s' % (DOC, HDR))
    return 0


if __name__ == '__main__':
    sys.exit(main())
