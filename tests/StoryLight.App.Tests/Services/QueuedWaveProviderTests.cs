using NAudio.Wave;
using StoryLight.App.Services;
using Xunit;

namespace StoryLight.App.Tests.Services;

public sealed class QueuedWaveProviderTests
{
    private static readonly WaveFormat TestFormat = WaveFormat.CreateCustomFormat(
        WaveFormatEncoding.Pcm,
        24000,
        1,
        24000,
        1,
        8);

    [Fact]
    public void Read_WhenQueueIsEmpty_ReturnsRequestedSilence()
    {
        var provider = new QueuedWaveProvider(TestFormat);
        var buffer = Enumerable.Repeat((byte)0x7F, 8).ToArray();

        var bytesRead = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, bytesRead);
        Assert.Equal(new byte[8], buffer);
        Assert.False(provider.HasPendingAudio);
    }

    [Fact]
    public void Read_WhenSegmentFinishes_RaisesSegmentCompletedOnce()
    {
        var provider = new QueuedWaveProvider(TestFormat);
        var completionCount = 0;
        provider.SegmentCompleted += () => completionCount++;
        provider.Enqueue(new byte[] { 1, 2, 3, 4 });
        var buffer = new byte[8];

        var bytesRead = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, bytesRead);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 }, buffer);
        Assert.Equal(1, completionCount);
        Assert.False(provider.HasPendingAudio);
    }

    [Fact]
    public void Clear_DropsQueuedAndPartialSegments()
    {
        var provider = new QueuedWaveProvider(TestFormat);
        provider.Enqueue(new byte[] { 9, 8, 7, 6 });
        var partial = new byte[2];
        provider.Read(partial, 0, partial.Length);

        provider.Clear();

        var buffer = Enumerable.Repeat((byte)0x5A, 4).ToArray();
        var bytesRead = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, bytesRead);
        Assert.Equal(new byte[4], buffer);
        Assert.False(provider.HasPendingAudio);
    }
}
