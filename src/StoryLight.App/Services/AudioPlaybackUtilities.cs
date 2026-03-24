using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace StoryLight.App.Services;

internal static class AudioPlaybackUtilities
{
    public static WaveFormat PlaybackFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

    public static byte[] ConvertToPlaybackFormat(byte[] audioBytes, WaveFormat inputFormat)
    {
        if (audioBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if (WaveFormatMatches(inputFormat, PlaybackFormat))
        {
            return audioBytes;
        }

        using var sourceBuffer = new MemoryStream(audioBytes, writable: false);
        using var rawStream = new RawSourceWaveStream(sourceBuffer, inputFormat);

        ISampleProvider sampleProvider = rawStream.ToSampleProvider();
        sampleProvider = ConvertChannels(sampleProvider, PlaybackFormat.Channels);

        if (sampleProvider.WaveFormat.SampleRate != PlaybackFormat.SampleRate)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, PlaybackFormat.SampleRate);
        }

        using var convertedBuffer = new MemoryStream();
        var sampleBuffer = new float[PlaybackFormat.SampleRate / 10 * PlaybackFormat.Channels];

        while (true)
        {
            var samplesRead = sampleProvider.Read(sampleBuffer, 0, sampleBuffer.Length);
            if (samplesRead == 0)
            {
                break;
            }

            var byteBuffer = new byte[samplesRead * sizeof(float)];
            Buffer.BlockCopy(sampleBuffer, 0, byteBuffer, 0, byteBuffer.Length);
            convertedBuffer.Write(byteBuffer, 0, byteBuffer.Length);
        }

        return convertedBuffer.ToArray();
    }

    private static ISampleProvider ConvertChannels(ISampleProvider sampleProvider, int targetChannels)
    {
        if (sampleProvider.WaveFormat.Channels == targetChannels)
        {
            return sampleProvider;
        }

        if (sampleProvider.WaveFormat.Channels == 1 && targetChannels == 2)
        {
            return new MonoToStereoSampleProvider(sampleProvider);
        }

        if (sampleProvider.WaveFormat.Channels == 2 && targetChannels == 1)
        {
            return new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }

        throw new NotSupportedException(
            $"Unsupported channel conversion: {sampleProvider.WaveFormat.Channels} -> {targetChannels}.");
    }

    private static bool WaveFormatMatches(WaveFormat left, WaveFormat right)
    {
        return left.Encoding == right.Encoding
            && left.SampleRate == right.SampleRate
            && left.Channels == right.Channels
            && left.BitsPerSample == right.BitsPerSample
            && left.BlockAlign == right.BlockAlign
            && left.AverageBytesPerSecond == right.AverageBytesPerSecond;
    }
}
