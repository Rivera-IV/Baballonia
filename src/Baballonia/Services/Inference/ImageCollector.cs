using OpenCvSharp;
using System;

namespace Baballonia.Services.Inference;

/// <summary>
/// Maintains the four-frame eye history expected by the temporal model. Channels are stored
/// separately in a fixed ring so a frame is split only once and never merged merely to be split
/// again on the following inference pass.
/// </summary>
public sealed class ImageCollector : IImageTransformer, IDisposable
{
    private const int HistoryLength = 4;
    private readonly Mat?[,] _history = new Mat?[HistoryLength, 2];
    private readonly Mat[] _mergeChannels = new Mat[HistoryLength * 2];
    private int _nextSlot;
    private int _count;
    private long _receivedFrames;

    public Mat? Apply(Mat image)
    {
        var channels = image.Split();
        if (channels.Length < 2)
        {
            foreach (var channel in channels)
                channel.Dispose();
            return null;
        }

        try
        {
            Cv2.EqualizeHist(channels[0], channels[0]);
            Cv2.EqualizeHist(channels[1], channels[1]);
        }
        catch
        {
            foreach (var channel in channels)
                channel.Dispose();
            throw;
        }

        for (var i = 2; i < channels.Length; i++)
            channels[i].Dispose();

        _history[_nextSlot, 0]?.Dispose();
        _history[_nextSlot, 1]?.Dispose();

        // Preserve the established camera/model channel mapping: right first, then left.
        _history[_nextSlot, 0] = channels[1];
        _history[_nextSlot, 1] = channels[0];
        _nextSlot = (_nextSlot + 1) % HistoryLength;
        _count = Math.Min(HistoryLength, _count + 1);
        _receivedFrames++;

        // Historical behavior begins inference on the fifth received frame and then uses frames
        // 5,4,3,2. Retaining that warm-up keeps model input exactly compatible.
        if (_receivedFrames < HistoryLength + 1 || _count < HistoryLength)
            return null;

        for (var age = 0; age < HistoryLength; age++)
        {
            var slot = (_nextSlot - 1 - age + HistoryLength) % HistoryLength;
            _mergeChannels[age * 2] = _history[slot, 0]!;
            _mergeChannels[age * 2 + 1] = _history[slot, 1]!;
        }

        var temporal = new Mat();
        try
        {
            Cv2.Merge(_mergeChannels, temporal);
            return temporal;
        }
        catch
        {
            temporal.Dispose();
            throw;
        }
    }

    public void Reset()
    {
        for (var slot = 0; slot < HistoryLength; slot++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                _history[slot, channel]?.Dispose();
                _history[slot, channel] = null;
            }
        }

        _nextSlot = 0;
        _count = 0;
        _receivedFrames = 0;
    }

    public void Dispose() => Reset();
}
