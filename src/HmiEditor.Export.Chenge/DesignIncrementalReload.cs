using System;
using System.Collections.Generic;
using System.Linq;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 设计画布"元数据变了"之后要推给宿主的那一份载荷（宿主 <c>K</c> 命令），以及推完之后
/// 编辑器这边要更新的记账。
/// </summary>
public sealed class DesignReloadPlan
{
    public required Dictionary<string, byte[]> Payload { get; init; }
    public required ChengeExportResult Result { get; init; }

    /// <summary>当前页随包推下去的那份 JSON（宿主里那份要跟着换，见 <see cref="DesignIncrementalReload"/>）。</summary>
    public byte[]? CurrentPageJson { get; init; }

    /// <summary>当前页重导出后的 编辑器控件 Id → widget id。几何补丁按它寻址。</summary>
    public IReadOnlyDictionary<string, string> CurrentPageWidgetIds { get; init; } =
        new Dictionary<string, string>();

    /// <summary>页面 id → 该页引用的 tag id（推完之后的样子）。</summary>
    public required IReadOnlyDictionary<string, HashSet<int>> PageTagRefs { get; init; }

    /// <summary>这一包里带了哪些页（日志用：判据要能看出"为什么是这几页"）。</summary>
    public IReadOnlyList<string> IncludedPageIds { get; init; } = Array.Empty<string>();

    /// <summary>退化成整包（判不出哪些页受影响，或有页没能导出）。</summary>
    public bool WholeProject { get; init; }

    /// <summary>退化原因 / 增量判据，进日志。</summary>
    public string Reason { get; init; } = "";

    /// <summary>导出有 error：载荷不完整，调用方不应发送。</summary>
    public bool Failed { get; init; }
}

/// <summary>
/// 元数据变化时算出一份**增量** <c>K</c> 包：元数据文件 + 当前页 + 受影响的页。
///
/// <para><b>为什么当前页必须带</b>：<c>J</c>（单页）只把 JSON 交给宿主建页，不写回它手里的
/// 工程表；<c>K</c> 之后宿主从工程表里重开当前页 —— 不带当前页，刚拖好的位置会被拉回启动时
/// 那份快照（改动只在编辑器模型里）。</para>
///
/// <para><b>为什么受影响页必须带</b>：页面 JSON 存的是 tag 序号。变量删了/换了序，没重发的页
/// 在切页时会把绑定指到别的变量或指到不存在的 tag —— 前者显示错值，后者建页失败。所以要么带
/// 该页，要么承认它没变；判不出来就整包，不允许"半新半旧还宣称一致"。</para>
/// </summary>
public static class DesignIncrementalReload
{
    public static DesignReloadPlan Build(
        HmiProject project,
        AssetLibrary? assets,
        string? projectDirectory,
        ChengeExportOptions options,
        string currentScreenId,
        ChengePageStats pageStats,
        IReadOnlyList<string> tagFingerprints,
        IReadOnlyDictionary<string, HashSet<int>> pageTagRefs)
    {
        var exporter = new ChengeExporter();
        var meta = exporter.ExportMetaOnly(project, assets, options, projectDirectory, pageStats, out var metaResult);
        if (!metaResult.Success)
        {
            return new DesignReloadPlan
            {
                Payload = meta,
                Result = metaResult,
                PageTagRefs = pageTagRefs,
                Reason = "元数据导出失败",
                Failed = true,
            };
        }

        // 判据：页里引用的 tag 序号在新表里是否还指向同一个变量。判不出来（没有上一轮记录、
        // 或有页没记过）就整包 —— 多传几十 KB 换"切页一定正确"。
        var stale = ChengeIncrementalExport.StalePageIds(tagFingerprints, metaResult.TagFingerprints,
            pageTagRefs.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<int>)kv.Value, StringComparer.Ordinal));
        if (stale == null)
        {
            return WholeProject(project, assets, projectDirectory, options, currentScreenId,
                pageTagRefs.Count == 0 ? "没有上一轮的页面 tag 引用记录" : "上一轮记录不完整（有新页或 tag 表为空）");
        }

        var screenByPageId = new Dictionary<string, HmiScreen>(StringComparer.Ordinal);
        foreach (var screen in project.Screens)
        {
            if (metaResult.PageIds.TryGetValue(screen.Id, out var pageId)) screenByPageId[pageId] = screen;
        }
        foreach (var pageId in screenByPageId.Keys)
        {
            if (!pageTagRefs.ContainsKey(pageId))
                return WholeProject(project, assets, projectDirectory, options, currentScreenId,
                    $"页 {pageId} 没有 tag 引用记录（新页或引用没记全）");
        }

        var currentPageId = metaResult.PageIds.TryGetValue(currentScreenId, out var current)
            ? current
            : "";
        var include = new HashSet<string>(stale, StringComparer.Ordinal);
        if (currentPageId.Length > 0) include.Add(currentPageId);

        var payload = new Dictionary<string, byte[]>(meta, StringComparer.Ordinal);
        var refs = new Dictionary<string, HashSet<int>>(pageTagRefs, StringComparer.Ordinal);
        byte[]? currentJson = null;
        IReadOnlyDictionary<string, string> currentWidgetIds = new Dictionary<string, string>();

        foreach (var pageId in include.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!screenByPageId.TryGetValue(pageId, out var screen))
                return WholeProject(project, assets, projectDirectory, options, currentScreenId,
                    $"页 {pageId} 找不到对应的屏（可能刚被删）");

            // 与整包同一套 maps/Build* 逻辑：tag 序号、widget id 都由导出器自己算，
            // 这里不重写一遍 —— 两份实现漂移的后果是"页里的绑定对不上 tags.json"。
            var json = exporter.ExportPageOnly(project, assets, options, projectDirectory, screen.Id,
                out var pageResult, out var widgetIds);
            if (json == null || !pageResult.Success)
            {
                return WholeProject(project, assets, projectDirectory, options, currentScreenId,
                    $"页 {pageId} 重导出失败（{pageResult.Summary}）");
            }
            payload[$"pages/{pageId}.json"] = json;
            refs[pageId] = ChengeIncrementalExport.TagIdsOf(json);
            if (pageId == currentPageId)
            {
                currentJson = json;
                currentWidgetIds = widgetIds;
            }
        }

        // 报警/文本/存盘被删空时，宿主手里那几份旧文件还在。补中性空文档，让"删掉的配置"
        // 真的从宿主消失（否则旧报警照样会响）。
        ChengeIncrementalExport.FillMissingMetaFiles(payload);

        return new DesignReloadPlan
        {
            Payload = payload,
            Result = metaResult,
            CurrentPageJson = currentJson,
            CurrentPageWidgetIds = currentWidgetIds,
            PageTagRefs = refs,
            IncludedPageIds = include.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            Reason = stale.Count == 0 ? "tag 表未改变页面可见语义，只带当前页" : $"受影响页 {stale.Count} 个",
        };
    }

    /// <summary>保守路径：整包重发。判不出受影响页、或有页导不出来时走它。</summary>
    private static DesignReloadPlan WholeProject(HmiProject project, AssetLibrary? assets,
        string? projectDirectory, ChengeExportOptions options, string currentScreenId, string reason)
    {
        var files = new ChengeExporter().ExportToMemory(project, assets, options, projectDirectory, out var result);

        var refs = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var (relative, bytes) in files)
        {
            if (!relative.StartsWith("pages/", StringComparison.Ordinal)) continue;
            refs[relative["pages/".Length..^".json".Length]] = ChengeIncrementalExport.TagIdsOf(bytes);
        }

        byte[]? currentJson = null;
        var currentPageId = result.PageIds.TryGetValue(currentScreenId, out var mapped) ? mapped : "";
        if (currentPageId.Length > 0)
            files.TryGetValue($"pages/{currentPageId}.json", out currentJson);

        return new DesignReloadPlan
        {
            Payload = files,
            Result = result,
            CurrentPageJson = currentJson,
            CurrentPageWidgetIds = result.WidgetIds,
            PageTagRefs = refs,
            IncludedPageIds = refs.Keys.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            WholeProject = true,
            Reason = reason,
            Failed = !result.Success,
        };
    }
}
