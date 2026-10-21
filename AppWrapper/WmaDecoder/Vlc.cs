// Canonical Huffman (VLC) decoder modelled on FFmpeg's vlc.c/get_bits.h.
namespace WmaDecoder;

internal sealed class Vlc
{
    private readonly int _lutBits;
    private readonly int[] _lutSymbol;
    private readonly byte[] _lutLength;
    private readonly Dictionary<long, int> _longCodes = new();
    private readonly Dictionary<long, int> _allCodes = new();
    public int MaxBits { get; }

    private Vlc(int lutBits, int maxBits)
    {
        _lutBits = Math.Max(1, lutBits);
        _lutSymbol = new int[1 << _lutBits];
        _lutLength = new byte[1 << _lutBits];
        MaxBits = maxBits;
    }

    private void AddCode(uint code, int length, int symbol)
    {
        long key = ((long)length << 32) | code;
        _allCodes[key] = symbol;
        if (length <= _lutBits)
        {
            int shift = _lutBits - length;
            int start = (int)(code << shift);
            int count = 1 << shift;
            for (int i = 0; i < count; i++)
            {
                _lutSymbol[start + i] = symbol;
                _lutLength[start + i] = (byte)length;
            }
        }
        else
        {
            _longCodes[key] = symbol;
        }
    }

    /// <summary>Build from explicit codes/bits; symbol is the table index.</summary>
    public static Vlc FromCodes(uint[] codes, byte[] bits, int lutBits)
    {
        int max = 0;
        for (int i = 0; i < bits.Length; i++) if (bits[i] > max) max = bits[i];
        var vlc = new Vlc(lutBits, max);
        for (int i = 0; i < codes.Length; i++)
            if (bits[i] > 0) vlc.AddCode(codes[i], bits[i], i);
        return vlc;
    }

    /// <summary>
    /// Build from code lengths, assigning canonical codes in table order
    /// exactly like FFmpeg's ff_vlc_init_from_lengths.
    /// </summary>
    public static Vlc FromLengths(int[] lengths, int[] symbols, int symbolOffset, int lutBits)
    {
        int max = 0;
        for (int i = 0; i < lengths.Length; i++) if (lengths[i] > max) max = lengths[i];
        var vlc = new Vlc(lutBits, max);
        ulong code = 0; // top-aligned (32-bit) accumulator, as in FFmpeg
        for (int i = 0; i < lengths.Length; i++)
        {
            int len = lengths[i];
            if (len > 0)
            {
                uint c = (uint)(code >> (32 - len));
                vlc.AddCode(c, len, symbols[i] + symbolOffset);
                code += 1UL << (32 - len);
            }
        }
        return vlc;
    }

    public int Decode(BitReader reader)
    {
        int peek = reader.PeekBits(_lutBits);
        int len = _lutLength[peek];
        if (len > 0)
        {
            reader.SkipBits(len);
            return _lutSymbol[peek];
        }
        // Slow path: walk bit by bit (also covers codes longer than the LUT).
        int code = 0;
        for (int length = 1; length <= MaxBits; length++)
        {
            code = (code << 1) | reader.GetBit();
            long key = ((long)length << 32) | (uint)code;
            if (_allCodes.TryGetValue(key, out int symbol))
                return symbol;
        }
        return -1;
    }
}
