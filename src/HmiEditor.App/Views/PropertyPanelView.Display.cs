using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HmiEditor.App.Controls;
using HmiEditor.App.Design;
using HmiEditor.App.Resources;
using HmiEditor.App.Services;
using HmiEditor.App;
using HmiEditor.App.Views.Editors;
using HmiEditor.Core.Design;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace HmiEditor.App.Views;

public partial class PropertyPanelView : UserControl
{
    // ── 状态化样式（完整四态：Normal/Pressed/Checked/Disabled）──

    /// <summary>加载控件的状态样式到编辑器：填充图片与对齐下拉，恢复当前编辑态的字段值。</summary>
    private void LoadVisualStates(HmiControl control)
    {
        PopulateVisualStateImageItems();
        PopulateAlignCombos(isNormal: true);

        // 恢复状态选择到 Normal
        CboVisualState.SelectedIndex = 0;
        _currentEditingState = VisualState.Normal;
        LoadVisualStateFields(control, _currentEditingState);
    }

    // ── 值显示配置 ──

    /// <summary>加载控件的值显示配置：小数位下拉、前后缀，并按类型定各行的显隐。</summary>
    private void LoadValueDisplayFields(HmiControl control)
    {
        // 值类型下拉仍按模型回填（旧工程里存着这个字段），但这一行不再显示，见 ShowValueDisplayRows
        var displayTag = control.ValueDisplayType.ToString();
        foreach (var item in ValueDisplayTypeCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), displayTag, StringComparison.Ordinal))
            {
                ValueDisplayTypeCombo.SelectedItem = item;
                break;
            }
        }

        ValueScaleBox.Value = control.ValueScaleFactor;
        ValuePrefixBox.Text = control.ValuePrefix ?? "";
        ValueSuffixBox.Text = control.ValueSuffix ?? "";

        // 小数位数下拉（0-4 位，clamp 到选项范围）
        var decimals = Math.Clamp(control.ValueDecimalPlaces, 0, 4);
        foreach (var item in ValueDecimalsCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), decimals.ToString(), StringComparison.Ordinal))
            {
                ValueDecimalsCombo.SelectedItem = item;
                break;
            }
        }

        ShowValueDisplayRows(control.Type);
    }

    /// <summary>
    /// 值显示组各行的显隐，判据是**导出器 / 绑定格式化真的读不读这个字段**，不是"这类控件看起来
    /// 会不会显示数字"：
    /// <list type="bullet">
    /// <item><b>值类型</b>（<c>ValueDisplayType</c>）：导出器不写、画布不读 —— 只有
    /// <c>PreviewValueResolver</c> 的 <c>BindingSemantics</c> 用它，而那个 API 生产上没有调用方
    /// （只剩测试）⇒ 面板不再给入口，字段留在模型里兼容旧工程；</item>
    /// <item><b>缩放比例</b>（<c>ValueScaleFactor</c>）：同上，同样不给入口；</item>
    /// <item><b>小数位</b>：<c>value/gauge/knob</c> 写 <c>props.decimals</c>；<c>label</c> 绑变量时
    /// 喂 <c>bind[text].fmt</c>（输入框的小数位在「控件功能参数」里另有一行，不在这里重复）；</item>
    /// <item><b>前缀 / 后缀</b>：<c>value</c> 写 <c>props.prefix/suffix</c>；<c>label</c> 绑变量时
    /// 折成 <c>{tag:…}</c> 占位符；</item>
    /// <item><b>单位</b>：只有 <c>value</c> 有 <c>unit</c> 键。</item>
    /// </list>
    ///
    /// <para>滑块 / 指示灯 / 下拉框 / 罐形 / 管道 / 阀门这些类型四条键一个都没有，整组收起 ——
    /// 以前它们照样显示「小数位数 / 前缀 / 后缀」，填了既不进导出也不进画布。</para>
    /// </summary>
    private void ShowValueDisplayRows(HmiControlType type)
    {
        RowValueType.IsVisible = false;
        RowValueScale.IsVisible = false;

        var usesTextFormat = type is HmiControlType.Label or HmiControlType.Value;
        RowValueDecimals.IsVisible = usesTextFormat
            || type is HmiControlType.Gauge or HmiControlType.Knob;
        RowValuePrefix.IsVisible = usesTextFormat;
        RowValueSuffix.IsVisible = usesTextFormat;
        RowValueUnit.IsVisible = type == HmiControlType.Value;
    }

    /// <summary>解析小数位数下拉的选中项。</summary>
    private static int ParseValueDecimalPlaces(ComboBoxItem? item)
    {
        if (item?.Tag is string tag && int.TryParse(tag, out var n))
            return Math.Clamp(n, 0, 6);
        return 0;
    }

    /// <summary>解析值类型下拉的选中项为枚举。</summary>
    private static ValueDisplayType ParseValueDisplayType(ComboBoxItem? item)
    {
        var tag = item?.Tag?.ToString();
        return Enum.TryParse<ValueDisplayType>(tag, out var type) ? type : ValueDisplayType.Auto;
    }
}
