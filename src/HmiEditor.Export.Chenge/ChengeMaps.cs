using HmiEditor.Core.Models.Scripting;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 导出期的一次性索引：编辑器内部 id → 37 工程里的 id。
/// 变量 id 必须 0 起连续（37 §5），所以这张表在写任何页面之前就要定下来。
/// </summary>
internal sealed class ChengeMaps
{
    public Dictionary<string, int> TagByVariableId { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TagByVariableName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> SysTagByEditorId { get; } = new(StringComparer.Ordinal);

    /// <summary>tag id → 变量名（曲线图例等显示用；系统变量取编辑器里的显示名）。</summary>
    public Dictionary<int, string> NameByTag { get; } = new();

    /// <summary>tag id → 工程值类型（bool 的写动作必须发 true/false，发 1 会被运行时拒绝）。</summary>
    public Dictionary<int, string> TypeByTag { get; } = new();

    public bool IsBoolTag(int tag) => TypeByTag.TryGetValue(tag, out var t) && t == "bool";

    public string? TagName(string variableRef)
    {
        var tag = ResolveTag(variableRef);
        return tag != null && NameByTag.TryGetValue(tag.Value, out var name) ? name : null;
    }

    public Dictionary<string, string> PageIdByScreenId { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PageIdByScreenName { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> DeviceIdByConnectionId { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SlaveDeviceIdBySlaveId { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HmiScript> ScriptById { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 编辑器里的存盘组名 → 导出后的 savecfg 组名（Slug + 去重）。
    ///
    /// <para><b>为什么要有这张表</b>：历史曲线的 <c>props.group</c> 由运行时拿去 savecfg.json
    /// 里对名字。写编辑器原值（中文/大写/空格）时组名是 Slug 过的，两边对不上 → 建页失败；
    /// 组名必须在写任何页面之前先定下来，页面与 savecfg.json 才用的是同一个。</para>
    /// </summary>
    public Dictionary<string, string> SaveCfgGroupByEditorName { get; } = new(StringComparer.Ordinal);

    /// <summary>编辑器组名 → 导出组名；不是存盘组时返回 null。</summary>
    public string? SaveCfgGroupOf(string? editorName)
    {
        if (string.IsNullOrWhiteSpace(editorName)) return null;
        return SaveCfgGroupByEditorName.TryGetValue(editorName.Trim(), out var name) ? name : null;
    }

    /// <summary>按变量 Id（优先）或变量名解析 tag id；系统变量按编辑器 id 解析。</summary>
    public int? ResolveTag(string? variableRef)
    {
        if (string.IsNullOrWhiteSpace(variableRef)) return null;
        if (SysTagByEditorId.TryGetValue(variableRef, out var sys)) return sys;
        if (TagByVariableId.TryGetValue(variableRef, out var id)) return id;
        if (TagByVariableName.TryGetValue(variableRef, out var byName)) return byName;
        return null;
    }

    public string? PageIdOf(string screenRef)
    {
        if (string.IsNullOrWhiteSpace(screenRef)) return null;
        if (PageIdByScreenId.TryGetValue(screenRef, out var byId)) return byId;
        if (PageIdByScreenName.TryGetValue(screenRef, out var byName)) return byName;
        return null;
    }
}
