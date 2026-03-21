namespace StoryLight.App.Services;

public static class TextToSpeechServiceFactory
{
    public static ITextToSpeechService Create()
    {
        return OperatingSystem.IsWindows()
            ? new HybridTextToSpeechService()
            : new NullTextToSpeechService();
    }
}
