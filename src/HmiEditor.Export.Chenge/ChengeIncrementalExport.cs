using System.Text;
using System.Text.Json.Nodes;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 增量重载包的语义：宿主 <c>K</c> 是"把这些文件并进它手里那套工程"（暂存表覆盖活动表，
/// 没推的文件保持原样），所以"推哪几个文件"就等于"哪几份内容算新的"。
///
/// <para><b>缺页 = 该页仍是旧内容</b>：页面 JSON 里存的是 tag 序号，元数据一换（变量增删/
/// 换序/改类型），没推的页就会让绑定指到别的变量上或指向不存在的 tag。所以调用方必须先算出
/// 哪些页的 JSON 会变（<see cref="StalePageIds"/>），判不出来就带全部页 —— 正确 &gt; 省。</para>
///
/// <para><b>缺元数据文件 = 宿主继续用旧的</b>：这里补的中性空文档就是给"某项配置被删空"
/// 用的（比如报警全删了）。不补的话宿主手里那份 alarms.json 还在，旧报警照样会响。</para>
/// </summary>
public static class ChengeIncrementalExport
{
    /// <summary>必须在包里出现的元数据文件：缺了就是"沿用宿主旧的那份"，语义与整包不同。</summary>
    private static readonly Dictionary<string, string> EmptyDocuments = new(StringComparer.Ordinal)
    {
        ["alarms.json"] = """{"alarms":[]}""",
        // languages 必须 1..N 项（运行时 i18n_load 的判据），texts 可以为空
        ["strings.json"] = """{"default":"zh","languages":[{"code":"zh","name":"简体中文"}],"texts":[]}""",
        ["savecfg.json"] = """{"groups":[]}""",
    };

    /// <summary>
    /// 把"这次导出没产出"的元数据文件补成中性空文档。
    /// 只补 <see cref="EmptyDocuments"/> 里那几项：它们的存在与否直接改变宿主行为（报警是否
    /// 在跑、存盘组是否在写），不像 tags/devices 那样每次都必然重新产出。
    /// </summary>
    public static void FillMissingMetaFiles(Dictionary<string, byte[]> payload)
    {
        foreach (var (relative, json) in EmptyDocuments)
        {
            if (!payload.ContainsKey(relative)) payload[relative] = Encoding.UTF8.GetBytes(json);
        }
    }

    /// <summary>
    /// 从导出的页面 JSON 里取出它引用到的全部 tag id。
    ///
    /// <para><b>为什么读产物而不是读模型</b>：能引用变量的地方散在几十种控件的私有属性里
    /// （值/柱/仪表/曲线的 curves[]、表格列、XY 轴、图表系列、脚本…）。在编辑器模型上枚举
    /// 等于把 <see cref="ChengeWidgetMapper"/> 抄一遍，抄漏一处就是"该页没带上、切页读到旧
    /// 绑定"。产物是导出器自己写出来的，键名就那几种。</para>
    /// </summary>
    public static HashSet<int> TagIdsOf(byte[] pageJson)
    {
        var ids = new HashSet<int>();
        if (pageJson.Length == 0) return ids;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(pageJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return ids;
        }
        Collect(root, ids);
        return ids;
    }

    private static void Collect(JsonNode? node, HashSet<int> ids)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (LooksLikeTagKey(key))
                    {
                        AddNumbers(value, ids);
                        // tag 列表（tags:[1,2]）之外不再递归：tag 值本身没有嵌套 tag
                        continue;
                    }
                    Collect(value, ids);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) Collect(item, ids);
                break;
        }
    }

    private static bool LooksLikeTagKey(string key)
    {
        if (key.Equals("tag", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("tags", StringComparison.OrdinalIgnoreCase))
            return true;
        return key.StartsWith("tag_", StringComparison.OrdinalIgnoreCase)
               || key.EndsWith("_tag", StringComparison.OrdinalIgnoreCase)
               || key.EndsWith("_tags", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddNumbers(JsonNode? node, HashSet<int> ids)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<int>(out var one):
                ids.Add(one);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (item is JsonValue v && v.TryGetValue<int>(out var many)) ids.Add(many);
                break;
        }
    }

    /// <summary>
    /// 一份导出的工程文件 → 每页引用的 tag id（页 id 取文件名）。
    ///
    /// <para>调用方（设计画布 / 自检）在整包导出之后立刻记一份，元数据变化时才有"上一轮这页
    /// 引用了什么"可比。</para>
    /// </summary>
    public static Dictionary<string, HashSet<int>> PageTagRefsOf(IReadOnlyDictionary<string, byte[]> files)
    {
        var refs = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var (relative, bytes) in files)
        {
            if (!relative.StartsWith("pages/", StringComparison.Ordinal)) continue;
            if (!relative.EndsWith(".json", StringComparison.Ordinal)) continue;
            refs[relative["pages/".Length..^".json".Length]] = TagIdsOf(bytes);
        }
        return refs;
    }

    /// <summary>
    /// 哪些页的 JSON 会因为 tag 表变化而过期。
    ///
    /// <para>判据：页里引用到的 tag 序号，在旧/新表里指向的不是同一个变量（名字或类型变了），
    /// 或者那个序号已经不存在了（变量被删）。序号整体错位（换序、删中间的变量）自然会被逐位
    /// 比较捕捉到。</para>
    ///
    /// <para>返回 <c>null</c> = 判不出来（没有上一轮的记录、或者有页的引用没记过），调用方应
    /// 带全部页。宁可多带一页，也不能让宿主里留一页旧内容还宣称一致。</para>
    /// </summary>
    public static HashSet<string>? StalePageIds(
        IReadOnlyList<string> oldFingerprints,
        IReadOnlyList<string> newFingerprints,
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> refsByPageId)
    {
        if (oldFingerprints.Count == 0 || newFingerprints.Count == 0 || refsByPageId.Count == 0)
            return null;

        var stale = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pageId, refs) in refsByPageId)
        {
            foreach (var tag in refs)
            {
                if (tag < 0 || tag >= newFingerprints.Count)
                {
                    stale.Add(pageId);          // 序号背后的变量没了
                    break;
                }
                if (tag >= oldFingerprints.Count) return null;   // 上一轮没有这个序号的记录
                if (!string.Equals(oldFingerprints[tag], newFingerprints[tag], StringComparison.Ordinal))
                {
                    stale.Add(pageId);          // 序号还在，但换了变量
                    break;
                }
            }
        }
        return stale;
    }
}
