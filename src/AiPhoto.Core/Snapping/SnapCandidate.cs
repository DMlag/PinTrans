using AiPhoto.Core.Imaging;

namespace AiPhoto.Core.Snapping;

/// <summary>Controls how the capture overlay snaps to regions while hovering.</summary>
public enum SnapMode
{
    /// <summary>Snap to UI Automation controls, child HWNDs, visual boxes/text blocks, windows, and monitors.</summary>
    Smart,

    /// <summary>Snap only to top-level windows and monitors.</summary>
    WindowOnly,

    /// <summary>Do not snap to windows or controls (only monitor bounds).</summary>
    Disabled,
}

/// <summary>Semantic kind of a snappable region.</summary>
public enum SnapKind
{
    TextBlock = 0,
    Control = 1,
    VisualBox = 2,
    Panel = 3,
    Window = 4,
    Monitor = 5,
}

/// <summary>Origin of a <see cref="SnapCandidate"/> (ordered from lowest to highest metadata richness).</summary>
public enum SnapSource
{
    Monitor = 0,
    ImageEdge = 1,
    ChildHwnd = 2,
    Window = 3,
    UiAutomation = 4,
}

/// <summary>
/// A rectangular region in canvas pixel coordinates that the selection cursor can snap to.
/// </summary>
public readonly record struct SnapCandidate(
    PixelRect Rect,
    SnapKind Kind,
    SnapSource Source,
    int Depth = 0,
    int WindowIndex = -1,
    string? Label = null)
{
    public long Area => (long)Rect.Width * Rect.Height;

    /// <summary>Short localized description of the candidate kind when no explicit <see cref="Label"/> is present.</summary>
    public string DisplayLabel => !string.IsNullOrWhiteSpace(Label)
        ? Label!
        : Kind switch
        {
            SnapKind.TextBlock => "文本块",
            SnapKind.Control => "控件",
            SnapKind.VisualBox => "区域",
            SnapKind.Panel => "面板",
            SnapKind.Window => "窗口",
            SnapKind.Monitor => "全屏",
            _ => "区域",
        };
}
