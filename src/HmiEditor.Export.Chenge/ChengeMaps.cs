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
