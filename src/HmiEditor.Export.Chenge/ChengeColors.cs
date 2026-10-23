using System.Globalization;
using HmiEditor.Core.Models;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 导出期的颜色/字号解引用 —— **主题在导出时烘进控件属性**。
///
/// <para>chenge-runtime 不读 `theme.json`、不认 `@token`（37 §6.11）：主题是编辑器的
/// 设计期概念，交给运行时之前颜色必须是字面 `#RRGGBB`。所以这里做两件事：
/// ① 把 `@token` 按工程主题解开；② 控件没显式给色的地方补主题默认值，
/// 否则同一个控件在编辑器里是主题色、在板上是运行时内置默认色。</para>
/// </summary>
internal sealed class ChengeColors
{
    private readonly HmiTheme _theme;
    private readonly ChengeIssueBag _issues;

    public ChengeColors(HmiTheme theme, ChengeIssueBag issues)
    {
        _theme = theme;
        _issues = issues;
    }

    public string TextDefault => Normalize(_theme.TextColor) ?? "#E6EDF3";
    public string Primary => Normalize(_theme.PrimaryColor) ?? "#2F70D0";
    public string Success => Normalize(_theme.SuccessColor) ?? "#38C172";
    public string Warning => Normalize(_theme.WarningColor) ?? "#F2B84B";
    public string Danger => Normalize(_theme.ErrorColor) ?? "#D32F2F";
    public string Border => Normalize(_theme.BorderColor) ?? "#3A4A5A";
    public string Surface => Normalize(_theme.SurfaceColor) ?? "#1B2530";
    public string Background => Normalize(_theme.BackgroundColor) ?? "#101820";

    /// <summary>解引用并规范化；空/null 返回 null（调用方决定回退值）。</summary>
    public string? Resolve(string? raw, string where)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim();
        if (s.StartsWith('@'))
        {
            var token = s[1..];
            var resolved = token switch
            {
                "bg" => Background,
                "surface" => Surface,
                "fg" or "text" => TextDefault,
                "primary" => Primary,
                "success" => Success,
                "warn" => Warning,
                "alarm" or "error" => Danger,
                "border" => Border,
                _ => null,
            };
            if (resolved == null)
            {
                _issues.Warn("color.token_unknown", where,
                    $"主题令牌 {s} 未定义，已回退到主题文字色（运行时不解析令牌）");
                return TextDefault;
            }
            return resolved;
        }

        var norm = Normalize(s);
        if (norm == null)
        {
            _issues.Warn("color.invalid", where, $"颜色 {raw} 不是 #RRGGBB，已回退到主题文字色");
            return TextDefault;
        }
        return norm;
    }

    public string ResolveOr(string? raw, string fallback, string where) => Resolve(raw, where) ?? fallback;

    /// <summary>#RGB / #RRGGBB / 0xRRGGBB → #RRGGBB；其它返回 null。</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = "#" + s[2..];
        if (!s.StartsWith('#')) return null;
        var hex = s[1..];
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6) return null;
        foreach (var c in hex)
            if (!Uri.IsHexDigit(c)) return null;
        return "#" + hex.ToUpper(CultureInfo.InvariantCulture);
    }
}
