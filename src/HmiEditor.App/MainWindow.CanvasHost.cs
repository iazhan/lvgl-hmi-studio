using System;
using Avalonia.Controls;
using HmiEditor.App.Resources;
using HmiEditor.App.Services;
using HmiEditor.App.Views;

namespace HmiEditor.App;

/// <summary>
/// 设计画布「真机渲染」开关（独立分部：MainWindow.axaml.cs 有行数基线）。
///
/// 打开时把底图换成宿主渲染出的真机位图，关掉回 C# 自绘。切换期间画布上的
/// 选中框/手柄/网格都不变 —— 它们本来就画在叠加层、位置取模型。
/// </summary>
public partial class MainWindow
{
    private DesignHostPreview? _designHost;

    /// <summary>
    /// 工程/屏幕就绪后自动挂上真机渲染（默认帧源）。
    ///
    /// <para>失败不打断：没编宿主、没有工程、导出失败都只写状态栏，画布回退 C# 自绘。
    /// 刚 clone 的环境本来就没有 chenge-preview-host，"打开工程就报错"不能接受。</para>
    /// </summary>
    /// <summary>
    /// 是否在工程就绪时自动挂真机渲染（默认 true）。测试置 false：那里没有宿主进程、
    /// 也不该起进程，画布回退 C# 自绘即可 —— 判据测的是编辑器逻辑，不是渲染来源。
    /// </summary>
    public static bool AutoStartDesignHost { get; set; } = true;

    private string? _designHostScreenId;

    /// <summary>属性面板几何框上次刷新的时刻（拖动中节流到 100ms 一次）。</summary>
    private long _lastGeometryRefreshMs;

    /// <summary>
    /// 工程/屏幕切换后对齐真机画面。宿主有自己的页面栈，切屏后它仍停在旧页 ——
    /// 位图会一直显示上一屏。现阶段整会话重启是最省的正确解；下一步改成发 G 切页 +
    /// 推该页 JSON，免掉这 1–2 秒。
    /// </summary>
    private void SyncDesignHostScreen()
    {
        if (!AutoStartDesignHost) return;
        var screenId = _currentScreen?.Id;
        if (_designHost?.IsRunning != true)
        {
            _designHostScreenId = screenId;
            TryStartDesignHostAsync();
            return;
        }

        if (string.Equals(screenId, _designHostScreenId, StringComparison.Ordinal)) return;
        Console.WriteLine($"[design] 切屏 {_designHostScreenId} → {screenId}：重启宿主");
        _designHostScreenId = screenId;
        _designHost.Stop();
        _designHost = null;
        TryStartDesignHostAsync();
    }

    private async void TryStartDesignHostAsync()
    {
        if (_designHost?.IsRunning == true) return;
        if (_currentProject == null) return;

        try
        {
            var canvasView = GetActiveView<DesignCanvasView>("Canvas") ?? CanvasPanel;
            var host = new DesignHostPreview();
            var ok = await host.StartAsync(_currentProject, _assetLibrary, _projectDirectory,
                BuildChengeExportOptions(), _settingsService, _currentScreen?.Id, canvasView.Canvas);
            if (!ok)
            {
                var why = host.LastError;
                host.Dispose();
                StatusText.Text = why;
                return;
            }

            _designHost = host;
            StatusText.Text = AppLocalization.Service["canvas.host.on"];
        }
        catch (Exception ex)
        {
            ReportOperationFailure(Lang.UiOpExportCode, ex);
        }
    }

    private async void ToggleCanvasHostFrame()
    {
        try
        {
            if (_designHost?.IsRunning == true)
            {
                _designHost.Stop();
                StatusText.Text = AppLocalization.Service["canvas.host.off"];
                return;
            }

            if (_currentProject == null)
            {
                StatusText.Text = LocService["chenge.export.no_project"];
                return;
            }

            var canvasView = GetActiveView<DesignCanvasView>("Canvas") ?? CanvasPanel;
            var host = new DesignHostPreview();
            var ok = await host.StartAsync(_currentProject, _assetLibrary, _projectDirectory,
                BuildChengeExportOptions(), _settingsService, _currentScreen?.Id, canvasView.Canvas);
            if (!ok)
            {
                var why = host.LastError;
                host.Dispose();
                StatusText.Text = why;
                return;
            }

            _designHost = host;
            StatusText.Text = AppLocalization.Service["canvas.host.on"];
        }
        catch (Exception ex)
        {
            ReportOperationFailure(Lang.UiOpExportCode, ex);
        }
    }
}
