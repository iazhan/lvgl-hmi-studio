using System.Reflection;
using System.Text.Json;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 导出侧读的控件契约（`spec/widget-schema.json` 的嵌入副本）。
///
/// <para><b>为什么导出器必须读这份 schema，而不是自己列属性名</b>：chenge-runtime 建页时
/// 逐个 prop 对照 `widgets_schema.h`（由同一份 schema 生成）——写一个 schema 里没有的键
/// 直接整页建页失败。手写映射表必然随契约漂移，而漂移的表现是"板上黑屏 + 一行报错"，
/// 定位成本远高于导出时拦住。</para>
///
/// <para>嵌入文件与运行时仓库的 `spec/*.json` 同源；升级契约时替换这四个文件即可
/// （见 MAPPING-REPORT.md 的"契约同步"一节）。</para>
/// </summary>
public sealed class ChengeSchema
{
    private readonly JsonDocument _widgets;
    private readonly JsonDocument _features;
    private readonly JsonDocument _perms;
    private readonly JsonDocument _sysvars;

    public static ChengeSchema Load() => new(
        Read("chenge.widget-schema.json"),
        Read("chenge.features.json"),
        Read("chenge.perms.json"),
        Read("chenge.sysvars.json"));

    private ChengeSchema(JsonDocument widgets, JsonDocument features, JsonDocument perms, JsonDocument sysvars)
    {
        _widgets = widgets;
        _features = features;
        _perms = perms;
        _sysvars = sysvars;
    }

    private static JsonDocument Read(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"嵌入的契约文件缺失：{name}");
        return JsonDocument.Parse(stream);
    }

    public string Version => _widgets.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

    public JsonElement Root => _widgets.RootElement;

    /// <summary>控件类型是否在契约里（`widgets` 段）。</summary>
    public bool HasWidget(string type) =>
        _widgets.RootElement.TryGetProperty("widgets", out var w) && w.TryGetProperty(type, out _);

    public JsonElement Widget(string type) =>
        _widgets.RootElement.GetProperty("widgets").GetProperty(type);

    /// <summary>该控件的可绑定属性清单（`widgets.&lt;type&gt;.bind`）。</summary>
    public bool CanBind(string type, string prop)
    {
        if (!HasWidget(type)) return false;
        if (!Widget(type).TryGetProperty("bind", out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in arr.EnumerateArray())
            if (item.GetString() == prop) return true;
        return false;
    }

    /// <summary>
    /// 建页期允许出现在 `props` 里的键：公共属性 + 控件自有属性；`shape` 还要并上图元
    /// 公共与具体 kind 的属性（运行时的 `schema_check_props` 是同一套规则）。
    /// </summary>
    public HashSet<string> AllowedProps(string type, string? shapeKind)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        AddKeys(set, "common");
        if (HasWidget(type) && Widget(type).TryGetProperty("props", out var props))
            foreach (var p in props.EnumerateObject()) set.Add(p.Name);

        if (type == "shape")
        {
            AddKeys(set, "figure_common");
            var figures = _widgets.RootElement.GetProperty("figures");
            foreach (var fig in figures.EnumerateObject())
            {
                if (fig.Value.TryGetProperty("props", out var fp))
                    foreach (var p in fp.EnumerateObject()) set.Add(p.Name);
            }
        }
        return set;
    }

    private void AddKeys(HashSet<string> set, string section)
    {
        if (!_widgets.RootElement.TryGetProperty(section, out var obj)) return;
        foreach (var p in obj.EnumerateObject()) set.Add(p.Name);
    }

    /// <summary>取属性定义（用于默认值/范围），没有返回 null。</summary>
    public JsonElement? PropDef(string type, string prop)
    {
        if (HasWidget(type) && Widget(type).TryGetProperty("props", out var props) &&
            props.TryGetProperty(prop, out var def))
            return def;
        if (_widgets.RootElement.TryGetProperty("common", out var common) &&
            common.TryGetProperty(prop, out var cdef))
            return cdef;
        return null;
    }

    /// <summary>manifest.features 的合法取值（`spec/features.json`）。</summary>
    public IReadOnlyCollection<string> FeatureVocab
    {
        get
        {
            var list = new List<string>();
            if (_features.RootElement.TryGetProperty("features", out var fs) && fs.ValueKind == JsonValueKind.Object)
                foreach (var f in fs.EnumerateObject()) list.Add(f.Name);
            return list;
        }
    }

    public bool IsKnownFeature(string feature) => FeatureVocab.Contains(feature);

    /// <summary>
    /// 命名权限位（`spec/perms.json` 的 `bits[].name`，与编辑器 `Permission` 枚举逐位一致）。
    /// </summary>
    public IReadOnlyList<string> PermissionKeys
    {
        get
        {
            var list = new List<string>();
            if (_perms.RootElement.TryGetProperty("bits", out var bits) && bits.ValueKind == JsonValueKind.Array)
                foreach (var p in bits.EnumerateArray())
                    if (p.TryGetProperty("name", out var n) && n.GetString() is string s) list.Add(s);
            return list;
        }
    }

    /// <summary>系统变量的运行时 id（tag id 60000 起）；按运行时的名字索引。</summary>
    public IReadOnlyDictionary<string, int> SystemVariableIds
    {
        get
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (_sysvars.RootElement.TryGetProperty("vars", out var vars) && vars.ValueKind == JsonValueKind.Array)
                foreach (var v in vars.EnumerateArray())
                    if (v.TryGetProperty("name", out var n) && n.GetString() is string name &&
                        v.TryGetProperty("id", out var id))
                        map[name] = id.GetInt32();
            return map;
        }
    }
}
