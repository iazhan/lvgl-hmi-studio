using Avalonia.Controls;
using FluentAvaloniaUI = FluentAvalonia.UI.Controls;
using HmiEditor.App.Views;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 历史表的「最大行数」行 —— 契约 <c>history_table.max_rows</c>（10..2000，缺省 500）。
///
/// <para>这条判据存在的理由：模型字段与导出器早就有（<c>HistTableMaxRows</c> →
/// <c>max_rows</c>），缺的一直是面板入口，用户改不了 ⇒ 历史表恒按 500 行排。
/// 所以这里判两件事：① 这一行只在历史表格下出现；② 在框里改值真的写回模型
/// （写了但不回写的话，切走再点开会退回默认值，看着像"没生效"）。</para>
/// </summary>
public class HistoryTableMaxRowsPanelTests
{
    private static HmiControl Make(HmiControlType type) => new()
    {
        Id = "c1",
        Type = type,
        Name = "n1",
        X = 10, Y = 20, Width = 200, Height = 120,
    };

    private static PropertyPanelView ShowPanel(HmiControl control)
    {
        var project = new HmiProject { Name = "P" };
        var screen = new HmiScreen { Id = "s1", Name = "Main" };
        project.Screens.Add(screen);

        var panel = new PropertyPanelView { CurrentScreen = screen };
        var window = new Window { Width = 900, Height = 900, Content = panel };
        window.Show();
        panel.SetProject(project);
        panel.BindControl(control);
        UiTestHost.Pump();
        return panel;
    }

    [Fact]
    public void MaxRowsRow_IsVisibleForHistoryTableOnly_AndWritesTheModel()
    {
        UiTestHost.Run(() =>
        {
            var table = Make(HmiControlType.HistoryTable);
            var panel = ShowPanel(table);

            var box = ControlWiringFace.Find<Control>(panel, "HistMaxRowsBox");
            var label = ControlWiringFace.Find<Control>(panel, "LblHistMaxRows");
            Assert.True(box.IsVisible && label.IsVisible, "历史表格下「最大行数」行必须显示");

            // 回填：面板要显示模型里的 500（契约缺省），不是 0
            var nud = box as FluentAvaloniaUI.FANumberBox;
            Assert.NotNull(nud);
            Assert.Equal(500, nud!.Value);

            nud.Value = 250;
            UiTestHost.Pump();
            Assert.Equal(250, table.HistTableMaxRows);
        });
    }

    [Fact]
    public void MaxRowsRow_IsHiddenForOtherTypes()
    {
        UiTestHost.Run(() =>
        {
            var panel = ShowPanel(Make(HmiControlType.Label));

            var box = ControlWiringFace.Find<Control>(panel, "HistMaxRowsBox");
            Assert.False(box.IsVisible,
                "标签没有历史表参数：这一行必须整行收起（隐藏行不占位，见 PropertyPanelLayoutTests）");
        });
    }
}
