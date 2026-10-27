using System;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace HmiEditor.Core.Models;

/// <summary>
/// 屏幕的控件集合。除了 <see cref="ObservableCollection{T}"/> 的职责，还负责一件事：
/// **谁动了它，日志里能查**。
///
/// <para><b>为什么需要</b>：画布白屏（拖进来的控件不显示、导出成空页）时，症状只会表现为
/// "页面 JSON 里 widgets 是空数组"，看不出控件是被删了、还是被挂到了某个容器下、还是换了
/// 一个 Screen 实例。模型层没有编译期防线能拦住这种"静默摘除"，所以把增删都记一笔 ——
/// 打开 <c>HMI_TRACE_MODEL=1</c> 时打印调用点，一次复现就能定位。</para>
/// </summary>
public sealed class ControlCollection : ObservableCollection<HmiControl>
{
    private static readonly bool Tracing =
        Environment.GetEnvironmentVariable("HMI_TRACE_MODEL") == "1";

    protected override void InsertItem(int index, HmiControl item)
    {
        base.InsertItem(index, item);
        if (Tracing) Trace($"Add[{index}] {item?.Id} count={Count}");
    }

    protected override void RemoveItem(int index)
    {
        var removed = index >= 0 && index < Count ? this[index] : null;
        base.RemoveItem(index);
        if (Tracing) Trace($"Remove[{index}] {removed?.Id} count={Count}");
    }

    protected override void ClearItems()
    {
        var before = Count;
        base.ClearItems();
        if (Tracing) Trace($"Clear was={before}");
    }

    protected override void SetItem(int index, HmiControl item)
    {
        base.SetItem(index, item);
        if (Tracing) Trace($"Set[{index}] {item?.Id}");
    }

    /// <summary>打印动作 + 第一帧**业务**调用点（跳过 model / BCL 帧，只要"谁调用的"）。</summary>
    private static void Trace(string what)
    {
        var frames = new StackTrace(2, false).GetFrames();
        var where = "<unknown>";
        if (frames != null)
        {
            foreach (var f in frames)
            {
                var type = f.GetMethod()?.DeclaringType?.FullName ?? "";
                if (type.StartsWith("HmiEditor.Core.Models", StringComparison.Ordinal)) continue;
                if (type.StartsWith("System.", StringComparison.Ordinal)) continue;
                where = $"{type}.{f.GetMethod()?.Name} {f.GetFileName()}:{f.GetFileLineNumber()}";
                break;
            }
        }
        Console.WriteLine($"[model] Controls.{what} <- {where}");
    }
}
