namespace StoryLight.App.Models;

public sealed record DocumentSection(string Title, string Text, string Anchor, bool PreserveAsPage = false);
