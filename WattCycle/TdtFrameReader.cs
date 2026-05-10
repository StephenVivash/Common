namespace WattCycle.Core;

internal sealed class TdtFrameReader
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
        var start = _buffer.IndexOf(0x7e);
        if (start < 0)
        {
            _buffer.Clear();
            return false;
        }

        if (start > 0)
        {
            _buffer.RemoveRange(0, start);
        }

        var expectedLength = TdtProtocol.ExpectedFrameLength(_buffer.ToArray());
        if (expectedLength == 0 || _buffer.Count < expectedLength)
        {
            return false;
        }

        frame = _buffer.Take(expectedLength).ToArray();
        _buffer.RemoveRange(0, expectedLength);
        return true;
    }
}
