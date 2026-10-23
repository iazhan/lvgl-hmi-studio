using System.Text;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 37 §1 的字符串 id 规则：小写字母开头，只含小写字母/数字/下划线，≤32。
/// 编辑器内部的 id 是 GUID 前 8 位（可能数字开头），页面 id 还可能是中文名，
/// 所以导出期必须过一遍归一化，而不是把内部 id 直接写进工程。
/// </summary>
internal static class ChengeNames
{
    public const int MaxLength = 32;

    public static bool IsValid(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > MaxLength) return false;
        if (s[0] < 'a' || s[0] > 'z') return false;
        foreach (var ch in s)
            if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return false;
        return true;
    }

    /// <summary>归一化：非 [a-z0-9_] 换下划线、压掉重复下划线、裁到 32、保证字母开头。</summary>
    public static string Slug(string? raw, string prefix)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw ?? "")
        {
            if (ch is >= 'A' and <= 'Z') sb.Append((char)(ch + 32));
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') sb.Append(ch);
            else sb.Append('_');
        }

        var s = sb.ToString();
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);
        s = s.Trim('_');
        if (s.Length > MaxLength) s = s[..MaxLength].TrimEnd('_');
        if (s.Length == 0 || s[0] is < 'a' or > 'z') s = prefix + "_" + s;
        if (s.Length > MaxLength) s = s[..MaxLength].TrimEnd('_');
        return s;
    }

    /// <summary>在已用集合里取一个不冲突的 id（冲突时追加 _2、_3…）。</summary>
    public static string Unique(string candidate, ISet<string> used)
    {
        var s = candidate;
        var n = 2;
        while (!used.Add(s))
        {
            var suffix = "_" + n++;
            s = candidate.Length + suffix.Length > MaxLength
                ? candidate[..(MaxLength - suffix.Length)] + suffix
                : candidate + suffix;
        }
        return s;
    }
}
