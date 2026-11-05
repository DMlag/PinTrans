using System.Windows;

namespace AiPhoto.App.LongCapture;

public partial class LongCapturePanel : Window
{
    public LongCapturePanel()
    {
        InitializeComponent();
    }

    public event EventHandler<bool>? AutoScrollChanged;
    public event EventHandler? FinishRequested;
    public event EventHandler? CancelRequested;

    public void SetProgress(int height, int frames)
        => HeightText.Text = $"{height:N0} px · {frames} 帧";

    public void SetWarning(string? text)
    {
        WarningText.Text = text ?? "";
        WarningText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetAutoScroll(bool on) => AutoButton.IsChecked = on;

    private void OnAutoClick(object sender, RoutedEventArgs e) => AutoScrollChanged?.Invoke(this, AutoButton.IsChecked == true);
    private void OnFinishClick(object sender, RoutedEventArgs e) => FinishRequested?.Invoke(this, EventArgs.Empty);
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
