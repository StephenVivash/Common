namespace WattCycle.Core;

internal sealed class JbdFrameReader
{
    private readonly List<byte> _buffer = [];

    public IReadOnlyList<byte[]> Add(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            _buffer.Add(value);
        }

        var frames = new List<byte[]>();
        while (TryTakeFrame(out var frame))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private bool TryTakeFrame(out byte[] frame)
    {
        frame = [];
        var start = _buffer.IndexOf(0xdd);
        if (start < 0)
        {
            _buffer.Clear();
            return false;
        }

        if (start > 0)
        {
            _buffer.RemoveRange(0, start);
        }

        if (_buffer.Count < 4)
        {
            return false;
        }

        var length = _buffer[3];
        var frameLength = length + 7;
        if (_buffer.Count < frameLength)
        {
            return false;
        }

        frame = _buffer.Take(frameLength).ToArray();
        _buffer.RemoveRange(0, frameLength);
        return true;
    }
}
