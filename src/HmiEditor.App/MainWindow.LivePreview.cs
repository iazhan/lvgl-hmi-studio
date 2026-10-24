using System;
using Avalonia.Controls;
using HmiEditor.App.Resources;
using HmiEditor.App.Views;

namespace HmiEditor.App;

/// <summary>
/// 实时预览入口（单独一个分部文件：MainWindow.axaml.cs 有行数基线，别把窗口打开逻辑塞回去）。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 把运行时当库加载，画面可点击/拖动。
    ///
    /// <para>与「导出并预览」的分工：那个起进程截一张图（验收产物、看日志）；
    /// 这个在编辑器里真的操作，用来验控件的点击/拖动行为。</para>
    /// </summary>
    private async void ShowLivePreview()
    {
        try
        {
            if (_currentProject == null)
            {
                StatusText.Text = LocService["chenge.export.no_project"];
                return;
            }

            var dialog = new ChengeLivePreviewView(
                _currentProject, _assetLibrary, _projectDirectory,
                BuildChengeExportOptions(), _settingsService);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(Lang.UiOpExportCode, ex);
        }
    }
}
