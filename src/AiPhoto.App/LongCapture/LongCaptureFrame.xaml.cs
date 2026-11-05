using System.Windows;
using AiPhoto.App.Infrastructure;
using AiPhoto.Core.Imaging;

namespace AiPhoto.App.LongCapture;

public partial class LongCaptureFrame : Window
{
    private readonly PixelRect _rect;

    /// <param name="captureRect">Capture region in physical pixels; the frame is drawn just outside it.</param>
    public LongCaptureFrame(PixelRect captureRect)
    {
        _rect = captureRect.Inflate(3);
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            PhysicalPlacement.MakeClickThrough(this);
            PhysicalPlacement.ExcludeFromCapture(this);
            PhysicalPlacement.Place(this, _rect);
        };
        Loaded += (_, _) => PhysicalPlacement.Place(this, _rect);
    }
}
