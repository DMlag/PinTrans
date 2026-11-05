using System.Windows.Media.Imaging;
using AiPhoto.Core.Imaging;
using AiPhoto.Core.Settings;
using AiPhoto.Core.Translation;

namespace AiPhoto.App.Services;

public enum CaptureIntent
{
    /// <summary>Select a region, annotate, copy/save/pin/translate.</summary>
    Normal,
    /// <summary>Translate immediately after the region is selected.</summary>
    Translate,
    /// <summary>Start a scrolling capture immediately after the region is selected.</summary>
    LongCapture,
}

/// <summary>Services that windows need from the application (implemented by <see cref="AppHost"/>).</summary>
public interface IAppHost
{
    AppSettings Settings { get; }

    IImageTranslator Translator { get; }

    /// <summary>Shows an image pinned on screen at <paramref name="screenRect"/> (physical pixels), optionally translating it.</summary>
    void ShowPin(Bgra32Image image, PixelRect screenRect, bool translate);

    /// <summary>Starts a scrolling capture of <paramref name="screenRect"/> (physical pixels).</summary>
    void StartLongCapture(PixelRect screenRect);

    /// <summary>Opens an image (e.g. a long screenshot) in the viewer window.</summary>
    void ShowViewer(Bgra32Image image, string title);

    void Notify(string title, string message, bool isError = false);

    /// <summary>Default file path (without extension) for a new screenshot.</summary>
    string SuggestFilePath(string? suffix = null);

    void OpenSettings();
}
