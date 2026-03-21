using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace StoryLight.App.Views;

public sealed partial class LoadingWindow : Window
{
    private readonly DispatcherTimer _pulseTimer;
    private readonly Image _loadingImage;
    private bool _dimmed;

    public LoadingWindow()
    {
        InitializeComponent();
        _loadingImage = this.FindControl<Image>("LoadingImage")
            ?? throw new InvalidOperationException("LoadingImage control was not found.");

        _pulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(170)
        };

        _pulseTimer.Tick += (_, _) =>
        {
            _loadingImage.Opacity = _dimmed ? 1.0 : 0.58;
            _dimmed = !_dimmed;
        };

        Opened += (_, _) => _pulseTimer.Start();
        Closed += (_, _) => _pulseTimer.Stop();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
