// Bit reader modelled on FFmpeg's GetBitContext (big-endian bit order,
// zero padding past the end of the buffer).
namespace WmaDecoder;

internal sealed class BitReader
{
    private byte[] _data = Array.Empty<byte>();
    private int _sizeBits;

    public int Position { get; private set; }
    public int BitsLeft => _sizeBits - Position;

    public void Init(byte[] data, int offset, int lengthBits)
    {
        // lengthBits is a bit count over data starting at byte offset.
        int byteLen = (lengthBits + 7) / 8;
        _data = new byte[byteLen];
        if (byteLen > 0 && offset < data.Length)
            Array.Copy(data, offset, _data, 0, Math.Min(byteLen, data.Length - offset));
        _sizeBits = lengthBits;
        Position = 0;
    }

    public void Init(byte[] data) => Init(data, 0, data.Length * 8);

    private int PeekBit(int index)
    {
        int byteIndex = index >> 3;
        if (byteIndex < 0 || byteIndex >= _data.Length) return 0;
        return (_data[byteIndex] >> (7 - (index & 7))) & 1;
    }

    public int PeekBits(int count)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
            result = (result << 1) | PeekBit(Position + i);
        return result;
    }

    public int GetBits(int count)
    {
        if (count <= 0) return 0;
        int result = PeekBits(count);
        Position += count;
        return result;
    }

    public long GetBitsLong(int count)
    {
        if (count <= 0) return 0;
        long result = 0;
        for (int i = 0; i < count; i++)
            result = (result << 1) | (uint)PeekBit(Position + i);
        Position += count;
        return result;
    }

    public int GetBit() => GetBits(1);

    public void SkipBits(int count) => Position += count;

    public void AlignToByte()
    {
        int rem = Position & 7;
        if (rem != 0) Position += 8 - rem;
    }
}
