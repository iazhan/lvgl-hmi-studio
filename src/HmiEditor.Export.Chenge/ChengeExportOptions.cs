namespace HmiEditor.Export.Chenge;

/// <summary>导出目标参数（不认识的值一律不猜，缺省取工程与硬件目标里的值）。</summary>
public sealed class ChengeExportOptions
{
    /// <summary>目标硬件配置 id（manifest.hw_profile）。空 = 按工程 HardwareTargetId 推。</summary>
    public string? HwProfile { get; set; }

    /// <summary>屏幕宽（manifest.screen.width）。0 = 取首页宽度。</summary>
    public int ScreenWidth { get; set; }

    /// <summary>屏幕高。0 = 取首页高度。</summary>
    public int ScreenHeight { get; set; }

    /// <summary>起始页 id；空 = 第一个页面。</summary>
    public string? StartPageId { get; set; }

    /// <summary>工程包里带的语言（strings.json 的 default）。</summary>
    public string DefaultLanguage { get; set; } = "zh";

    /// <summary>是否把图片转成 RGB565 .bin 一并写出。</summary>
    public bool BundleAssets { get; set; } = true;
}

public sealed class ChengeExportResult
{
    public required ChengeIssueBag Issues { get; init; }
    public required string OutputDirectory { get; init; }
    public bool Success => !Issues.HasErrors;

    public int PageCount { get; init; }
    public int WidgetCount { get; init; }
    public int TagCount { get; init; }
    public int DeviceCount { get; init; }
    public IReadOnlyList<string> WrittenFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 编辑器控件 Id → 导出后的 widget id（`w3` / `w3_1`）。两种 id 不是一回事：
    /// 导出器按遍历序号生成 widget id，所以设计画布的几何补丁必须查这张表才能寻址。
    /// </summary>
    public IReadOnlyDictionary<string, string> WidgetIds { get; init; } =
        new Dictionary<string, string>();

    public string Summary =>
        $"页面 {PageCount} / 控件 {WidgetCount} / 变量 {TagCount} / 设备 {DeviceCount}" +
        $"；错误 {Issues.ErrorCount}，警告 {Issues.WarningCount}";
}
