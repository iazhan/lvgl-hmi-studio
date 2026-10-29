using System.Text.Json;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

public class PropertyPanelCoverageExportTests
{
    [Fact]
    public void CommonGeometryAndVisibility_ReachPageJson()
    {
        var project = new HmiProject { Name = "coverage", HardwareTargetId = "f1c100s-kinseal" };
        var screen = new HmiScreen { Id = "s1", Name = "Main", Width = 800, Height = 480 };
        screen.Controls.Add(new HmiControl
        {
            Id = "c1",
            Type = HmiControlType.Label,
            Name = "Label1",
            X = 35,
            Y = 45,
            Width = 180,
            Height = 32,
            IsVisible = false,
            IsEnabled = false,
            Tooltip = "长按提示",
            Opacity = 42,
            BgColor = "#112233",
            BorderColor = "#445566",
            BorderWidth = 3,
            Radius = 7,
            Text = "coverage",
        });
        project.Screens.Add(screen);

        var output = TestTempDir.NewDir("property-panel-coverage-export");
        try
        {
            var result = ExportFixture.Export(project, output);
            Assert.True(result.Success, string.Join("\n", result.Issues.Items));

            var pageId = result.PageIds[screen.Id];
            using var pageDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "pages", $"{pageId}.json")));
            var widget = pageDocument.RootElement.GetProperty("widgets")[0];
            Assert.Equal(35, widget.GetProperty("x").GetInt32());
            Assert.Equal(45, widget.GetProperty("y").GetInt32());
            Assert.Equal(180, widget.GetProperty("w").GetInt32());
            Assert.Equal(32, widget.GetProperty("h").GetInt32());

            var props = widget.GetProperty("props");
            Assert.False(props.GetProperty("visible").GetBoolean());
            Assert.True(props.TryGetProperty("enabled", out var enabled), "enabled=false 必须进入产物");
            Assert.True(props.TryGetProperty("tooltip", out var tooltip), "tooltip 非空必须进入产物");
            Assert.False(enabled.GetBoolean());
            Assert.Equal("长按提示", tooltip.GetString());
            Assert.Equal(107, props.GetProperty("opacity").GetInt32());
            Assert.Equal("#112233", props.GetProperty("bg_color").GetString());
            Assert.Equal("#445566", props.GetProperty("border_color").GetString());
            Assert.Equal(3, props.GetProperty("border_width").GetInt32());
            Assert.Equal(7, props.GetProperty("radius").GetInt32());
        }
        finally
        {
            TestTempDir.DeleteDir(output);
        }
    }
}
