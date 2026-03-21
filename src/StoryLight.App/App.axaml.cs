using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StoryLight.App.Services;
using StoryLight.App.ViewModels;
using StoryLight.App.Views;

namespace StoryLight.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var importer = new DocumentImportService();
            var stateStore = new JsonReaderStateStore();
            var textToSpeech = TextToSpeechServiceFactory.Create();
            var splashWindow = new LoadingWindow();

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = splashWindow;
            _ = ShowMainWindowAsync(desktop, importer, stateStore, textToSpeech, splashWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowMainWindowAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        IDocumentImporter importer,
        IReaderStateStore stateStore,
        ITextToSpeechService textToSpeech,
        LoadingWindow splashWindow)
    {
        await Task.Delay(1400);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(importer, stateStore, textToSpeech)
            };

            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            splashWindow.Close();
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        });
    }
}
