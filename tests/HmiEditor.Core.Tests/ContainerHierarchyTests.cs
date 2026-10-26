using HmiEditor.Core;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Core.Tests;

/// <summary>
/// 容器层级模型（<see cref="ContainerHierarchy"/>）的单元测试。
/// 坐标换算（绝对 ↔ 相对）是画布渲染/命中/拖拽与导出共用的地基，单独钉住。
/// </summary>
public class ContainerHierarchyTests
{
    private static HmiControl Container(string id, double x, double y, double w = 200, double h = 160)
        => new HmiControl
        {
            Id = id,
            Name = id,
            Type = HmiControlType.Shape,
            ShapeKind = "container",
            X = x,
            Y = y,
            Width = w,
            Height = h,
        };

    private static HmiControl Child(string id, double x, double y, HmiControlType type = HmiControlType.Label)
        => new HmiControl { Id = id, Name = id, Type = type, X = x, Y = y, Width = 80, Height = 30 };

    [Fact]
    public void IsContainer_OnlyShapeWithContainerKind()
    {
        Assert.True(ContainerHierarchy.IsContainer(Container("c", 0, 0)));
        Assert.False(ContainerHierarchy.IsContainer(Child("l", 0, 0)));
        Assert.False(ContainerHierarchy.IsContainer(new HmiControl
        {
            Type = HmiControlType.Shape, ShapeKind = "rect",
        }));
    }

    [Fact]
    public void AttachAt_ConvertsAbsoluteToRelative_AndSetsParentId()
    {
        var screen = new HmiScreen();
        var container = Container("cont", 100, 50);
        screen.Controls.Add(container);
        var child = Child("kid", 130, 80);   // 屏幕绝对坐标

        ContainerHierarchy.AttachAt(container, child, ContainerHierarchy.Index(screen));

        Assert.Equal(30, child.X);           // 130 - 100
        Assert.Equal(30, child.Y);           // 80 - 50
        Assert.Equal("cont", child.ParentId);
        Assert.Single(container.Children);
    }

    [Fact]
    public void DetachTo_RestoresAbsoluteCoordinates()
    {
        var screen = new HmiScreen();
        var container = Container("cont", 100, 50);
        screen.Controls.Add(container);
        var child = Child("kid", 130, 80);
        ContainerHierarchy.AttachAt(container, child, ContainerHierarchy.Index(screen));
        Assert.Equal(30, child.X);           // 先确认换算过

        ContainerHierarchy.DetachTo(container, child, ContainerHierarchy.Index(screen));

        Assert.Equal(130, child.X);
        Assert.Equal(80, child.Y);
        Assert.Null(child.ParentId);
        Assert.Empty(container.Children);
    }

    [Fact]
    public void AttachAt_IsIdempotent_ForUndoReplay()
    {
        var screen = new HmiScreen();
        var container = Container("cont", 100, 50);
        screen.Controls.Add(container);
        var child = Child("kid", 130, 80);
        ContainerHierarchy.AttachAt(container, child, ContainerHierarchy.Index(screen));

        ContainerHierarchy.AttachAt(container, child, ContainerHierarchy.Index(screen));   // 撤销命令重放

        Assert.Single(container.Children);
        Assert.Equal(30, child.X);   // 坐标没有被二次扣减
    }

    [Fact]
    public void AttachAt_UsesContainerAbsoluteOrigin_WhenNested()
    {
        // 嵌套容器：内层容器自己的 X 已被换算成相对值，挂子控件必须沿父链求它的绝对原点
        var screen = new HmiScreen();
        var outer = Container("outer", 100, 50);
        screen.Controls.Add(outer);
        var inner = Container("inner", 120, 60);
        ContainerHierarchy.AttachAt(outer, inner, ContainerHierarchy.Index(screen));
        Assert.Equal(20, inner.X);           // 已换算成相对 outer

        var child = Child("kid", 125, 65);   // 屏幕绝对坐标
        ContainerHierarchy.AttachAt(inner, child, ContainerHierarchy.Index(screen));

        Assert.Equal(5, child.X);            // 125 - (100 + 20)
        Assert.Equal(5, child.Y);            // 65 - (50 + 10)
    }

    [Fact]
    public void Attach_RefusesCycles()
    {
        var outer = Container("outer", 0, 0);
        var inner = Container("inner", 20, 20);
        ContainerHierarchy.Attach(outer, inner);

        ContainerHierarchy.Attach(inner, outer);   // 祖父挂进孙子 → 拒绝

        Assert.Single(outer.Children);
        Assert.Empty(inner.Children);
        Assert.Null(outer.ParentId);
    }

    [Fact]
    public void OriginOf_AccumulatesAncestorChain()
    {
        var screen = new HmiScreen();
        var outer = Container("outer", 100, 50);
        screen.Controls.Add(outer);
        var inner = Container("inner", 120, 60);   // 绝对坐标：挂进 outer 后相对 (20,10)
        var child = Child("kid", 125, 65);         // 绝对坐标：挂进 inner 后相对 (5,5)
        var index0 = ContainerHierarchy.Index(screen);
        ContainerHierarchy.AttachAt(outer, inner, index0);
        ContainerHierarchy.AttachAt(inner, child, ContainerHierarchy.Index(screen));

        var index = ContainerHierarchy.Index(screen);

        Assert.Equal((0d, 0d), ContainerHierarchy.OriginOf(outer, index));
        Assert.Equal((100d, 50d), ContainerHierarchy.OriginOf(inner, index));
        Assert.Equal((120d, 60d), ContainerHierarchy.OriginOf(child, index));

        var rect = ContainerHierarchy.AbsoluteRect(child, index);
        Assert.Equal(125d, rect.X);      // 120 + 5
        Assert.Equal(65d, rect.Y);
        Assert.Equal(80d, rect.Width);
        Assert.Equal(30d, rect.Height);
    }

    [Fact]
    public void Flatten_WalksParentsBeforeChildren()
    {
        var screen = new HmiScreen();
        var container = Container("cont", 0, 0);
        var a = Child("a", 0, 0);
        var b = Child("b", 0, 0);
        ContainerHierarchy.Attach(container, a);
        ContainerHierarchy.Attach(container, b);
        screen.Controls.Add(container);
        screen.Controls.Add(Child("root", 0, 0));

        Assert.Equal(new[] { "cont", "a", "b", "root" },
            ContainerHierarchy.Flatten(screen).Select(c => c.Id).ToArray());
    }

    [Fact]
    public void RepointDescendants_RebindsStaleParentIdsAfterClone()
    {
        var container = Container("cont", 0, 0);
        var child = Child("kid", 10, 10);
        ContainerHierarchy.Attach(container, child);

        container.Id = "cont-clone";     // 克隆只换顶层 Id

        ContainerHierarchy.RepointDescendants(container);

        Assert.Equal("cont-clone", child.ParentId);
    }

    [Fact]
    public void ContainsControl_CoversSelfAndDescendants()
    {
        var outer = Container("outer", 0, 0);
        var inner = Container("inner", 20, 20);
        ContainerHierarchy.Attach(outer, inner);

        Assert.True(ContainerHierarchy.ContainsControl(outer, inner));
        Assert.True(ContainerHierarchy.ContainsControl(outer, outer));
        Assert.False(ContainerHierarchy.ContainsControl(inner, outer));
    }
}
