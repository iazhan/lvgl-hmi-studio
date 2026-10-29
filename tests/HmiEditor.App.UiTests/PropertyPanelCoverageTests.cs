using Avalonia.Controls;
using System.Linq;
using HmiEditor.App.Views;
using HmiEditor.Core.Models;
using HmiEditor.Export.Chenge;
using HmiEditor.TestSupport;
using Xunit;

namespace HmiEditor.App.UiTests;

public class PropertyPanelCoverageTests
{
    [Fact]
    public void GeometryAndVisibilityEditors_AreVisibleAndWriteBack()
    {
        UiTestHost.Run(() =>
        {
            var control = new HmiControl
            {
                Id = "c1",
                Type = HmiControlType.Label,
                Name = "Label1",
                X = 10,
                Y = 20,
                Width = 120,
                Height = 40,
            };
            var screen = new HmiScreen { Id = "s1", Name = "Main" };
            screen.Controls.Add(control);

            var panel = new PropertyPanelView { CurrentScreen = screen };
            var window = new Window { Width = 720, Height = 760, Content = panel };
            panel.SetProject(new HmiProject { Screens = { screen } });
            panel.BindControl(control);
            window.Show();
            UiTestHost.Pump(3);

            var geometry = panel.FindControl<Control>("GeometryExpander");
            Assert.NotNull(geometry);
            Assert.True(geometry!.IsVisible);
            Assert.True(geometry.FindControl<Control>("PropX")!.IsVisible);
            Assert.True(geometry.FindControl<Control>("PropY")!.IsVisible);
            Assert.True(geometry.FindControl<Control>("PropWidth")!.IsVisible);
            Assert.True(geometry.FindControl<Control>("PropHeight")!.IsVisible);
            Assert.True(geometry.FindControl<Control>("PropVisible")!.IsVisible);
            Assert.True(geometry.FindControl<Control>("PropEnabled")!.IsVisible);
            Assert.True(geometry.FindControl<Control>("TooltipBox")!.IsVisible);

            var schema = ChengeSchema.Load();
            Assert.NotNull(schema.PropDef("label", "visible"));
            Assert.NotNull(schema.PropDef("label", "opacity"));
            Assert.NotNull(schema.PropDef("label", "bg_color"));
            Assert.NotNull(schema.PropDef("label", "border_color"));
            Assert.NotNull(schema.PropDef("label", "border_width"));
            Assert.NotNull(schema.PropDef("label", "radius"));

            panel.FindControl<FluentAvalonia.UI.Controls.FANumberBox>("PropX")!.Value = 35;
            UiTestHost.Pump();
            Assert.Equal(35, control.X);

            panel.FindControl<CheckBox>("PropVisible")!.IsChecked = false;
            UiTestHost.Pump();
            Assert.False(control.IsVisible);

            panel.FindControl<TextBox>("TooltipBox")!.Text = "提示";
            UiTestHost.Pump();
            panel.FindControl<CheckBox>("PropEnabled")!.IsChecked = false;
            UiTestHost.Pump();
            Assert.False(control.IsEnabled);
            Assert.Equal("提示", control.Tooltip);

            window.Close();
            UiTestHost.Pump();
        });
    }

    [Fact]
    public void PanelSections_ExposeTheNewUserPaths()
    {
        UiTestHost.Run(() =>
        {
            var panel = new PropertyPanelView();
            Assert.NotNull(panel.FindControl<Control>("GeometryExpander"));
            Assert.NotNull(panel.FindControl<Control>("ContentAppearanceSection"));
            Assert.NotNull(panel.FindControl<Control>("BindingStateSection"));
            Assert.NotNull(panel.FindControl<Control>("DataDisplaySection"));
            Assert.NotNull(panel.FindControl<Control>("ActionsAdvancedSection"));
        });
    }

    [Fact]
    public void NormalState_DoesNotDuplicateDefaultTextEditor()
    {
        UiTestHost.Run(() =>
        {
            var screen = new HmiScreen { Id = "s1", Name = "Main" };
            var control = new HmiControl { Id = "c1", Name = "Button1", Type = HmiControlType.Button, Text = "默认" };
            screen.Controls.Add(control);
            var panel = new PropertyPanelView { CurrentScreen = screen };
            var window = new Window { Width = 720, Height = 760, Content = panel };
            panel.SetProject(new HmiProject { Screens = { screen } });
            panel.BindControl(control);
            window.Show();
            UiTestHost.Pump(3);

            var state = panel.FindControl<ComboBox>("CboVisualState")!;
            Assert.Equal(0, state.SelectedIndex);
            Assert.False(panel.FindControl<TextBox>("VsText")!.IsVisible);
            Assert.True(panel.FindControl<Control>("VsTextDefaultHint")!.IsVisible);

            state.SelectedIndex = 1;
            UiTestHost.Pump(2);
            Assert.True(panel.FindControl<TextBox>("VsText")!.IsVisible);
            Assert.False(panel.FindControl<Control>("VsTextDefaultHint")!.IsVisible);

            window.Close();
            UiTestHost.Pump();
        });
    }

    [Fact]
    public void ContractProjection_HasTheExpectedWidgetAndFigureSurface()
    {
        var schema = ChengeSchema.Load().Root;
        Assert.Equal(34, schema.GetProperty("widgets").EnumerateObject().Count());
        Assert.Equal(40, schema.GetProperty("figures").EnumerateObject().Count());
        Assert.Equal(18, schema.GetProperty("common").EnumerateObject().Count());
        Assert.Equal(283, schema.GetProperty("widgets").EnumerateObject()
            .Sum(widget => widget.Value.GetProperty("props").EnumerateObject().Count()));
    }

    [Fact]
    public void AppearanceRows_AreHiddenWhenTheRuntimeDoesNotConsumeThem()
    {
        UiTestHost.Run(() =>
        {
            var screen = new HmiScreen { Id = "s1", Name = "Main" };
            var control = new HmiControl { Id = "c1", Name = "Slider1", Type = HmiControlType.Slider };
            screen.Controls.Add(control);

            var panel = new PropertyPanelView { CurrentScreen = screen };
            var window = new Window { Width = 720, Height = 760, Content = panel };
            panel.SetProject(new HmiProject { Screens = { screen } });
            panel.BindControl(control);
            window.Show();
            UiTestHost.Pump(3);

            Assert.True(panel.FindControl<Control>("GeometryExpander")!.IsVisible);
            Assert.True(panel.FindControl<Control>("PropVisible")!.IsVisible);
            Assert.False(panel.FindControl<Control>("RowTextColor")!.IsVisible);
            Assert.False(panel.FindControl<Control>("RowFontSize")!.IsVisible);
            Assert.True(panel.FindControl<Control>("RowBgColor")!.IsVisible);
            Assert.True(panel.FindControl<Control>("RowOpacity")!.IsVisible);

            window.Close();
            UiTestHost.Pump();
        });
    }
}
