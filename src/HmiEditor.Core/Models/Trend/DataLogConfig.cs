namespace HmiEditor.Core.Models.Trend;

/// <summary>
/// 数据记录配置 —— 嵌入式 Flash 友好的环形缓冲区策略。
///
/// 设计原则 (data-pipeline-reliability):
///   - 新鲜度 SLI: SampleIntervalMs 定义数据时效目标
///   - 完整性: 环形缓冲区满时覆盖最旧记录（OverwriteOldest）或停止（StopWhenFull）
///   - 重放路径: StoragePath 支持 SD 卡或 Flash 扇区偏移
/// </summary>
public class DataLogConfig
{
    /// <summary>唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>存盘组名（导出 savecfg.json 的 group.name；历史曲线的 group 引用它）。</summary>
    public string Name { get; set; } = "";

    /// <summary>采集模式：period（按 PeriodS）/ change（值变化）/ trigger（动作触发）。</summary>
    public string Mode { get; set; } = "period";

    /// <summary>周期秒（mode=period 时生效，1–3600）。</summary>
    public int PeriodS { get; set; } = 60;

    /// <summary>组容量上限（MB，1–64）。与 MaxRecords 二选一，写了 MaxRecords 就以条数为准。</summary>
    public int MaxMb { get; set; } = 4;

    /// <summary>CSV 时间列格式：iso / unix。</summary>
    public string CsvTimeFormat { get; set; } = "iso";

    /// <summary>采样间隔（毫秒），默认 1000ms。这是数据新鲜度 SLI。</summary>
    public int SampleIntervalMs { get; set; } = 1000;

    /// <summary>最大记录数（环形缓冲区容量）</summary>
    public int MaxRecords { get; set; } = 86400;

    /// <summary>缓冲区满时的策略</summary>
    public LogFullPolicy FullPolicy { get; set; } = LogFullPolicy.OverwriteOldest;

    /// <summary>存储路径（SD 卡路径或 Flash 扇区偏移地址）</summary>
    public string? StoragePath { get; set; }

    /// <summary>要记录的变量 ID 列表</summary>
    public List<string> VariableIds { get; set; } = new();

    /// <summary>是否记录时间戳</summary>
    public bool RecordTimestamp { get; set; } = true;

    /// <summary>CSV 导出分隔符</summary>
    public string CsvDelimiter { get; set; } = ",";

    /// <summary>是否启用存储压缩（嵌入式常用 RLE 或差值编码）</summary>
    public bool EnableCompression { get; set; } = false;

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 缓冲区满时的处理策略。
/// </summary>
public enum LogFullPolicy
{
    /// <summary>覆盖最旧记录（嵌入式默认策略）</summary>
    OverwriteOldest,

    /// <summary>停止记录，保留现有数据</summary>
    StopWhenFull,
}
