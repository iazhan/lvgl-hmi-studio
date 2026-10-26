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
                BuildChengeExportOptions(), _settingsService, canvasView.Canvas);
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
