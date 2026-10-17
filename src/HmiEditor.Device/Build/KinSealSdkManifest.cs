using System.Text.Json;
using System.Text.Json.Serialization;

namespace HmiEditor.Device.Build;

/// <summary>
/// kinseal SDK 的下载清单 —— 描述「哪个版本、从哪下、多大、SHA-256 是多少」。
///
/// <para><b>为什么要有清单而不是写死一个 URL：</b>SDK 里是 buildroot 工具链 + 设备侧运行时，
/// 体积大、会升级；清单让「换版本」变成改一个 JSON，而不是发新版编辑器。
/// 校验值必须来自清单（可信来源），不能由下载到的文件自己声明。</para>
///
/// <para><b>清单形态</b>（字段名大小写不敏感，允许注释）：</para>
/// <code>
/// {
///   "schemaVersion": 1,
///   "version": "1.0.0",
///   "url": "https://example.com/kinseal/sdk-1.0.0.tar.gz",   // 随包分发时留空
///   "sha256": "&lt;64 位十六进制&gt;",
///   "sizeBytes": 123456789
/// }
/// </code>
///
/// <para><b><c>url</c> 为什么可以空</b>：随安装包分发的归档只有本地文件，没有下载地址；
/// 校验值仍来自清单。只有走「从网络下载」时才要求非空（见
/// <see cref="KinSealSdkInstaller.InstallAsync"/>）。</para>
/// </summary>
public sealed class KinSealSdkManifest
{
    /// <summary>本编辑器认识的清单结构版本。认不出就拒绝，不猜。</summary>
    public const int SupportedSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SupportedSchemaVersion;

    /// <summary>SDK 版本号（安装后用它在界面上显示；也作为「已是最新」的比较依据）。</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    /// <summary>安装包地址（http/https）。</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    /// <summary>安装包的 SHA-256（64 位十六进制，大小写不敏感）。</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = "";

    /// <summary>安装包字节数；0 = 清单未声明，跳过长度校验（SHA-256 仍然校验）。</summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析并校验清单；任何字段不合规都抛 <see cref="InvalidDataException"/>（消息可直接上界面）。</summary>
    public static KinSealSdkManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("SDK 清单为空。");

        KinSealSdkManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<KinSealSdkManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"SDK 清单不是合法 JSON：{ex.Message}", ex);
        }

        if (manifest is null)
            throw new InvalidDataException("SDK 清单为空。");

        return Validate(manifest);
    }

    /// <summary>字段级校验（与 <see cref="Parse"/> 分开，便于直接从对象构造时复用）。</summary>
    public static KinSealSdkManifest Validate(KinSealSdkManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException(
                $"SDK 清单结构版本 {manifest.SchemaVersion} 不受支持（本版本只认 {SupportedSchemaVersion}）。");

        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("SDK 清单缺少 version。");

        // 随包分发的归档没有下载地址，url 允许为空；填了就必须是 http/https 绝对地址
        if (!string.IsNullOrWhiteSpace(manifest.Url))
        {
            if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"SDK 清单的 url 必须是 http/https 绝对地址：{manifest.Url}");
        }

        if (!IsValidSha256(manifest.Sha256))
            throw new InvalidDataException($"SDK 清单的 sha256 必须是 64 位十六进制：{manifest.Sha256}");

        if (manifest.SizeBytes < 0)
            throw new InvalidDataException($"SDK 清单的 sizeBytes 不能为负：{manifest.SizeBytes}");

        return new KinSealSdkManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            Version = manifest.Version,
            Url = manifest.Url,
            Sha256 = manifest.Sha256.Trim().ToLowerInvariant(),
            SizeBytes = manifest.SizeBytes,
        };
    }

    /// <summary>是否为 64 位十六进制（去掉可选空白）。</summary>
    public static bool IsValidSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length != 64) return false;
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
