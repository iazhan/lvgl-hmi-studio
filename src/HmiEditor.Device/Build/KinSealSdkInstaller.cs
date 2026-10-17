using HmiEditor.Core.Services;

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HmiEditor.Device.Build;

/// <summary>安装阶段（界面按它显示"正在做什么"）。</summary>
public enum KinSealSdkInstallPhase
{
    Download,
    Verify,
    Extract,
    Done,
}

/// <summary>安装进度。Fraction 为 0..1（下载阶段总长未知时给 -1，界面按不确定进度显示）。</summary>
public sealed record KinSealSdkProgress(KinSealSdkInstallPhase Phase, double Fraction, string Detail = "");

/// <summary>安装结果（失败给可上界面的原因，不抛异常）。</summary>
public sealed class KinSealSdkInstallResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Version { get; init; }
    public string? Root { get; init; }
    /// <summary>本次是否真的装了（false = 已是最新，直接复用）。</summary>
    public bool Installed { get; init; }
    public IReadOnlyList<string> Logs { get; init; } = Array.Empty<string>();
}

/// <summary>安装记录（写在 SDK 根下的 state.json）。</summary>
public sealed class KinSealSdkState
{
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("sourceUrl")] public string SourceUrl { get; init; } = "";
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("installedAtUtc")] public DateTime InstalledAtUtc { get; init; }
}

/// <summary>
/// kinseal SDK 的<b>首次使用下载 + 校验 + 安装</b>。
///
/// <para><b>为什么需要它</b>：交叉编译要 buildroot 工具链与设备侧运行时（几百 MB），
/// 不可能塞进编辑器安装包，也不该要求用户自己去拼目录。编辑器按清单下载一次、校验后
/// 装进<b>用户数据目录</b>（<see cref="AppPaths.DataRoot"/>，可写、随用户走），
/// 之后 <c>F1C100SSoBuilder</c> 的发现逻辑自动认它。</para>
///
/// <para><b>完整性判据（缺一不可）</b>：</para>
/// <list type="number">
///   <item>清单里的 <c>sha256</c> 与下载内容逐字节一致（不匹配就**不落盘**）；</item>
///   <item>清单声明了 <c>sizeBytes</c> 时，长度也要一致；</item>
///   <item>解压出的目录必须含运行时判别文件与工具链可执行文件，缺任何一个都算装失败。</item>
/// </list>
///
/// <para><b>解压安全</b>：条目路径逐条做前缀围栏校验，任何 <c>..</c>/绝对路径/符号链接
/// 直接拒绝（安装包来自网络，不能假设它守规矩）。</para>
///
/// <para><b>换装是原子的</b>：先解到同级 staging 目录，校验通过后才替换正式目录；
/// 换装中途失败会回滚旧目录 —— 不留"半个 SDK"这种比没有更难查的状态。</para>
/// </summary>
public sealed class KinSealSdkInstaller
{
    /// <summary>用户数据根下的 SDK 目录名（<c>&lt;DataRoot&gt;/sdk/kinseal</c>）。</summary>
    public const string SdkRootRelativePath = "sdk/kinseal";

    /// <summary>交叉编译器可执行文件名（与 F1C100SSoBuilder 同一常量来源见其文档）。</summary>
    private const string GccName = "arm-buildroot-linux-gnueabi-gcc";

    private const string RuntimeMarker = "runtime/src/runtime/hmi_ui_api.h";
    private const string ToolchainMarker = "toolchain/bin/" + GccName;
    private const string StateFileName = "state.json";

    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpMessageHandler? _handler;

    /// <summary>构造。<paramref name="handler"/> 仅测试用（喂伪造响应，不碰网络）。</summary>
    public KinSealSdkInstaller(HttpMessageHandler? handler = null) => _handler = handler;

    /// <summary>
    /// SDK 根目录覆盖变量。默认装在 <c>&lt;DataRoot&gt;/sdk/kinseal</c>；
    /// 多实例隔离 / 测试需要"干净的没有 SDK 的环境"时指向别处。
    /// </summary>
    public const string SdkRootEnvVar = "HMI_KINSEAL_SDK_ROOT";

    /// <summary>安装目标根（默认 <c>&lt;DataRoot&gt;/sdk/kinseal</c>）。未安装时不保证存在。</summary>
    public static string SdkRoot
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(SdkRootEnvVar);
            return !string.IsNullOrWhiteSpace(overridden)
                ? Path.GetFullPath(overridden)
                : Path.Combine(AppPaths.DataRoot, "sdk", "kinseal");
        }
    }

    /// <summary>操作日志（每条一行，供界面展示）。</summary>
    public event Action<string>? Log;

    // ───────────────────────── 已装状态 ─────────────────────────

    /// <summary>读取已安装记录；没有/读坏都返回 null（读坏不抛异常，按未装处理）。</summary>
    public static KinSealSdkState? ReadState()
    {
        try
        {
            var path = Path.Combine(SdkRoot, StateFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<KinSealSdkState>(File.ReadAllText(path), StateJson);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 是否已装好：既有安装记录，判别文件也都在。
    /// 只看目录不看记录会认下"手工扔进来的半个目录"；只看记录会认下"被删了一半"。
    /// </summary>
    public static bool IsInstalled() => VerifyLayout(SdkRoot, out _);

    /// <summary>校验目录布局（判别文件齐不齐、LVGL 树在不在）。</summary>
    public static bool VerifyLayout(string root, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            error = $"SDK 目录不存在：{root}";
            return false;
        }
        if (!File.Exists(Path.Combine(root, RuntimeMarker.Replace('/', Path.DirectorySeparatorChar))))
        {
            error = $"SDK 缺少设备侧运行时（{RuntimeMarker}）";
            return false;
        }
        if (!File.Exists(Path.Combine(root, ToolchainMarker.Replace('/', Path.DirectorySeparatorChar))))
        {
            error = $"SDK 缺少交叉编译器（{ToolchainMarker}）";
            return false;
        }
        if (!HasLvglTree(Path.Combine(root, "runtime")))
        {
            error = "SDK 缺少 LVGL 源码树（runtime/third_party/lvgl-*）";
            return false;
        }
        return true;
    }

    private static bool HasLvglTree(string runtimeDir)
    {
        var thirdParty = Path.Combine(runtimeDir, "third_party");
        if (!Directory.Exists(thirdParty)) return false;
        try
        {
            return Directory.EnumerateDirectories(thirdParty, "lvgl-*").Any(d =>
                File.Exists(Path.Combine(d, "lvgl.h")) && File.Exists(Path.Combine(d, "CMakeLists.txt")));
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ───────────────────────── 下载安装 ─────────────────────────

    /// <summary>
    /// 按清单下载并安装。已装同一版本时直接成功（<see cref="KinSealSdkInstallResult.Installed"/> = false）。
    /// </summary>
    public async Task<KinSealSdkInstallResult> InstallAsync(
        string manifestUrl,
        IProgress<KinSealSdkProgress>? progress = null,
        CancellationToken ct = default)
    {
        var logs = new List<string>();
        void Info(string msg) { logs.Add(msg); Log?.Invoke(msg); }

        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            const string msg = "未配置 SDK 下载源（设置→真机部署 填写清单地址，或改用本地安装包）";
            Info($"[sdk] ✗ {msg}");
            return new KinSealSdkInstallResult { Success = false, Error = msg, Logs = logs };
        }

        var archivePath = "";
        try
        {
            using var http = CreateHttpClient();
            Info($"[sdk] 读取清单 {manifestUrl}");
            var manifest = await FetchManifestAsync(http, manifestUrl, ct).ConfigureAwait(false);
            Info($"[sdk] 清单版本 {manifest.Version}，安装包 {FormatSize(manifest.SizeBytes)}");

            if (string.IsNullOrWhiteSpace(manifest.Url))
                throw new InvalidDataException(
                    "SDK 清单未提供下载地址（url 为空）——随安装包分发的归档请改用本地安装包安装");

            var state = ReadState();
            if (state is not null
                && string.Equals(state.Version, manifest.Version, StringComparison.Ordinal)
                && string.Equals(state.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase)
                && VerifyLayout(SdkRoot, out _))
            {
                Info($"[sdk] 已是 {manifest.Version}，跳过");
                return new KinSealSdkInstallResult
                {
                    Success = true, Version = manifest.Version, Root = SdkRoot, Installed = false, Logs = logs,
                };
            }

            archivePath = AppPaths.NewTempFilePath("sdk-download", ArchiveExtension(manifest.Url));
            await DownloadAsync(http, manifest, archivePath, progress, ct).ConfigureAwait(false);

            return await InstallArchiveCoreAsync(
                archivePath, manifest.Sha256, manifest.Version, manifest.Url, progress, logs, Info, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Info("[sdk] 已取消");
            return new KinSealSdkInstallResult { Success = false, Error = "已取消", Logs = logs };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            Info($"[sdk] ✗ {ex.Message}");
            return new KinSealSdkInstallResult { Success = false, Error = ex.Message, Logs = logs };
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    /// <summary>
    /// 从本地安装包安装（离线/内网场景）。
    /// <paramref name="expectedSha256"/> 为空时只记录算出的值并在日志里标注"未与清单核对"。
    /// </summary>
    public async Task<KinSealSdkInstallResult> InstallFromArchiveAsync(
        string archivePath,
        string? expectedSha256 = null,
        string version = "",
        IProgress<KinSealSdkProgress>? progress = null,
        CancellationToken ct = default)
    {
        var logs = new List<string>();
        void Info(string msg) { logs.Add(msg); Log?.Invoke(msg); }

        try
        {
            if (!File.Exists(archivePath))
            {
                var msg = $"安装包不存在：{archivePath}";
                Info($"[sdk] ✗ {msg}");
                return new KinSealSdkInstallResult { Success = false, Error = msg, Logs = logs };
            }

            return await InstallArchiveCoreAsync(
                archivePath, expectedSha256, version, archivePath, progress, logs, Info, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Info("[sdk] 已取消");
            return new KinSealSdkInstallResult { Success = false, Error = "已取消", Logs = logs };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Info($"[sdk] ✗ {ex.Message}");
            return new KinSealSdkInstallResult { Success = false, Error = ex.Message, Logs = logs };
        }
    }

    private async Task<KinSealSdkInstallResult> InstallArchiveCoreAsync(
        string archivePath,
        string? expectedSha256,
        string version,
        string source,
        IProgress<KinSealSdkProgress>? progress,
        List<string> logs,
        Action<string> info,
        CancellationToken ct)
    {
        // ── 校验 ──
        progress?.Report(new KinSealSdkProgress(KinSealSdkInstallPhase.Verify, 0, "校验 SHA-256"));
        var actualSha = await ComputeSha256Async(archivePath, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (!string.Equals(actualSha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"校验失败：SHA-256 不匹配（清单 {expectedSha256}，实际 {actualSha}）";
                info($"[sdk] ✗ {msg}");
                return new KinSealSdkInstallResult { Success = false, Error = msg, Logs = logs };
            }
            info($"[sdk] SHA-256 匹配 {actualSha}");
        }
        else
        {
            info($"[sdk] ⚠ 未提供校验值，仅记录 SHA-256 {actualSha}");
        }

        // ── 解压到 staging（同级目录，保证最后一步 rename 是原子的） ──
        var parent = Path.GetDirectoryName(SdkRoot)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $"kinseal.staging-{Guid.NewGuid():N}");
        progress?.Report(new KinSealSdkProgress(KinSealSdkInstallPhase.Extract, 0, "解压安装包"));
        info($"[sdk] 解压到 {staging}");

        try
        {
            await ExtractArchiveAsync(archivePath, staging, info, ct).ConfigureAwait(false);

            var root = ResolveSdkRoot(staging);
            if (root is null)
            {
                const string noRuntime = "安装包内容不完整：找不到 runtime/（归档根下应只有 runtime/ 与 toolchain/）";
                info($"[sdk] ✗ {noRuntime}");
                return new KinSealSdkInstallResult { Success = false, Error = noRuntime, Logs = logs };
            }

            if (!VerifyLayout(root, out var layoutError))
            {
                var msg = $"安装包内容不完整：{layoutError}";
                info($"[sdk] ✗ {msg}");
                return new KinSealSdkInstallResult { Success = false, Error = msg, Logs = logs };
            }

            // 单层包裹目录（kinseal-sdk-1.0.0/…）时把内容提上一层，安装后目录形态固定
            if (!string.Equals(root, staging, StringComparison.Ordinal))
            {
                var inner = root;
                var lift = Path.Combine(parent, $"kinseal.lift-{Guid.NewGuid():N}");
                Directory.Move(inner, lift);
                Directory.Delete(staging, recursive: true);
                staging = lift;
            }

            // ── 换装 ──
            progress?.Report(new KinSealSdkProgress(KinSealSdkInstallPhase.Extract, 0.9, "写入 SDK 目录"));
            SwapIntoPlace(staging, SdkRoot);
            staging = "";   // 已被 move 走，finally 不需要清理

            var finalState = new KinSealSdkState
            {
                Version = string.IsNullOrWhiteSpace(version) ? "unknown" : version,
                Sha256 = actualSha,
                SourceUrl = source,
                SizeBytes = new FileInfo(archivePath).Length,
                InstalledAtUtc = DateTime.UtcNow,
            };
            await File.WriteAllTextAsync(
                Path.Combine(SdkRoot, StateFileName),
                JsonSerializer.Serialize(finalState, StateJson)).ConfigureAwait(false);

            progress?.Report(new KinSealSdkProgress(KinSealSdkInstallPhase.Done, 1, "完成"));
            info($"[sdk] ✓ 已安装 {finalState.Version} → {SdkRoot}");
            return new KinSealSdkInstallResult
            {
                Success = true, Version = finalState.Version, Root = SdkRoot, Installed = true, Logs = logs,
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging))
                TryDeleteDirectory(staging);
        }
    }

    // ───────────────────────── 下载 ─────────────────────────

    private HttpClient CreateHttpClient()
    {
        var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        // 大文件下载不能用整体超时：进度/取消一律靠 CancellationToken，由调用方决定预算。
        http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }

    private static async Task<KinSealSdkManifest> FetchManifestAsync(
        HttpClient http,
        string manifestUrl,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http
            .GetAsync(manifestUrl, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return KinSealSdkManifest.Parse(json);
    }

    private async Task DownloadAsync(
        HttpClient http,
        KinSealSdkManifest manifest,
        string destination,
        IProgress<KinSealSdkProgress>? progress,
        CancellationToken ct)
    {
        using var response = await http
            .GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = manifest.SizeBytes > 0
            ? manifest.SizeBytes
            : response.Content.Headers.ContentLength ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long written = 0;
        var lastReport = DateTime.UtcNow;
        progress?.Report(new KinSealSdkProgress(KinSealSdkInstallPhase.Download, total > 0 ? 0 : -1, "开始下载"));

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;

            // 断流/被替换的响应不应无限增长：声明了长度就以 2 倍为硬上限
            if (manifest.SizeBytes > 0 && written > manifest.SizeBytes * 2)
                throw new InvalidDataException($"下载长度异常（已收 {written} 字节，清单声明 {manifest.SizeBytes}）");

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 120)
            {
                lastReport = now;
                progress?.Report(new KinSealSdkProgress(
                    KinSealSdkInstallPhase.Download,
                    total > 0 ? Math.Min(1.0, (double)written / total) : -1,
                    total > 0 ? $"{FormatSize(written)} / {FormatSize(total)}" : FormatSize(written)));
            }
        }

        if (manifest.SizeBytes > 0 && written != manifest.SizeBytes)
            throw new InvalidDataException($"下载不完整（收到 {written} 字节，清单声明 {manifest.SizeBytes}）");

        progress?.Report(new KinSealSdkProgress(KinSealSdkInstallPhase.Download, 1, FormatSize(written)));
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ───────────────────────── 解压 ─────────────────────────

    /// <summary>
    /// 解压安装包到 <paramref name="destination"/>（zip / tar / tar.gz 按扩展名或魔数识别）。
    /// 每个条目都做越界校验；符号链接一类条目直接拒绝。
    /// </summary>
    private static async Task ExtractArchiveAsync(
        string archivePath, string destination, Action<string> info, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);

        if (await IsZipAsync(archivePath, ct).ConfigureAwait(false))
        {
            await ExtractZipAsync(archivePath, destination, ct).ConfigureAwait(false);
            return;
        }
        await ExtractTarAsync(archivePath, destination, info, ct).ConfigureAwait(false);
    }

    private static async Task<bool> IsZipAsync(string path, CancellationToken ct)
    {
        var header = new byte[4];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = await stream.ReadAsync(header, ct).ConfigureAwait(false);
        return read == 4 && header[0] == 'P' && header[1] == 'K' && header[2] is 3 or 5 or 7;
    }

    private static async Task ExtractZipAsync(string archivePath, string destination, CancellationToken ct)
    {
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(SafeJoin(destination, entry.FullName));
                continue;
            }

            var target = SafeJoin(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarAsync(
        string archivePath, string destination, Action<string> info, CancellationToken ct)
    {
        // 链接条目延后处理：目标可能比链接自己晚解压（tar 不保证顺序）
        var pendingLinks = new List<(string LinkPath, string LinkName, string ResolvedTarget)>();

        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream stream = file;
        if (await LooksGzippedAsync(archivePath, ct).ConfigureAwait(false))
            stream = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);

        await using (stream)
        {
            using var reader = new TarReader(stream);
            while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
            {
                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(SafeJoin(destination, entry.Name));
                    continue;
                }

                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    // 链接是合法内容（buildroot 的交叉 gcc 本身就是 -> toolchain-wrapper 的软链），
                    // 但不能凭 LinkName 就建：必须先解析出真实落点，仍在安装根内才允许。
                    var linkPath = SafeJoin(destination, entry.Name);
                    var resolved = ResolveLinkTarget(destination, linkPath, entry.LinkName);
                    pendingLinks.Add((linkPath, entry.LinkName ?? "", resolved));
                    continue;
                }

                // 设备节点/管道/FIFO：安装包不需要，且是越界写入的经典手段
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException($"安装包含不允许的条目类型（{entry.EntryType}）：{entry.Name}");

                var target = SafeJoin(destination, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                                 bufferSize: 81920, useAsync: true))
                {
                    if (entry.DataStream is not null)
                        await entry.DataStream.CopyToAsync(output, ct).ConfigureAwait(false);
                }

                // 权限必须照 tar 里的还原：工具链靠 +x，丢了就是"装好了但调不起来"
                if (!OperatingSystem.IsWindows())
                {
                    try { File.SetUnixFileMode(target, entry.Mode); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        throw new InvalidDataException($"无法还原文件权限（{entry.Mode}）：{entry.Name}", ex);
                    }
                }
            }
        }

        // 全部文件落盘后再建链接：目标此时一定已解压
        var unresolved = 0;
        foreach (var (linkPath, linkName, resolved) in pendingLinks)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

            if (TryCreateLink(linkPath, linkName, resolved))
                continue;

            // Windows 无建链接权限时退化成复制（对 ld/include 查找等价，只是不再共享 inode）
            if (File.Exists(resolved))
            {
                try
                {
                    File.Copy(resolved, linkPath, overwrite: true);
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            unresolved++;
        }

        if (unresolved > 0)
            info($"[sdk] ⚠ {unresolved} 个符号链接未能还原（目标缺失或无权限），工具链可能不完整");
    }

    /// <summary>
    /// 解析链接目标并校验落点在安装根内。返回解析后的绝对路径（可能尚不存在）。
    /// 绝对路径目标一律拒绝 —— 那会指到安装目录之外。
    /// </summary>
    private static string ResolveLinkTarget(string root, string linkPath, string? linkName)
    {
        if (string.IsNullOrWhiteSpace(linkName))
            throw new InvalidDataException($"安装包含空目标链接：{linkPath}");

        if (Path.IsPathRooted(linkName))
            throw new InvalidDataException($"安装包含绝对路径链接（拒绝）：{linkPath} -> {linkName}");

        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        // 相对目标是相对**链接所在目录**解析的（tar 语义），不是相对安装根
        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, linkName));
        if (!resolved.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(resolved, rootFull, StringComparison.Ordinal))
            throw new InvalidDataException($"安装包含越界链接（拒绝）：{linkPath} -> {linkName}");

        return resolved;
    }

    private static bool TryCreateLink(string linkPath, string linkName, string resolvedTarget)
    {
        try
        {
            // 用**原始相对目标**建链：解压目录整体搬家后链接依然有效
            if (File.Exists(linkPath) || Directory.Exists(linkPath)) return true;
            File.CreateSymbolicLink(linkPath, linkName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> LooksGzippedAsync(string path, CancellationToken ct)
    {
        var header = new byte[2];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = await stream.ReadAsync(header, ct).ConfigureAwait(false);
        return read == 2 && header[0] == 0x1f && header[1] == 0x8b;
    }

    /// <summary>
    /// 把条目名拼到根下，并保证结果仍在根内。
    /// 安装包来自网络：<c>../../etc/passwd</c> 这种条目必须当场拒绝，而不是"写到外面去试试"。
    /// </summary>
    private static string SafeJoin(string root, string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0)
            throw new InvalidDataException("安装包含空条目名。");

        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(full, rootFull, StringComparison.Ordinal))
            throw new InvalidDataException($"安装包含越界路径：{entryName}");

        return full;
    }

    /// <summary>
    /// 找出真正的 SDK 根：内容直接在顶层，或被包在单层目录里（<c>kinseal-sdk-1.0.0/runtime/…</c>）。
    /// 形态不符返回 null（由调用方给出"内容不完整"）。
    /// </summary>
    private static string? ResolveSdkRoot(string staging)
    {
        if (Directory.Exists(Path.Combine(staging, "runtime"))) return staging;

        string[] dirs;
        try { dirs = Directory.GetDirectories(staging); }
        catch (IOException) { return null; }

        if (dirs.Length != 1) return null;
        return Directory.Exists(Path.Combine(dirs[0], "runtime")) ? dirs[0] : null;
    }

    // ───────────────────────── 落盘 ─────────────────────────

    /// <summary>
    /// 用 staging 取代正式目录；失败回滚旧目录（旧目录改名备份，成功后才删）。
    /// </summary>
    private static void SwapIntoPlace(string staging, string target)
    {
        var backup = target + ".old-" + Guid.NewGuid().ToString("N");
        var movedOld = false;
        if (Directory.Exists(target))
        {
            Directory.Move(target, backup);
            movedOld = true;
        }

        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            if (movedOld && !Directory.Exists(target)) Directory.Move(backup, target);
            throw;
        }

        if (movedOld) TryDeleteDirectory(backup);
    }

    private static string ArchiveExtension(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ".tar.gz";
        if (path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return ".tgz";
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ".zip";
        if (path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return ".tar";
        return ".bin";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "大小未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
