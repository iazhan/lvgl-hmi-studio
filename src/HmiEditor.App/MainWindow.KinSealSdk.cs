using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Hmi.Hardware.Builtin;
using HmiEditor.App.Resources;
using HmiEditor.App.Services;
using HmiEditor.Device.Build;
using HmiEditor.Core.Services;

namespace HmiEditor.App;

/// <summary>
/// MainWindow 的 <b>kinseal SDK 前置</b>部分：编译（「编译转换」）与「编译并运行」在动手前
/// 先把 SDK 备好。
///
/// <para><b>为什么单独一个文件</b>：<c>MainWindow.axaml.cs</c> 有行数守卫（相等判定）。
/// 这两段逻辑是「新职责」，析出后主文件不被推高，也让"什么时候需要 SDK"只有一个地方可读。</para>
///
/// <para><b>为什么必须先装再编</b>：kinseal 的交叉编译要 buildroot 工具链 + 设备侧运行时契约头，
/// 「编译并运行」要运行时自带的 LVGL 树 —— 缺任一项都只会得到一条难懂的编译错误。
/// SDK 随安装包分发（<c>&lt;install&gt;/sdk/kinseal-sdk-*.tar.gz</c>），首次使用时校验后
/// 装进用户目录（<see cref="KinSealSdkBootstrapper"/>）。</para>
///
/// <para><b>只对 kinseal 生效</b>：其他目标的工具链另有来源（T113 走 Tina SDK），
/// 不能因为"顺手"就给所有工程装一遍 SDK。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>「编译转换」入口：先备 SDK，再走原有编译流水线。</summary>
    private async Task CompileProjectWithSdkAsync()
    {
        if (!await EnsureKinSealSdkAsync()) return;
        await _buildPipeline.CompileAsync();
    }

    /// <summary>
    /// 「编译并运行」入口：确定工作目录 → 备 SDK → 探测 LVGL 树 → 打开对话框。
    /// </summary>
    private async Task BuildAndRunSimulatorAsync()
    {
        if (_currentProject == null)
        {
            StatusText.Text = LocService["gen.create.or.open.project"];
            return;
        }

        // 1. 确定模拟器工作目录（可配置，默认可清理临时根，支持增量编译复用）
        var workDir = _settingsService.Simulator.WorkDir;
        if (string.IsNullOrWhiteSpace(workDir))
        {
            var safeName = string.Concat(_currentProject.Name.Split(Path.GetInvalidFileNameChars()));
            workDir = AppPaths.TempSubdir("sim", safeName);
        }

        // 2. 探测 LVGL 源码目录（用户配置 → 环境变量 LVGL_DIR → 安装包内置 SDK → 工程同级 ../lvgl）
        var lvglDir = SimulatorBuildService.FindLvglDirectory(_settingsService.Simulator.LvglDir, workDir);
        if (string.IsNullOrEmpty(lvglDir))
        {
            // SDK 里带着 LVGL 树：首次使用先装，装完再探测一次
            if (!await EnsureKinSealSdkAsync()) return;
            lvglDir = SimulatorBuildService.FindLvglDirectory(_settingsService.Simulator.LvglDir, workDir);
            if (string.IsNullOrEmpty(lvglDir))
            {
                StatusText.Text = LocService["buildrun.no.lvgl"];
                return;
            }
        }

        // 3. 打开编译并运行对话框（Load 后自动开始编译）；不等它关，保持非模态调用语义
        var dialog = new Views.BuildRunDialog(_currentProject, workDir, lvglDir, _assetLibrary);
        _ = dialog.ShowDialog(this);
    }

    /// <summary>
    /// 确保 kinseal SDK 可用（只对该目标生效；已安装时只是一次目录检查，无重活）。
    /// 失败返回 false 并把原因写到状态栏 —— 调用方直接放弃，不去编一个必然失败的工程。
    /// </summary>
    private async Task<bool> EnsureKinSealSdkAsync()
    {
        if (_currentProject?.HardwareTargetId != AllwinnerF1C100STarget.TargetId) return true;

        var result = await KinSealSdkBootstrapper.EnsureAsync(
            AppContext.BaseDirectory, _settingsService.KinSeal.SdkManifestUrl);
        if (result.Success) return true;

        StatusText.Text = LocService.Format(Lang.SdkKinSealUnavailable, result.Error ?? "");
        return false;
    }
}
