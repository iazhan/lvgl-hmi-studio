using System.Text;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 37 §1 的字符串 id 规则：小写字母开头，只含小写字母/数字/下划线，≤32。
/// 编辑器内部的 id 是 GUID 前 8 位（可能数字开头），页面 id 还可能是中文名，
/// 所以导出期必须过一遍归一化，而不是把内部 id 直接写进工程。
/// </summary>
public static class ChengeNames
{
    public const int MaxLength = 32;

    /// <summary>板端保留名：<c>projects/active</c> 是"当前工程"指针，不能当工程目录。</summary>
    public const string ReservedActive = "active";

    /// <summary>
    /// 板端对工程 id 的要求（<c>manifest.name</c>，见 chenge-common 的 <c>id_valid</c>）：
    /// 1~32 位、只含小写字母/数字/下划线、不能是 <see cref="ReservedActive"/>。
    ///
    /// <para>与 <see cref="IsValid"/> 的差别：这里允许数字或下划线开头 —— 板端只照目录名判，
    /// 卡更严的规则会出现"编辑器不让填、板子其实认"。</para>
    /// </summary>
    public static bool IsBoardProjectId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > MaxLength) return false;
        if (string.Equals(s, ReservedActive, StringComparison.Ordinal)) return false;
        foreach (var ch in s)
            if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return false;
        return true;
    }

    /// <summary>
    /// 按工程名推导板上工程 id。
    ///
    /// <para>纯中文名经过 <see cref="Slug"/> 只剩前缀（<c>proj_</c>），两个中文工程会撞成同一个
    /// id —— 板端 "更新组态" 按 id 覆盖目录，撞了就是互相覆盖。所以"名字里没有任何可用
    /// ASCII 字符"时补名称的稳定短哈希。导出器与设置页预览共用本函数，两处结果必须一致，
    /// 否则界面显示的 id 与真正写进 manifest 的不是同一个。</para>
    /// </summary>
    public static string DeriveBoardProjectId(string? name)
    {
        var raw = name ?? "";
        var id = Slug(raw, "proj");
        if (id is "proj" or "proj_") id = "proj_" + ShortHash(raw);
        if (string.Equals(id, ReservedActive, StringComparison.Ordinal)) id = "proj_" + id;
        return IsBoardProjectId(id) ? id : "chenge_project";
    }

    /// <summary>FNV-1a 32 位的 8 位小写 hex：只为"推导 id 不撞、且同输入同输出"。</summary>
    public static string ShortHash(string s)
    {
        var h = 2166136261u;
        foreach (var ch in s)
        {
            h ^= ch;
            h *= 16777619u;
        }
        return h.ToString("x8");
    }

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
