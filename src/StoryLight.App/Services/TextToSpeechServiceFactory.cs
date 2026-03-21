namespace StoryLight.App.Services;

public static class TextToSpeechServiceFactory
{
    public static ITextToSpeechService Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSpeechService();
        }

        return new NullTextToSpeechService();
    }
}
