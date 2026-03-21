using Avalonia.Controls;
using Avalonia.Threading;

namespace StoryLight.App.Views;

public sealed partial class LoadingWindow : Window
{
    private readonly DispatcherTimer _pulseTimer;
    private bool _dimmed;

    public LoadingWindow()
    {
        InitializeComponent();

        _pulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(170)
        };

        _pulseTimer.Tick += (_, _) =>
        {
            LoadingImage.Opacity = _dimmed ? 1.0 : 0.58;
            _dimmed = !_dimmed;
        };

        Opened += (_, _) => _pulseTimer.Start();
        Closed += (_, _) => _pulseTimer.Stop();
    }
}
