using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(importer, stateStore, textToSpeech)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
