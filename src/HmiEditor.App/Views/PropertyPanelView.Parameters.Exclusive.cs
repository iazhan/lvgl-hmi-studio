using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using HmiEditor.App.Resources;
using HmiEditor.Core.Design;
using HmiEditor.Core.Models;

namespace HmiEditor.App.Views;

/// <summary>
/// 「专属参数」区：运行时确实读了、但此前在面板上没有可编辑行的属性。
///
/// <para><b>显隐单位是整行</b>：静态行在 XAML 里各是一个具名 <c>Grid</c>（<c>RowXxx</c>），
/// 图元 kind 级行由 <see cref="FigurePropertyCatalog"/> 动态生成 —— 键名、缺省、范围只登记
/// 在那一张表里，面板与导出器都读它，不在这里另抄一份 kind→属性清单。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>动态生成的行：属性键 → 编辑器（回填与写回都按这张表走）。</summary>
    private readonly Dictionary<string, Control> _figureEditors = new(StringComparer.Ordinal);

    private string? _figureKindBuilt;

    private static bool IsFigureWidget(HmiControlType type) => type is
        HmiControlType.Shape or HmiControlType.Rectangle or HmiControlType.Ellipse or
        HmiControlType.Line or HmiControlType.Polyline or HmiControlType.Arc or
        HmiControlType.Tank or HmiControlType.Valve;

    /// <summary>控件对应的图元 kind（与导出器 ChengeWidgetMapper 的推断规则一致）。</summary>
    private static string FigureKindOf(HmiControl control)
    {
        switch (control.Type)
        {
            case HmiControlType.Shape:
                return string.IsNullOrWhiteSpace(control.ShapeKind) ? "rect" : control.ShapeKind!.Trim();
            case HmiControlType.Rectangle:
                return control.Radius > 0 ? "round_rect" : "rect";
            case HmiControlType.Ellipse:
                return Math.Abs(control.Width - control.Height) < 1 ? "circle" : "ellipse";
            case HmiControlType.Line:
                return "line";
            case HmiControlType.Polyline:
                return "polyline";
            case HmiControlType.Arc:
                return "arc";
            case HmiControlType.Tank:
                return "tank";
            case HmiControlType.Valve:
                return (control.ValveType ?? "").Trim().ToLowerInvariant() switch
                {
                    "gate" => "valve_gate",
                    "butterfly" => "valve_butterfly",
                    _ => "valve_ball",
                };
            default:
                return "rect";
        }
    }

    private void ResetExclusiveRows()
    {
        foreach (var name in new[]
                 {
                     "RowArcStart", "RowArcEnd", "RowGaugeLabelPos", "RowChartDecimals",
                     "RowHistRefresh", "RowCurveRefresh", "RowRecipeRowHeight", "RowAlarmFilter", "RowAlarmColumns",
                     "RowAlarmScroll", "RowPipeWallColor", "RowBoxColor", "RowKeyboardTarget",
                     "RowXyXMin", "RowXyXMax", "RowXyXDecimals", "RowFigureFillOpa", "RowFigurePoints",
                 })
        {
            if (this.FindControl<Control>(name) is { } row) row.IsVisible = false;
        }
        KnobShowValueCheck.IsVisible = false;
        RecipeScrollbarCheck.IsVisible = false;
        FigureParamHost.IsVisible = false;
        FigureParamHost.Children.Clear();
        _figureEditors.Clear();
        _figureKindBuilt = null;
        ExclusiveParamExpander.IsVisible = false;
    }

    /// <summary>按控件类型显示专属行，并回填值。</summary>
    private void ShowExclusiveRows(HmiControl control)
    {
        ResetExclusiveRows();
        var type = control.Type;

        var isFigure = IsFigureWidget(type);
        var kind = FigureKindOf(control);
        var isArcLike = type == HmiControlType.Arc ||
                        (type == HmiControlType.Shape && kind is "arc" or "pie");

        SetExclusiveRow("RowArcStart", isArcLike);
        SetExclusiveRow("RowArcEnd", isArcLike);
        SetExclusiveRow("RowGaugeLabelPos", type == HmiControlType.Gauge);
        KnobShowValueCheck.IsVisible = type == HmiControlType.Knob;
        SetExclusiveRow("RowChartDecimals", type == HmiControlType.Chart);
        SetExclusiveRow("RowHistRefresh", type == HmiControlType.HistoryTable);
        SetExclusiveRow("RowCurveRefresh", type == HmiControlType.HistoryCurve);
        SetExclusiveRow("RowRecipeRowHeight", type == HmiControlType.RecipeTable);
        RecipeScrollbarCheck.IsVisible = type == HmiControlType.RecipeTable;
        SetExclusiveRow("RowAlarmFilter", type == HmiControlType.AlarmList);
        SetExclusiveRow("RowAlarmColumns", type == HmiControlType.AlarmList);
        SetExclusiveRow("RowAlarmScroll", type == HmiControlType.AlarmBar);
        SetExclusiveRow("RowPipeWallColor", type == HmiControlType.Pipe);
        SetExclusiveRow("RowBoxColor", type == HmiControlType.CheckBox);
        SetExclusiveRow("RowKeyboardTarget", type == HmiControlType.Keyboard);
        SetExclusiveRow("RowXyXMin", type == HmiControlType.XyCurve);
        SetExclusiveRow("RowXyXMax", type == HmiControlType.XyCurve);
        SetExclusiveRow("RowXyXDecimals", type == HmiControlType.XyCurve);
        SetExclusiveRow("RowFigureFillOpa", isFigure);
        SetExclusiveRow("RowFigurePoints", isFigure || type == HmiControlType.Pipe);

        // 值回填
        ArcStartBox.Value = control.ArcAngleStart ?? 0;
        ArcEndBox.Value = control.ArcAngleEnd ?? 360;
        SelectComboByTag(GaugeLabelPosCombo, control.GaugeLabelPos, 0);
        KnobShowValueCheck.IsChecked = control.KnobShowValue;
        ChartDecimalsBox.Value = control.ValueDecimalPlaces is >= 0 and <= 6 ? control.ValueDecimalPlaces : 0;
        HistRefreshBox.Value = control.HistTableRefreshMs;
        CurveRefreshBox.Value = control.RefreshMs;
        RecipeRowHeightBox.Value = control.RecipeRowHeight;
        RecipeScrollbarCheck.IsChecked = control.RecipeScrollbar;
        AlarmLevelFilterBox.Text = control.AlarmLevelFilter ?? "";
        AlarmColumnsBox.Text = control.AlarmColumns ?? "";
        SelectComboByTag(AlarmScrollCombo, control.AlarmScroll, 0);
        PipeWallColorBox.Text = control.PipeWallColor ?? "";
        BoxColorBox.Text = control.BoxColor ?? "";
        KeyboardTargetBox.Text = control.KeyboardTarget ?? "";
        XyXMinBox.Value = control.XMinValue;
        XyXMaxBox.Value = control.XMaxValue;
        XyXDecimalsBox.Value = control.CurveXDecimals;
        FigureFillOpaBox.Value = control.FigureFillOpa ?? 255;
        FigurePointsBox.Text = FormatPoints(control.Points);

        // 图元 kind 级参数：只对图元控件、且只对该 kind 登记过的属性建行
        var hasKindProps = false;
        if (isFigure)
        {
            var props = FigurePropertyCatalog.For(kind);
            if (props.Count > 0)
            {
                BuildFigureRows(kind, props, control);
                hasKindProps = true;
            }
        }
        FigureParamHost.IsVisible = hasKindProps;

        ExclusiveParamExpander.IsVisible =
            isArcLike || isFigure || type is HmiControlType.Gauge or HmiControlType.Knob
                or HmiControlType.Chart or HmiControlType.HistoryTable or HmiControlType.HistoryCurve
                or HmiControlType.RecipeTable
                or HmiControlType.AlarmList or HmiControlType.AlarmBar or HmiControlType.Pipe
                or HmiControlType.CheckBox or HmiControlType.Keyboard or HmiControlType.XyCurve;
    }

    private void SetExclusiveRow(string rowName, bool visible)
    {
        if (this.FindControl<Control>(rowName) is { } row) row.IsVisible = visible;
    }

    private static string FormatPoints(List<PointData>? points)
    {
        if (points == null || points.Count == 0) return "";
        return string.Join("\n", points.Select(p =>
            string.Format(CultureInfo.InvariantCulture, "{0},{1}",
                p.X.ToString("0.###", CultureInfo.InvariantCulture),
                p.Y.ToString("0.###", CultureInfo.InvariantCulture))));
    }

    private static List<PointData>? ParsePoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<PointData>();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split(new[] { ',', '，', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            list.Add(new PointData(x, y));
        }
        return list.Count >= 2 ? list.Take(64).ToList() : null;
    }

    /// <summary>按一组登记项生成整行（标签 + 编辑器），并接上写回。</summary>
    private void BuildFigureRows(string kind, IReadOnlyList<FigureProperty> props, HmiControl control)
    {
        FigureParamHost.Children.Clear();
        _figureEditors.Clear();
        _figureKindBuilt = kind;

        foreach (var prop in props)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*"), Margin = new Thickness(0, 1) };
            row.Children.Add(new TextBlock
            {
                Text = AppLocalization.Service[$"prop.figure.{prop.Key}"],
                Foreground = (IBrush?)this.FindResource("HmiTextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            });

            Control editor;
            switch (prop.Type)
            {
                case FigurePropType.Bool:
                {
                    var check = new CheckBox
                    {
                        IsChecked = FigurePropertyCatalog.ToBool(prop.Effective(control)),
                        FontSize = 12,
                    };
                    check.Click += (_, _) =>
                    {
                        if (_isUpdating || _currentControl == null) return;
                        prop.Set(_currentControl, check.IsChecked == true);
                        PropertyChanged?.Invoke(this, _currentControl);
                    };
                    editor = check;
                    break;
                }
                case FigurePropType.Enum:
                {
                    var combo = new ComboBox { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Stretch };
                    var index = 0;
                    var current = (prop.Effective(control) as string) ?? "";
                    foreach (var option in prop.Options)
                    {
                        combo.Items.Add(new ComboBoxItem { Content = option, Tag = option });
                        if (string.Equals(option, current, StringComparison.OrdinalIgnoreCase)) index = combo.ItemCount - 1;
                    }
                    combo.SelectedIndex = index;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (_isUpdating || _currentControl == null) return;
                        if (combo.SelectedItem is ComboBoxItem { Tag: string tag })
                        {
                            prop.Set(_currentControl, tag);
                            PropertyChanged?.Invoke(this, _currentControl);
                        }
                    };
                    editor = combo;
                    break;
                }
                case FigurePropType.Int:
                {
                    var box = new FANumberBox
                    {
                        Minimum = prop.Min,
                        Maximum = prop.Max,
                        Value = FigurePropertyCatalog.ToInt(prop.Effective(control)),
                        Margin = new Thickness(0, 2),
                    };
                    box.ValueChanged += (_, e) =>
                    {
                        if (_isUpdating || _currentControl == null) return;
                        prop.Set(_currentControl, (int)Math.Round(e.NewValue));
                        PropertyChanged?.Invoke(this, _currentControl);
                    };
                    editor = box;
                    break;
                }
                default:
                {
                    var box = new FANumberBox
                    {
                        Minimum = prop.Min,
                        Maximum = prop.Max,
                        Value = FigurePropertyCatalog.ToDouble(prop.Effective(control)),
                        SmallChange = 0.05,
                        Margin = new Thickness(0, 2),
                    };
                    box.ValueChanged += (_, e) =>
                    {
                        if (_isUpdating || _currentControl == null) return;
                        prop.Set(_currentControl, Math.Round(e.NewValue, 3));
                        PropertyChanged?.Invoke(this, _currentControl);
                    };
                    editor = box;
                    break;
                }
            }

            editor.Name = $"Fig_{prop.Key}";
            Grid.SetColumn(editor, 1);
            row.Children.Add(editor);
            FigureParamHost.Children.Add(row);
            _figureEditors[prop.Key] = editor;
        }
    }

    /// <summary>把「专属参数」区里可见的行写回模型（不可见的行绝不写，防止跨类型污染）。</summary>
    private void SaveExclusiveRows()
    {
        if (_currentControl == null) return;
        var c = _currentControl;

        if (RowArcStart.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ArcAngleStart), (int)ArcStartBox.Value, v => c.ArcAngleStart = v);
        if (RowArcEnd.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ArcAngleEnd), (int)ArcEndBox.Value, v => c.ArcAngleEnd = v);
        if (RowGaugeLabelPos.IsVisible && GaugeLabelPosCombo.SelectedItem is ComboBoxItem { Tag: string pos })
            ValidateAndWrite(nameof(HmiControl.GaugeLabelPos), pos, v => c.GaugeLabelPos = v);
        if (KnobShowValueCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.KnobShowValue), KnobShowValueCheck.IsChecked ?? true, v => c.KnobShowValue = v);
        if (RowChartDecimals.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ValueDecimalPlaces), (int)ChartDecimalsBox.Value, v => c.ValueDecimalPlaces = v);
        if (RowHistRefresh.IsVisible)
            ValidateAndWrite(nameof(HmiControl.HistTableRefreshMs), (int)HistRefreshBox.Value, v => c.HistTableRefreshMs = v);
        if (RowCurveRefresh.IsVisible)
            ValidateAndWrite(nameof(HmiControl.RefreshMs), (int)CurveRefreshBox.Value, v => c.RefreshMs = v);
        if (RowRecipeRowHeight.IsVisible)
            ValidateAndWrite(nameof(HmiControl.RecipeRowHeight), (int)RecipeRowHeightBox.Value, v => c.RecipeRowHeight = v);
        if (RecipeScrollbarCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.RecipeScrollbar), RecipeScrollbarCheck.IsChecked ?? true, v => c.RecipeScrollbar = v);
        if (RowAlarmFilter.IsVisible)
            ValidateAndWrite(nameof(HmiControl.AlarmLevelFilter),
                string.IsNullOrWhiteSpace(AlarmLevelFilterBox.Text) ? null : AlarmLevelFilterBox.Text.Trim(),
                v => c.AlarmLevelFilter = v);
        if (RowAlarmColumns.IsVisible)
            ValidateAndWrite(nameof(HmiControl.AlarmColumns),
                string.IsNullOrWhiteSpace(AlarmColumnsBox.Text) ? null : AlarmColumnsBox.Text.Trim(),
                v => c.AlarmColumns = v);
        if (RowAlarmScroll.IsVisible && AlarmScrollCombo.SelectedItem is ComboBoxItem { Tag: string scroll })
            ValidateAndWrite(nameof(HmiControl.AlarmScroll), scroll, v => c.AlarmScroll = v);
        if (RowPipeWallColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.PipeWallColor), PropertyValidator.SanitizeColor(PipeWallColorBox.Text),
                v => c.PipeWallColor = v);
        if (RowBoxColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.BoxColor), PropertyValidator.SanitizeColor(BoxColorBox.Text),
                v => c.BoxColor = v);
        if (RowKeyboardTarget.IsVisible)
            ValidateAndWrite(nameof(HmiControl.KeyboardTarget),
                string.IsNullOrWhiteSpace(KeyboardTargetBox.Text) ? null : KeyboardTargetBox.Text.Trim(),
                v => c.KeyboardTarget = v);
        if (RowXyXMin.IsVisible)
            ValidateAndWrite(nameof(HmiControl.XMinValue), XyXMinBox.Value, v => c.XMinValue = v);
        if (RowXyXMax.IsVisible)
            ValidateAndWrite(nameof(HmiControl.XMaxValue), XyXMaxBox.Value, v => c.XMaxValue = v);
        if (RowXyXDecimals.IsVisible)
            ValidateAndWrite(nameof(HmiControl.CurveXDecimals), (int)XyXDecimalsBox.Value, v => c.CurveXDecimals = v);
        if (RowFigureFillOpa.IsVisible)
            ValidateAndWrite(nameof(HmiControl.FigureFillOpa), (int)FigureFillOpaBox.Value, v => c.FigureFillOpa = v);
        if (RowFigurePoints.IsVisible)
            ValidateAndWrite(nameof(HmiControl.Points), ParsePoints(FigurePointsBox.Text), v => c.Points = v);
        // 图元 kind 级行的写回在编辑事件里即时完成（BuildFigureRows），这里不重写：
        // 重写会把"用户没碰过的可空字段"变成有值，等于伪造一条用户设置。
    }
}
