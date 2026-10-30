namespace HmiEditor.Core;

/// <summary>
/// HMI Editor 版本信息中心。
/// 所有版本字符串统一从此处引用。
/// </summary>
public static class VersionInfo
{
    /// <summary>
    /// 版本号：**major 固定 2**，只递增后两位（2.x.y）。
    /// 新增控件/属性属于 minor（2.x.0），修缺陷属于 patch（2.x.y）。
    /// </summary>
    public const string Version = "2.28.7";

    /// <summary>完整的版本显示字符串</summary>
    public const string DisplayVersion = $"HMI Editor v{Version}";

    /// <summary>版本后缀（预览版 = -preview, RC = -rc, 正式版 = ""）</summary>
    public const string Suffix = "";

    /// <summary>完整版本标识</summary>
    public static string FullVersion => $"{Version}{Suffix}";
}
