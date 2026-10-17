using System;
using System.IO;

namespace HmiEditor.TestSupport;

/// <summary>
/// 把 kinseal SDK 根指到一个**唯一的空目录**（<c>HMI_KINSEAL_SDK_ROOT</c>），用完还原。
///
/// <para><b>为什么要它</b>：SDK 默认装在用户数据目录里，而数据根在测试进程里被重定向到
/// 仓库内共享的 <c>.hmi-test-tmp/appdata</c> —— 另一个程序集装过一次（或有人手工装过），
/// 「没有 SDK 时应该是 null / 应该报缺失」这类断言就会莫名其妙变红。让每个用例拿到自己的空目录，
/// 结论只取决于本用例写了什么。</para>
///
/// <para>环境变量名与 <c>KinSealSdkInstaller.SdkRootEnvVar</c> 同值；这里写字面量是为了
/// 不让 TestSupport 反向依赖 Device 程序集（漂移由该常量上的注释与本注释共同约束）。</para>
/// </summary>
public sealed class SdkRootSandbox : IDisposable
{
    private const string EnvVar = "HMI_KINSEAL_SDK_ROOT";

    private readonly string? _previous;

    /// <summary>本用例专属的 SDK 根（已创建，空）。</summary>
    public string Root { get; }

    public SdkRootSandbox()
    {
        _previous = Environment.GetEnvironmentVariable(EnvVar);
        Root = TestTempDir.NewDir("sdkroot");
        Environment.SetEnvironmentVariable(EnvVar, Root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _previous);
        TestTempDir.DeleteDir(Root);
    }
}
