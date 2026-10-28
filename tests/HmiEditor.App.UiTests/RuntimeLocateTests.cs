using HmiEditor.App.Services;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 运行时定位判据。
///
/// <para>这一组守的是**安装包形态**：随包把运行时放在
/// <c>&lt;安装目录&gt;/chenge-runtime/</c> 下，用户装完就能直接画布预览，
/// 不必再手工填"运行时目录"。同时也是"别猜路径"的判据 —— 找不到就返回 null，
/// 不做向上遍历、不假设平级仓库（那样会在作者机器上能跑、在用户机器上乱指）。</para>
/// </summary>
public sealed class RuntimeLocateTests : IDisposable
{
    private readonly string _root;
    private readonly string? _savedEnv;

    public RuntimeLocateTests()
    {
        // 仓库纪律：测试不碰系统临时目录（/tmp 是 10MB tmpfs，全量并行会写爆）
        _root = TestTempDir.NewDir("runtime-locate");

        // 环境变量是候选之一：测试机若真设了它，会把"找不到"的用例带偏
        _savedEnv = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("CHENGE_RUNTIME_DIR", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CHENGE_RUNTIME_DIR", _savedEnv);
        TestTempDir.DeleteDir(_root);
    }

    private string CreateBinary(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ChengeExportService.BinaryName);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void BinaryName_FollowsThePlatform()
        => Assert.Equal(OperatingSystem.IsWindows() ? "chenge-runtime.exe" : "chenge-runtime",
            ChengeExportService.BinaryName);

    [Fact]
    public void Locate_FindsRuntimeBundledNextToTheEditor()
    {
        var bundled = CreateBinary(Path.Combine(_root, "chenge-runtime"));

        var hit = ChengeExportService.LocateRuntime(null, _root);

        Assert.NotNull(hit);
        Assert.Equal(bundled, hit!.BinaryPath);
        Assert.Equal(Path.Combine(_root, "chenge-runtime"), hit.RuntimeDir);
    }

    [Fact]
    public void Locate_AcceptsPortableLayout_BinaryBesideTheEditor()
    {
        var beside = CreateBinary(_root);

        var hit = ChengeExportService.LocateRuntime(null, _root);

        Assert.NotNull(hit);
        Assert.Equal(beside, hit!.BinaryPath);
    }

    [Fact]
    public void Locate_PrefersTheConfiguredFolderOverTheBundledOne()
    {
        CreateBinary(Path.Combine(_root, "chenge-runtime"));
        var overrideDir = Path.Combine(_root, "dev-build");
        var overrideBin = CreateBinary(overrideDir);

        // 设置项优先级最高（临时指到别处调试时不希望被随包那份遮住）
        using var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Chenge.SetRuntimeDir(overrideDir);
        var hit = ChengeExportService.LocateRuntime(settings, _root);

        Assert.NotNull(hit);
        Assert.Equal(overrideBin, hit!.BinaryPath);
    }

    [Fact]
    public void Locate_ReturnsNullAndReportsWhereItLooked()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.Null(ChengeExportService.LocateRuntime(null, empty));

        // 报错要能说清"找过哪儿"：装完提示"没有运行时"却不给路径，只能靠猜
        var dirs = ChengeExportService.RuntimeSearchDirs(null, empty);
        Assert.Contains(Path.Combine(empty, "chenge-runtime"), dirs);
        Assert.Contains(empty, dirs);
        Assert.DoesNotContain(dirs, d => d.Contains("..", StringComparison.Ordinal));
    }
}
