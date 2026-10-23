namespace HmiEditor.Export.Chenge;

public enum ChengeIssueSeverity
{
    /// <summary>阻止导出（运行时一定会拒绝加载，或语义会静默丢失）。</summary>
    Error,

    /// <summary>导出继续，但内容被降级/丢弃，必须让人看见。</summary>
    Warning,
}

/// <summary>
/// 一条导出诊断。`Location` 是"在哪"（工程/页面/控件 id 一路下去），
/// 与运行时的报错风格对齐（文件 + JSON 路径 + 原因），便于两边对照。
/// </summary>
public sealed record ChengeIssue(ChengeIssueSeverity Severity, string Code, string Location, string Message)
{
    public override string ToString() => $"{Severity} {Code} @ {Location}: {Message}";
}

/// <summary>导出诊断收集器：错误不抛异常，全部攒起来一次给用户看。</summary>
public sealed class ChengeIssueBag
{
    private readonly List<ChengeIssue> _items = new();

    public IReadOnlyList<ChengeIssue> Items => _items;

    public bool HasErrors => _items.Any(i => i.Severity == ChengeIssueSeverity.Error);

    public int ErrorCount => _items.Count(i => i.Severity == ChengeIssueSeverity.Error);
    public int WarningCount => _items.Count(i => i.Severity == ChengeIssueSeverity.Warning);

    public void Error(string code, string location, string message) =>
        _items.Add(new ChengeIssue(ChengeIssueSeverity.Error, code, location, message));

    public void Warn(string code, string location, string message) =>
        _items.Add(new ChengeIssue(ChengeIssueSeverity.Warning, code, location, message));
}
