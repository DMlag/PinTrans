using AiPhoto.Core.Imaging;
using AiPhoto.Core.Snapping;

namespace AiPhoto.Core.Tests.Snapping;

public sealed class SnapIndexTests
{
    [Fact]
    public void Query_ReturnsHierarchyFromSmallestControlToWindowToMonitor()
    {
        var index = new SnapIndex(new PixelRect(0, 0, 1920, 1080));
        index.AddMonitor(new PixelRect(0, 0, 1920, 1080), "主显示器");

        var childPanel = new SnapCandidate(new PixelRect(120, 120, 400, 300), SnapKind.Panel, SnapSource.ChildHwnd, Depth: 1, Label: "面板");
        var winIdx = index.AddWindow(new PixelRect(100, 100, 800, 600), "记事本", [childPanel]);

        var uiaButton = new SnapCandidate(new PixelRect(150, 150, 80, 32), SnapKind.Control, SnapSource.UiAutomation, Depth: 3, Label: "按钮 · 确定");
        index.SetWindowUiaCandidates(winIdx, [uiaButton]);

        var stack = index.Query(160, 160);

        Assert.Equal(4, stack.Count);
        Assert.Equal("按钮 · 确定", stack[0].DisplayLabel);
        Assert.Equal(new PixelRect(150, 150, 80, 32), stack[0].Rect);

        Assert.Equal("面板", stack[1].DisplayLabel);
        Assert.Equal(new PixelRect(120, 120, 400, 300), stack[1].Rect);

        Assert.Equal("窗口 · 记事本", stack[2].DisplayLabel);
        Assert.Equal(new PixelRect(100, 100, 800, 600), stack[2].Rect);

        Assert.Equal("主显示器", stack[3].DisplayLabel);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), stack[3].Rect);
    }

    [Fact]
    public void Query_IgnoresChildControlsOfOccludedBackgroundWindow()
    {
        var index = new SnapIndex(new PixelRect(0, 0, 1920, 1080));
        index.AddMonitor(new PixelRect(0, 0, 1920, 1080));

        // Topmost window (Z = 0) covers (200, 200)
        var frontIdx = index.AddWindow(new PixelRect(150, 150, 300, 300), "前端弹窗");
        // Background window (Z = 1) also covers (200, 200) and has a button right at (200, 200)
        var backButton = new SnapCandidate(new PixelRect(180, 180, 60, 30), SnapKind.Control, SnapSource.ChildHwnd, Depth: 2, Label: "后台按钮");
        var backIdx = index.AddWindow(new PixelRect(50, 50, 1000, 800), "主窗口", [backButton]);

        var stackAtOverlap = index.Query(200, 200);

        // Background button is occluded by front window, so it must NOT appear in the stack at (200, 200).
        Assert.DoesNotContain(stackAtOverlap, c => c.Label == "后台按钮");
        Assert.Equal("窗口 · 前端弹窗", stackAtOverlap[0].DisplayLabel);
        // The larger main window behind the small dialog is still available when scrolling up past the dialog.
        Assert.Equal("窗口 · 主窗口", stackAtOverlap[1].DisplayLabel);

        // Outside the front window, at (600, 600), the background window is the topmost window at that point.
        var backButton2 = new SnapCandidate(new PixelRect(580, 580, 80, 40), SnapKind.Control, SnapSource.UiAutomation, Depth: 2, Label: "可见按钮");
        index.SetWindowUiaCandidates(backIdx, [backButton2]);
        var stackAtExposed = index.Query(600, 600);
        Assert.Equal("可见按钮", stackAtExposed[0].DisplayLabel);
        Assert.Equal(frontIdx, index.TopWindowIndexAt(200, 200));
        Assert.Equal(backIdx, index.TopWindowIndexAt(600, 600));
    }

    [Fact]
    public void Query_DeduplicatesNearlyIdenticalRects_PreferringRicherSource()
    {
        var index = new SnapIndex(new PixelRect(0, 0, 1920, 1080));
        var childHwnd = new SnapCandidate(new PixelRect(200, 200, 120, 36), SnapKind.Control, SnapSource.ChildHwnd, Depth: 1, Label: "Edit");
        var winIdx = index.AddWindow(new PixelRect(100, 100, 600, 400), "编辑器", [childHwnd]);

        // UIA reports the same control within 2px with a richer label, plus a wrapper pane of identical size.
        var uiaWrapper = new SnapCandidate(new PixelRect(201, 200, 119, 36), SnapKind.Panel, SnapSource.UiAutomation, Depth: 1, Label: "面板");
        var uiaInput = new SnapCandidate(new PixelRect(200, 200, 120, 36), SnapKind.Control, SnapSource.UiAutomation, Depth: 2, Label: "输入框 · 搜索");
        index.SetWindowUiaCandidates(winIdx, [uiaWrapper, uiaInput]);

        var stack = index.Query(220, 215);

        // Only 2 items: the deduplicated control and the window (not 3 duplicate control rects).
        Assert.Equal(2, stack.Count);
        Assert.Equal("输入框 · 搜索", stack[0].DisplayLabel);
        Assert.Equal(SnapSource.UiAutomation, stack[0].Source);
    }

    [Fact]
    public void Query_ClipsChildCandidatesToWindowAndCanvasBounds()
    {
        var index = new SnapIndex(new PixelRect(0, 0, 1000, 800));
        var winIdx = index.AddWindow(new PixelRect(100, 100, 400, 300), "浏览");

        // Offscreen/overflowing scrolled web element extending beyond the window bottom
        var overflowing = new SnapCandidate(new PixelRect(120, 250, 200, 400), SnapKind.Panel, SnapSource.UiAutomation, Depth: 2, Label: "长列表");
        index.SetWindowUiaCandidates(winIdx, [overflowing]);

        var stack = index.Query(150, 300);
        Assert.Equal(new PixelRect(120, 250, 200, 150), stack[0].Rect);
    }
}
