namespace HmiEditor.Device.Build;

/// <summary>
/// kinseal SDK 的<b>首次使用安装</b>：安装包里带着归档（<c>&lt;install&gt;/sdk/kinseal-sdk-*.tar.gz</c>
/// ＋同名 <c>.manifest.json</c>），第一次真正用到交叉编译时把它校验后装进用户目录。
///
/// <para><b>为什么不在安装器里直接铺开</b>：归档解压后约 360 MB / 两万多个文件，塞进安装器会让
/// 安装变慢、且 Windows 上还容易丢可执行位；打成 tar.gz 由应用自己解压，权限与布局都由代码控制，
/// 顺带和「从网络下载安装」共用同一条 <see cref="KinSealSdkInstaller"/> 校验/解压/落盘路径。</para>
///
/// <para><b>三条来源，优先级固定</b>：</para>
/// <list type="number">
///   <item>已经装好（<see cref="KinSealSdkInstaller.IsInstalled"/>）→ 直接用，不做任何 IO 重活；</item>
///   <item>安装包自带归档 → 按清单校验后安装（<b>没有清单就拒绝</b>：无法校验等于没校验）；</item>
///   <item>设置里配了清单地址 → 按清单下载安装（保留给将来；当前默认关闭）。</item>
/// </list>
/// </summary>
public static class KinSealSdkBootstrapper
{
    /// <summary>安装包内置归档所在目录（相对安装目录）。</summary>
    public const string BundledFolderName = "sdk";

    /// <summary>归档文件名前缀（<c>kinseal-sdk-&lt;version&gt;.tar.gz</c>）。</summary>
    public const string ArchivePrefix = "kinseal-sdk-";

    /// <summary>内置归档目录。</summary>
    public static string BundledDir(string installDir) => Path.Combine(installDir, BundledFolderName);

    /// <summary>当前平台标签（SDK 归档名后缀）：<c>win</c> / <c>linux</c>。</summary>
    public static string CurrentPlatformTag =>
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "other";

    /// <summary>
    /// 找随包分发的归档：<c>&lt;install&gt;/sdk/kinseal-sdk-*.tar.gz</c>。
    ///
    /// <para><b>平台变体</b>：工具链部分与宿主绑定（Linux 版是 ELF，Windows 版是 clang.exe），
    /// 所以归档名带平台后缀 <c>-linux-x64</c> / <c>-win-x64</c>。优先取本平台的；没有本平台的
    /// 就退回无后缀的旧格式；只有别平台时才返回它（由调用方给出"装不了"的明确提示，
    /// 而不是笼统的"未找到 SDK"）。同平台多个时按文件名**降序**取第一个。</para>
    /// </summary>
    public static string? FindBundledArchive(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return null;
        var dir = BundledDir(installDir);
        if (!Directory.Exists(dir)) return null;

        try
        {
            var all = Directory.EnumerateFiles(dir, ArchivePrefix + "*.tar.gz").ToList();
            if (all.Count == 0) return null;

            string Name(string p) => Path.GetFileName(p);
            var tag = CurrentPlatformTag;

            var mine = all
                .Where(p => Name(p).Contains($"-{tag}-x64", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (mine is not null) return mine;

            // 旧格式（无平台后缀）仍然可用：工具链由 toolchain.json 描述，未必绑定平台
            var untagged = all
                .Where(p => !System.Text.RegularExpressions.Regex.IsMatch(
                    Name(p), ArchivePrefix + @".+-(win|linux|mac)-x64\.tar\.gz$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .OrderByDescending(Name, StringComparer.Ordinal)
                .FirstOrDefault();
            return untagged ?? all.OrderByDescending(Name, StringComparer.Ordinal).First();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>归档是否属于**别**的平台（本机装不了的那种）。</summary>
    public static bool IsForeignPlatformArchive(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        var m = System.Text.RegularExpressions.Regex.Match(
            name, ArchivePrefix + @".+-(?<tag>win|linux|mac)-x64\.tar\.gz$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && !string.Equals(m.Groups["tag"].Value, CurrentPlatformTag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>归档对应的清单路径：<c>xxx.tar.gz</c> → <c>xxx.manifest.json</c>。</summary>
    public static string ManifestPathFor(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var name = Path.GetFileName(archivePath);
        const string tarGz = ".tar.gz";
        var stem = name.EndsWith(tarGz, StringComparison.OrdinalIgnoreCase)
            ? name[..^tarGz.Length]
            : Path.GetFileNameWithoutExtension(name);
        return Path.Combine(Path.GetDirectoryName(archivePath)!, stem + ".manifest.json");
    }

    /// <summary>
    /// 确保 SDK 已可用；已装则直接返回成功（<see cref="KinSealSdkInstallResult.Installed"/> = false）。
    /// 失败返回可上界面的原因，不抛异常。
    /// </summary>
    /// <param name="installDir">编辑器安装目录（找内置归档）。</param>
    /// <param name="manifestUrl">可选的下载清单地址（空 = 只认内置归档）。</param>
    /// <param name="progress">安装进度（首次安装有解压阶段，界面可显示）。</param>
    /// <param name="ct">取消。</param>
    public static async Task<KinSealSdkInstallResult> EnsureAsync(
        string installDir,
        string? manifestUrl = null,
        IProgress<KinSealSdkProgress>? progress = null,
        CancellationToken ct = default)
    {
        var logs = new List<string>();

        // 先读随包清单：它决定"已装的那份是不是就是这一份"（换字体/升级 SDK 后必须能换掉旧的）
        var archive = FindBundledArchive(installDir);
        if (archive is not null && IsForeignPlatformArchive(archive))
        {
            var foreign = $"安装包附带的是别的平台 SDK（{Path.GetFileName(archive)}），"
                          + $"本机是 {CurrentPlatformTag}-x64 —— 工具链无法在本机执行。"
                          + "请用对应平台的安装包，或在 设置→真机部署 里改用它平台的本地安装包";
            logs.Add($"[sdk] ✗ {foreign}");
            return new KinSealSdkInstallResult { Success = false, Error = foreign, Logs = logs };
        }
        KinSealSdkManifest? bundled = null;
        string? manifestError = null;
        if (archive is not null)
        {
            var manifestPath = ManifestPathFor(archive);
            if (!File.Exists(manifestPath))
            {
                manifestError = $"SDK 归档缺少清单，无法校验：{manifestPath}";
            }
            else
            {
                try
                {
                    bundled = KinSealSdkManifest.Parse(
                        await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    manifestError = ex.Message;
                }
            }
        }

        if (KinSealSdkInstaller.IsInstalled())
        {
            var state = KinSealSdkInstaller.ReadState();
            // 清单读不出来时不动已装的那份：宁可用旧的，也不要把能用的删掉换个来路不明的
            var sameAsBundled = bundled is null
                || string.Equals(state?.Sha256, bundled.Sha256, StringComparison.OrdinalIgnoreCase);
            if (sameAsBundled)
            {
                logs.Add($"[sdk] 已安装 {state?.Version ?? "?"}（{KinSealSdkInstaller.SdkRoot}）");
                return new KinSealSdkInstallResult
                {
                    Success = true,
                    Installed = false,
                    Version = state?.Version,
                    Root = KinSealSdkInstaller.SdkRoot,
                    Logs = logs,
                };
            }

            logs.Add($"[sdk] 安装包内归档与已装版本不一致（已装 {state?.Version ?? "?"} → {bundled?.Version}），重新安装");
        }
        else if (manifestError is not null)
        {
            logs.Add($"[sdk] ✗ {manifestError}");
            return new KinSealSdkInstallResult { Success = false, Error = manifestError, Logs = logs };
        }

        if (archive is not null && bundled is not null)
        {
            logs.Add($"[sdk] 使用安装包自带归档（{bundled.Version}）：{archive}");
            var result = await new KinSealSdkInstaller()
                .InstallFromArchiveAsync(archive, bundled.Sha256, bundled.Version, progress, ct)
                .ConfigureAwait(false);
            return Merge(result, logs);
        }

        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            logs.Add($"[sdk] 安装包未附带可用的 SDK，改用下载清单：{manifestUrl}");
            var result = await new KinSealSdkInstaller()
                .InstallAsync(manifestUrl, progress, ct)
                .ConfigureAwait(false);
            return Merge(result, logs);
        }

        var notFound = manifestError
            ?? $"未找到 kinseal SDK：安装包未附带 {ArchivePrefix}*.tar.gz，也未配置下载清单地址";
        logs.Add($"[sdk] ✗ {notFound}");
        return new KinSealSdkInstallResult { Success = false, Error = notFound, Logs = logs };
    }

    private static KinSealSdkInstallResult Merge(KinSealSdkInstallResult result, List<string> prefix)
    {
        if (prefix.Count == 0) return result;
        var logs = new List<string>(prefix.Count + result.Logs.Count);
        logs.AddRange(prefix);
        logs.AddRange(result.Logs);
        return new KinSealSdkInstallResult
        {
            Success = result.Success,
            Error = result.Error,
            Version = result.Version,
            Root = result.Root,
            Installed = result.Installed,
            Logs = logs,
        };
    }
}
