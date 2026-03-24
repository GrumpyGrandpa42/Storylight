using NAudio.Wave;

namespace StoryLight.App.Services;

internal sealed class QueuedWaveProvider : IWaveProvider
{
    private readonly object _sync = new();
    private readonly Queue<QueuedSegment> _segments = new();
    private QueuedSegment? _currentSegment;

    public QueuedWaveProvider(WaveFormat waveFormat)
    {
        WaveFormat = waveFormat;
    }

    public event Action? SegmentCompleted;

    public WaveFormat WaveFormat { get; }

    public bool HasPendingAudio
    {
        get
        {
            lock (_sync)
            {
                return _currentSegment is not null || _segments.Count > 0;
            }
        }
    }

    public void Enqueue(byte[] audioBytes)
    {
        if (audioBytes.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            _segments.Enqueue(new QueuedSegment(audioBytes));
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _segments.Clear();
            _currentSegment = null;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var completedSegments = 0;
        var bytesRead = 0;

        lock (_sync)
        {
            while (bytesRead < count)
            {
                if (_currentSegment is null)
                {
                    if (_segments.Count == 0)
                    {
                        Array.Clear(buffer, offset + bytesRead, count - bytesRead);
                        bytesRead = count;
                        break;
                    }

                    _currentSegment = _segments.Dequeue();
                }

                var remaining = _currentSegment.Bytes.Length - _currentSegment.Offset;
                var toCopy = Math.Min(remaining, count - bytesRead);
                Buffer.BlockCopy(_currentSegment.Bytes, _currentSegment.Offset, buffer, offset + bytesRead, toCopy);

                _currentSegment.Offset += toCopy;
                bytesRead += toCopy;

                if (_currentSegment.Offset >= _currentSegment.Bytes.Length)
                {
                    _currentSegment = null;
                    completedSegments++;
                }
            }
        }

        for (var i = 0; i < completedSegments; i++)
        {
            SegmentCompleted?.Invoke();
        }

        return bytesRead;
    }

    private sealed class QueuedSegment
    {
        public QueuedSegment(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public int Offset { get; set; }
    }
}
