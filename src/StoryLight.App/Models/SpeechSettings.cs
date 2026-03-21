namespace StoryLight.App.Models;

public sealed class SpeechSettings
{
    public string? VoiceId { get; set; }
    public int Rate { get; set; }
    public bool ContinueToNextPage { get; set; }
}
