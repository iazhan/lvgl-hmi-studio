using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HmiEditor.App.Views;
using HmiEditor.TestSupport;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 「新建工程」对话框的三件事：型号一换参数摘要跟着变、名字/目录不合法就地拦下、
/// 合法输入返回**精确的落盘路径**。
///
/// <para>为什么值得判：这三条都是"错了不报错"的形状 —— 摘要不刷新等于让用户按错型号建工程；
/// 空名字放行会存成 `Untitled.hmi`；路径拼错则工程建完落到别处（或覆盖同名工程）。</para>
/// </summary>
public class NewProjectViewTests
{
    private static string NewTempDir() => TestTempDir.NewDir("chenge-newproj");

    private static void Click(Window view, string name) =>
        view.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Dispose(Window view)
    {
        if (view.IsVisible) view.Close();
        UiTestHost.Pump();
    }

    [Fact]
    public void TargetSelection_FillsSummaryRows()
    {
        using var sandbox = new DataRootSandbox("newproject-summary");

        UiTestHost.Run(() =>
        {
            _ = new WindowDepot(sandbox.DataRoot);
            var view = new NewProjectView(NewTempDir());
            view.Show();
            UiTestHost.Pump(3);
            try
            {
                // 前提自检：默认就选中了一个型号（否则摘要那一片全是空）
                Assert.NotNull(view.FindControl<ComboBox>("CmbTarget")!.SelectedItem);

                var resolution = view.FindControl<TextBlock>("TxtSumResolution")!.Text;
                Assert.False(string.IsNullOrWhiteSpace(resolution));
                Assert.Contains("×", resolution!, StringComparison.Ordinal);

                // RAM / Flash 是预算判读的入口，不能空着
                Assert.False(string.IsNullOrWhiteSpace(view.FindControl<TextBlock>("TxtSumRam")!.Text));
                Assert.False(string.IsNullOrWhiteSpace(view.FindControl<TextBlock>("TxtSumFlash")!.Text));
                Assert.False(string.IsNullOrWhiteSpace(view.FindControl<TextBlock>("TxtSumVendor")!.Text));
            }
            finally
            {
                Dispose(view);
            }
        });
    }

    [Fact]
    public void Create_RequiresName_ThenReturnsTrimmedPath()
    {
        using var sandbox = new DataRootSandbox("newproject-create");

        UiTestHost.Run(() =>
        {
            _ = new WindowDepot(sandbox.DataRoot);
            var dir = NewTempDir();
            var view = new NewProjectView(dir);
            view.Show();
            UiTestHost.Pump(3);
            try
            {
                // 空名字：不许建，错误就地显示
                Click(view, "BtnCreate");
                Assert.Null(view.Result);
                Assert.True(view.FindControl<TextBlock>("TxtError")!.IsVisible);

                // 名字 + 合法目录：返回值里是 Trim 后的名字与 <名称>.hmi
                view.FindControl<TextBox>("TxtProjectName")!.Text = "  炉温监控  ";
                Click(view, "BtnCreate");

                Assert.NotNull(view.Result);
                Assert.Equal("炉温监控", view.Result!.Name);
                Assert.Equal(Path.Combine(dir, "炉温监控.hmi"), view.Result.FilePath);
            }
            finally
            {
                Dispose(view);
                TestTempDir.DeleteDir(dir);
            }
        });
    }

    [Fact]
    public void Create_RefusesToOverwriteExistingProject()
    {
        using var sandbox = new DataRootSandbox("newproject-exists");

        UiTestHost.Run(() =>
        {
            _ = new WindowDepot(sandbox.DataRoot);
            var dir = NewTempDir();
            File.WriteAllText(Path.Combine(dir, "dup.hmi"), "{}");
            var view = new NewProjectView(dir);
            view.Show();
            UiTestHost.Pump(3);
            try
            {
                view.FindControl<TextBox>("TxtProjectName")!.Text = "dup";
                Click(view, "BtnCreate");

                // 同名工程**不覆盖**：拒绝并给出文件名，用户自己改名或换目录
                Assert.Null(view.Result);
                var error = view.FindControl<TextBlock>("TxtError")!;
                Assert.True(error.IsVisible);
                Assert.Contains("dup.hmi", error.Text!, StringComparison.Ordinal);
            }
            finally
            {
                Dispose(view);
                TestTempDir.DeleteDir(dir);
            }
        });
    }
}
