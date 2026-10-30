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

    /// <summary>起始页 id；空 = 用 <see cref="StartScreenId"/>，再空 = 第一个页面。</summary>
    public string? StartPageId { get; set; }

    /// <summary>
    /// 起始屏的编辑器 id。页 id 的生成规则在导出器内部（<c>ChengeMaps</c>），
    /// 调用方通常不知道自己的屏会变成哪个页 id —— 传这个比传 <see cref="StartPageId"/> 稳。
    /// </summary>
    public string? StartScreenId { get; set; }

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

    /// <summary>
    /// 本次写进 <c>manifest.name</c> 的板上工程 id。调用方据此把"推导出来的 id"固化进工程文件。
    /// </summary>
    public string ProjectId { get; init; } = "";

    /// <summary>
    /// id 由工程名推导而来（工程里没有显式 id）。为 true 时调用方应把 <see cref="ProjectId"/>
    /// 写回 <c>HmiProject.ProjectId</c> 并保存 —— 否则下次改工程名就会换 id。
    /// </summary>
    public bool ProjectIdGenerated { get; init; }

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

    /// <summary>编辑器屏 Id → 导出后的页面 id。设计画布推当前页 JSON 时要用。</summary>
    public IReadOnlyDictionary<string, string> PageIds { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// 本次建页累加出来的 manifest 派生声明（<c>features</c> / <c>font_sizes</c>）。
    ///
    /// <para>不建页的导出（<see cref="ChengeExporter.ExportMetaOnly"/>）拿不到它们，只能照这份
    /// 上一轮的记过来 —— manifest 少声明一项能力，宿主建页期就会拒载仍在跑的那一页。</para>
    /// </summary>
    public ChengePageStats PageStats { get; init; } = ChengePageStats.Empty;

    /// <summary>
    /// 按 tag id 排列的页面可见指纹（<c>名字|类型</c>）。页面 JSON 只引用 tag 序号，
    /// 序号背后换了变量就意味着那一页过期 —— 设计画布据此决定增量重载要带哪些页。
    /// </summary>
    public IReadOnlyList<string> TagFingerprints { get; init; } = Array.Empty<string>();

    public string Summary =>
        $"页面 {PageCount} / 控件 {WidgetCount} / 变量 {TagCount} / 设备 {DeviceCount}" +
        $"；错误 {Issues.ErrorCount}，警告 {Issues.WarningCount}";
}

/// <summary>
/// 建页期累加出来的 manifest 派生声明：<c>features</c>（控件要什么能力）与
/// <c>font_sizes</c>（用到的字号档位）。
///
/// <para><b>为什么单独一个类型</b>：这两项只有 mapper 真正建页时才知道（控件类型 → 能力、
/// 控件字号 → 档位）。设计画布只重发元数据 + 若干页时拿不到它们，必须把上一份整包导出的
/// 结果原样带进 manifest —— 少声明一项能力，宿主建页期会以"需要 manifest.features 声明 X"
/// 拒载，而那一页此刻正在宿主里跑着。</para>
/// </summary>
public sealed class ChengePageStats
{
    public static ChengePageStats Empty { get; } = new(Array.Empty<string>(), Array.Empty<int>());

    public ChengePageStats(IEnumerable<string> features, IEnumerable<int> fontSizes)
    {
        Features = features.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        FontSizes = fontSizes.Distinct().OrderBy(s => s).ToArray();
    }

    public IReadOnlyList<string> Features { get; }
    public IReadOnlyList<int> FontSizes { get; }
}
