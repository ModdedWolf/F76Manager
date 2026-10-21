// Pure managed port of FFmpeg's WMA v1/v2 decoder
// (libavcodec/wma.c + libavcodec/wmadec.c, LGPL-2.1-or-later).
// See LICENSE-LGPL.txt. Only the decoding path is ported; WMA Pro
// (0x0162) and WMA Lossless (0x0163) are reported as unsupported.
using System.Buffers.Binary;

namespace WmaDecoder;

public sealed class WmaFormatInfo
{
    public ushort Tag { get; set; }
    public int Channels { get; set; }
    public int SampleRate { get; set; }
    public int ByteRate { get; set; }
    public int BlockAlign { get; set; }
    public int BitsPerSample { get; set; }
    public byte[] ExtraData { get; set; } = Array.Empty<byte>();
    public int BitRate => ByteRate * 8;
}

public sealed class WmaV2Decoder
{
    private const int BlockMinBits = 7;
    private const int BlockMaxBits = 11;
    private const int BlockMaxSize = 1 << BlockMaxBits;
    private const int BlockNbSizes = BlockMaxBits - BlockMinBits + 1;
    private const int HighBandMaxSize = 16;
    private const int NbLspCoefs = 10;
    private const int MaxChannels = 2;
    private const int NoiseTabSize = 8192;
    private const int LspPowBits = 7;
    private const int MaxCodedSuperframeSize = 32768;
    private const int VlcBits = 9;

    private readonly int _version; // 1 = 0x160, 2 = 0x161
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _bitRate;
    private readonly int _blockAlign;
    private readonly bool _useBitReservoir;
    private readonly bool _useVariableBlockLen;
    private readonly bool _useExpVlc;
    private bool _useNoiseCoding;
    private readonly int _byteOffsetBits;

    private readonly int _frameLen;
    private readonly int _frameLenBits;
    private readonly int _nbBlockSizes;
    private readonly int[] _exponentSizes = new int[BlockNbSizes];
    private readonly int[][] _exponentBands = Jagged<int>(BlockNbSizes, 25);
    private readonly int[] _highBandStart = new int[BlockNbSizes];
    private readonly int _coefsStart;
    private readonly int[] _coefsEnd = new int[BlockNbSizes];
    private readonly int[] _exponentHighSizes = new int[BlockNbSizes];
    private readonly int[][] _exponentHighBands = Jagged<int>(BlockNbSizes, HighBandMaxSize);
    private readonly float[][] _windows;

    private readonly Vlc? _expVlc;
    private readonly Vlc? _hgainVlc;
    private readonly Vlc[] _coefVlc = new Vlc[2];
    private readonly ushort[][] _runTable = new ushort[2][];
    private readonly float[][] _levelTable = new float[2][];

    private readonly Mdct[] _mdcts;
    private readonly float[] _lspCosTable = new float[BlockMaxSize];
    private readonly float[] _lspPowETable = new float[256];
    private readonly float[] _lspPowMTable1 = new float[1 << LspPowBits];
    private readonly float[] _lspPowMTable2 = new float[1 << LspPowBits];
    private readonly float[] _powTab = new float[156];

    private readonly BitReader _gb = new();
    private bool _resetBlockLengths = true;
    private int _blockLenBits;
    private int _nextBlockLenBits;
    private int _prevBlockLenBits;
    private int _blockLen;
    private int _blockNum;
    private int _blockPos;
    private bool _msStereo;
    private readonly bool[] _channelCoded = new bool[MaxChannels];
    private readonly int[] _exponentsBsize = new int[MaxChannels];
    private readonly float[][] _exponents = JaggedFloat(MaxChannels, BlockMaxSize);
    private readonly float[] _maxExponent = { 1.0f, 1.0f };
    private readonly float[][] _coefs1 = JaggedFloat(MaxChannels, BlockMaxSize);
    private readonly float[][] _coefs = JaggedFloat(MaxChannels, BlockMaxSize);
    private readonly float[] _output = new float[BlockMaxSize * 2];
    private readonly float[][] _frameOut = JaggedFloat(MaxChannels, BlockMaxSize * 2);
    private readonly int[][] _highBandCoded = Jagged<int>(MaxChannels, HighBandMaxSize);
    private readonly int[][] _highBandValues = Jagged<int>(MaxChannels, HighBandMaxSize);
    private readonly bool[] _exponentsInitialized = new bool[MaxChannels];
    private readonly float[] _noiseTable = new float[NoiseTabSize];
    private int _noiseIndex;
    private readonly float _noiseMult;

    private readonly byte[] _lastSuperframe = new byte[MaxCodedSuperframeSize + 64];
    private int _lastBitoffset;
    private int _lastSuperframeLen;
    private bool _eofDone;

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public int FrameLength => _frameLen;
    public int Version => _version;

    private static T[][] Jagged<T>(int rows, int cols) where T : new()
    {
        var a = new T[rows][];
        for (int i = 0; i < rows; i++) a[i] = new T[cols];
        return a;
    }

    private static float[][] JaggedFloat(int rows, int cols)
    {
        var a = new float[rows][];
        for (int i = 0; i < rows; i++) a[i] = new float[cols];
        return a;
    }

    internal static int GetFrameLenBits(int sampleRate, int version)
    {
        if (sampleRate <= 16000) return 9;
        if (sampleRate <= 22050 || (sampleRate <= 32000 && version == 1)) return 10;
        if (sampleRate <= 48000 || version < 3) return 11;
        if (sampleRate <= 96000) return 12;
        return 13;
    }

    private static int Log2Int(int value)
    {
        if (value <= 0) return 0;
        return 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);
    }

    private static float Exp10(float x) => (float)Math.Pow(10, x);

    public WmaV2Decoder(WmaFormatInfo format)
    {
        if (format.Tag == 0x0160) _version = 1;
        else if (format.Tag == 0x0161) _version = 2;
        else throw new InvalidDataException($"audio_codec_unsupported: format tag 0x{format.Tag:X4} is not WMA v1/v2");

        _channels = format.Channels;
        _sampleRate = format.SampleRate;
        _bitRate = format.BitRate;
        _blockAlign = format.BlockAlign;

        if (_blockAlign <= 0) throw new InvalidDataException("block_align is not set");
        if (_sampleRate > 50000 || _channels > 2 || _channels <= 0 || _bitRate <= 0)
            throw new InvalidDataException("unsupported WMA stream parameters");

        byte[] extradata = format.ExtraData ?? Array.Empty<byte>();
        int flags2 = 0;
        if (_version == 1 && extradata.Length >= 4) flags2 = BinaryPrimitives.ReadUInt16LittleEndian(extradata.AsSpan(2));
        else if (_version == 2 && extradata.Length >= 6) flags2 = BinaryPrimitives.ReadUInt16LittleEndian(extradata.AsSpan(4));

        _useExpVlc = (flags2 & 0x0001) != 0;
        _useBitReservoir = (flags2 & 0x0002) != 0;
        _useVariableBlockLen = (flags2 & 0x0004) != 0;
        if (_version == 2 && extradata.Length >= 8 &&
            BinaryPrimitives.ReadUInt16LittleEndian(extradata.AsSpan(4)) == 0xd && _useVariableBlockLen)
        {
            _useVariableBlockLen = false; // FFmpeg issue1503 workaround
        }

        _frameLenBits = GetFrameLenBits(_sampleRate, _version);
        _nextBlockLenBits = _frameLenBits;
        _prevBlockLenBits = _frameLenBits;
        _blockLenBits = _frameLenBits;
        _frameLen = 1 << _frameLenBits;

        if (_useVariableBlockLen)
        {
            int nb = ((flags2 >> 3) & 3) + 1;
            if (_bitRate / _channels >= 32000) nb += 2;
            int nbMax = _frameLenBits - BlockMinBits;
            if (nb > nbMax) nb = nbMax;
            _nbBlockSizes = nb + 1;
        }
        else _nbBlockSizes = 1;

        _useNoiseCoding = true;
        float highFreq = _sampleRate * 0.5f;
        int sampleRate1 = _sampleRate;
        if (_version == 2)
        {
            if (sampleRate1 >= 44100) sampleRate1 = 44100;
            else if (sampleRate1 >= 22050) sampleRate1 = 22050;
            else if (sampleRate1 >= 16000) sampleRate1 = 16000;
            else if (sampleRate1 >= 11025) sampleRate1 = 11025;
            else if (sampleRate1 >= 8000) sampleRate1 = 8000;
        }

        float bps = (float)_bitRate / (_channels * _sampleRate);
        _byteOffsetBits = Log2Int((int)(bps * _frameLen / 8.0f + 0.5f)) + 2;

        float bps1 = bps;
        if (_channels == 2) bps1 = bps * 1.6f;
        if (sampleRate1 == 44100)
        {
            if (bps1 >= 0.61f) _useNoiseCoding = false;
            else highFreq = highFreq * 0.4f;
        }
        else if (sampleRate1 == 22050)
        {
            if (bps1 >= 1.16f) _useNoiseCoding = false;
            else if (bps1 >= 0.72f) highFreq = highFreq * 0.7f;
            else highFreq = highFreq * 0.6f;
        }
        else if (sampleRate1 == 16000)
        {
            highFreq = bps > 0.5f ? highFreq * 0.5f : highFreq * 0.3f;
        }
        else if (sampleRate1 == 11025) highFreq = highFreq * 0.7f;
        else if (sampleRate1 == 8000)
        {
            if (bps <= 0.625f) highFreq = highFreq * 0.5f;
            else if (bps > 0.75f) _useNoiseCoding = false;
            else highFreq = highFreq * 0.65f;
        }
        else
        {
            if (bps >= 0.8f) highFreq = highFreq * 0.75f;
            else if (bps >= 0.6f) highFreq = highFreq * 0.6f;
            else highFreq = highFreq * 0.5f;
        }

        _coefsStart = _version == 1 ? 3 : 0;
        for (int k = 0; k < _nbBlockSizes; k++)
        {
            int blockLen = _frameLen >> k;
            if (_version == 1)
            {
                int lpos = 0, i;
                for (i = 0; i < 25; i++)
                {
                    int a = WmaTables.CriticalFreqs[i];
                    int b = _sampleRate;
                    int pos = ((blockLen * 2 * a) + (b >> 1)) / b;
                    if (pos > blockLen) pos = blockLen;
                    _exponentBands[0][i] = pos - lpos;
                    if (pos >= blockLen) { i++; break; }
                    lpos = pos;
                }
                _exponentSizes[0] = i;
            }
            else
            {
                byte[]? table = null;
                int a = _frameLenBits - BlockMinBits - k;
                if (a < 3)
                {
                    if (_sampleRate >= 44100) table = WmaTables.ExponentBand44100[a];
                    else if (_sampleRate >= 32000) table = WmaTables.ExponentBand32000[a];
                    else if (_sampleRate >= 22050) table = WmaTables.ExponentBand22050[a];
                }
                if (table != null)
                {
                    int n = table[0];
                    for (int i = 0; i < n; i++) _exponentBands[k][i] = table[i + 1];
                    _exponentSizes[k] = n;
                }
                else
                {
                    int j = 0, lpos = 0;
                    for (int i = 0; i < 25; i++)
                    {
                        int aa = WmaTables.CriticalFreqs[i];
                        int b = _sampleRate;
                        int pos = ((blockLen * 2 * aa) + (b << 1)) / (4 * b);
                        pos <<= 2;
                        if (pos > blockLen) pos = blockLen;
                        if (pos > lpos) _exponentBands[k][j++] = pos - lpos;
                        if (pos >= blockLen) break;
                        lpos = pos;
                    }
                    _exponentSizes[k] = j;
                }
            }

            _coefsEnd[k] = (_frameLen - ((_frameLen * 9) / 100)) >> k;
            _highBandStart[k] = (int)((blockLen * 2 * highFreq) / _sampleRate + 0.5f);
            {
                int n = _exponentSizes[k], j = 0, pos = 0;
                for (int i = 0; i < n; i++)
                {
                    int start = pos;
                    pos += _exponentBands[k][i];
                    int end = pos;
                    if (start < _highBandStart[k]) start = _highBandStart[k];
                    if (end > _coefsEnd[k]) end = _coefsEnd[k];
                    if (end > start) _exponentHighBands[k][j++] = end - start;
                }
                _exponentHighSizes[k] = j;
            }
        }

        _windows = new float[_nbBlockSizes][];
        for (int i = 0; i < _nbBlockSizes; i++)
        {
            int size = 1 << (_frameLenBits - i);
            var window = new float[size];
            for (int j = 0; j < size; j++)
                window[j] = (float)Math.Sin((j + 0.5) * (Math.PI / (2.0 * size)));
            _windows[i] = window;
        }

        _mdcts = new Mdct[_nbBlockSizes];
        for (int i = 0; i < _nbBlockSizes; i++)
            _mdcts[i] = new Mdct(1 << (_frameLenBits - i));

        if (_useNoiseCoding)
        {
            _noiseMult = _useExpVlc ? 0.02f : 0.04f;
            uint seed = 1;
            float norm = (float)((1.0 / (float)(1L << 31)) * Math.Sqrt(3) * _noiseMult);
            for (int i = 0; i < NoiseTabSize; i++)
            {
                seed = seed * 314159 + 1;
                _noiseTable[i] = (float)(int)seed * norm;
            }
            _hgainVlc = Vlc.FromLengths(WmaTables.HgainLengths, WmaTables.HgainSymbols, -18, 9);
        }
        else _noiseMult = 0;

        if (_useExpVlc)
            _expVlc = Vlc.FromCodes(WmaTables.AacScalefactorCode, WmaTables.AacScalefactorBits, 8);
        else
            LspToCurveInit(_frameLen);

        for (int i = 0; i < _powTab.Length; i++)
            _powTab[i] = (float)Math.Pow(10, (i - 60) / 16.0);

        int coefVlcTable = 2;
        if (_sampleRate >= 32000)
        {
            if (bps1 < 0.72f) coefVlcTable = 0;
            else if (bps1 < 1.16f) coefVlcTable = 1;
        }
        var defs = CoefDefs();
        InitCoefVlc(0, defs[coefVlcTable * 2]);
        InitCoefVlc(1, defs[coefVlcTable * 2 + 1]);
    }

    private sealed class CoefDef
    {
        public required uint[] Codes;
        public required byte[] Bits;
        public required ushort[] Levels;
    }

    private static CoefDef[] CoefDefs() => new[]
    {
        new CoefDef { Codes = WmaTables.Coef0HuffCodes, Bits = WmaTables.Coef0HuffBits, Levels = WmaTables.Levels0 },
        new CoefDef { Codes = WmaTables.Coef1HuffCodes, Bits = WmaTables.Coef1HuffBits, Levels = WmaTables.Levels1 },
        new CoefDef { Codes = WmaTables.Coef2HuffCodes, Bits = WmaTables.Coef2HuffBits, Levels = WmaTables.Levels2 },
        new CoefDef { Codes = WmaTables.Coef3HuffCodes, Bits = WmaTables.Coef3HuffBits, Levels = WmaTables.Levels3 },
        new CoefDef { Codes = WmaTables.Coef4HuffCodes, Bits = WmaTables.Coef4HuffBits, Levels = WmaTables.Levels4 },
        new CoefDef { Codes = WmaTables.Coef5HuffCodes, Bits = WmaTables.Coef5HuffBits, Levels = WmaTables.Levels5 },
    };

    private void InitCoefVlc(int index, CoefDef def)
    {
        int n = def.Bits.Length;
        _coefVlc[index] = Vlc.FromCodes(def.Codes, def.Bits, VlcBits);
        var runTable = new ushort[n];
        var levelTable = new float[n];
        int i = 2, level = 1, k = 0;
        while (i < n)
        {
            int l = def.Levels[k++];
            for (int j = 0; j < l; j++)
            {
                runTable[i] = (ushort)j;
                levelTable[i] = level;
                i++;
            }
            level++;
        }
        _runTable[index] = runTable;
        _levelTable[index] = levelTable;
    }

    // ---- LSP helpers -------------------------------------------------

    private void LspToCurveInit(int frameLen)
    {
        double wdel = Math.PI / frameLen;
        for (int i = 0; i < frameLen; i++)
            _lspCosTable[i] = 2.0f * (float)Math.Cos(wdel * i);
        for (int i = 0; i < 256; i++)
        {
            int e = i - 126;
            _lspPowETable[i] = (float)Math.Pow(2, e * -0.25);
        }
        double b = 1.0;
        for (int i = (1 << LspPowBits) - 1; i >= 0; i--)
        {
            int m = (1 << LspPowBits) + i;
            double a = m * (0.5 / (1 << LspPowBits));
            a = 1 / Math.Sqrt(Math.Sqrt(a));
            _lspPowMTable1[i] = (float)(2 * a - b);
            _lspPowMTable2[i] = (float)(b - a);
            b = a;
        }
    }

    private float PowM1_4(float x)
    {
        uint v = BitConverter.SingleToUInt32Bits(x);
        uint e = v >> 23;
        uint m = (v >> (23 - LspPowBits)) & ((1 << LspPowBits) - 1);
        uint tv = ((v << LspPowBits) & ((1 << 23) - 1)) | (127u << 23);
        float t = BitConverter.UInt32BitsToSingle(tv);
        float a = _lspPowMTable1[m];
        float b = _lspPowMTable2[m];
        return _lspPowETable[e] * (a + b * t);
    }

    private void LspToCurve(float[] outp, out float valMax, int n, float[] lsp)
    {
        float max = 0;
        for (int i = 0; i < n; i++)
        {
            float p = 0.5f, q = 0.5f;
            float w = _lspCosTable[i];
            for (int j = 1; j < NbLspCoefs; j += 2)
            {
                q *= w - lsp[j - 1];
                p *= w - lsp[j];
            }
            p *= p * (2.0f - w);
            q *= q * (2.0f + w);
            float v = PowM1_4(p + q);
            if (v > max) max = v;
            outp[i] = v;
        }
        valMax = max;
    }

    private void DecodeExpLsp(int ch)
    {
        var lspCoefs = new float[NbLspCoefs];
        for (int i = 0; i < NbLspCoefs; i++)
        {
            int val = (i == 0 || i >= 8) ? _gb.GetBits(3) : _gb.GetBits(4);
            lspCoefs[i] = WmaTables.LspCodebook[i][val];
        }
        LspToCurve(_exponents[ch], out float max, _blockLen, lspCoefs);
        _maxExponent[ch] = max;
    }

    private bool DecodeExpVlc(int ch)
    {
        int bsizeIndex = _frameLenBits - _blockLenBits;
        var bands = _exponentBands[bsizeIndex];
        float[] exps = _exponents[ch];
        int q = 0, bandIndex = 0, lastExp;
        float maxScale = 0;
        if (_version == 1)
        {
            lastExp = _gb.GetBits(5) + 10;
            float v = _powTab[lastExp + 60];
            maxScale = v;
            int n = bands[bandIndex++];
            for (int i = 0; i < n; i++) exps[q++] = v;
        }
        else lastExp = 36;

        while (q < _blockLen)
        {
            int code = _expVlc!.Decode(_gb);
            if (code < 0) return false;
            lastExp += code - 60;
            if ((uint)(lastExp + 60) >= (uint)_powTab.Length) return false;
            float v = _powTab[lastExp + 60];
            if (v > maxScale) maxScale = v;
            int n = bands[bandIndex++];
            for (int i = 0; i < n && q < exps.Length; i++) exps[q++] = v;
        }
        _maxExponent[ch] = maxScale;
        return true;
    }

    // ---- coefficient RLE ----------------------------------------------

    private uint GetLargeVal()
    {
        int nBits = 8;
        if (_gb.GetBit() != 0)
        {
            nBits += 8;
            if (_gb.GetBit() != 0)
            {
                nBits += 8;
                if (_gb.GetBit() != 0) nBits += 7;
            }
        }
        return (uint)_gb.GetBitsLong(nBits);
    }

    private bool RunLevelDecode(Vlc vlc, float[] levelTable, ushort[] runTable,
        float[] ptr, int numCoefs, int blockLen, int coefNbBits)
    {
        int offset = 0;
        int coefMask = blockLen - 1;
        for (; offset < numCoefs; offset++)
        {
            int code = vlc.Decode(_gb);
            if (code < 0) return false;
            if (code > 1)
            {
                offset += runTable[code];
                int signBit = _gb.GetBit();
                float lvl = levelTable[code];
                if ((offset & coefMask) < ptr.Length)
                    ptr[offset & coefMask] = signBit == 1 ? lvl : -lvl;
            }
            else if (code == 1)
            {
                break; // EOB
            }
            else
            {
                int level = _gb.GetBits(coefNbBits);
                offset += _gb.GetBits(_frameLenBits);
                int signBit = _gb.GetBit();
                if ((offset & coefMask) < ptr.Length)
                    ptr[offset & coefMask] = signBit == 1 ? level : -level;
            }
        }
        return offset <= numCoefs;
    }

    private static int TotalGainToBits(int totalGain)
    {
        if (totalGain < 15) return 13;
        if (totalGain < 32) return 12;
        if (totalGain < 40) return 11;
        if (totalGain < 45) return 10;
        return 9;
    }

    // ---- windowing ------------------------------------------------------

    private void WmaWindow(float[] outArr, int outOffset)
    {
        float[] inp = _output;
        int inOffset = 0;
        int outPos = outOffset;
        int blockLen;

        if (_blockLenBits <= _prevBlockLenBits)
        {
            blockLen = _blockLen;
            int bsize = _frameLenBits - _blockLenBits;
            var win = _windows[bsize];
            for (int i = 0; i < blockLen; i++)
                outArr[outPos + i] = inp[inOffset + i] * win[i] + outArr[outPos + i];
        }
        else
        {
            blockLen = 1 << _prevBlockLenBits;
            int n = (_blockLen - blockLen) / 2;
            int bsize = _frameLenBits - _prevBlockLenBits;
            var win = _windows[bsize];
            for (int i = 0; i < blockLen; i++)
                outArr[outPos + n + i] = inp[inOffset + n + i] * win[i] + outArr[outPos + n + i];
            Array.Copy(inp, inOffset + n + blockLen, outArr, outPos + n + blockLen, n);
        }

        outPos += _blockLen;
        inOffset += _blockLen;

        if (_blockLenBits <= _nextBlockLenBits)
        {
            blockLen = _blockLen;
            int bsize = _frameLenBits - _blockLenBits;
            var win = _windows[bsize];
            for (int i = 0; i < blockLen; i++)
                outArr[outPos + i] = inp[inOffset + i] * win[blockLen - 1 - i];
        }
        else
        {
            blockLen = 1 << _nextBlockLenBits;
            int n = (_blockLen - blockLen) / 2;
            int bsize = _frameLenBits - _nextBlockLenBits;
            var win = _windows[bsize];
            Array.Copy(inp, inOffset, outArr, outPos, n);
            for (int i = 0; i < blockLen; i++)
                outArr[outPos + n + i] = inp[inOffset + n + i] * win[blockLen - 1 - i];
            Array.Clear(outArr, outPos + n + blockLen, n);
        }
    }

    // ---- block / frame / superframe -------------------------------------

    /// <returns>0 = more blocks, 1 = last block of frame.</returns>
    private int DecodeBlock()
    {
        int channels = _channels;

        if (_useVariableBlockLen)
        {
            int n = Log2Int(_nbBlockSizes - 1) + 1;
            if (_resetBlockLengths)
            {
                _resetBlockLengths = false;
                int v = _gb.GetBits(n);
                if (v >= _nbBlockSizes) throw new InvalidDataException("prev_block_len_bits out of range");
                _prevBlockLenBits = _frameLenBits - v;
                v = _gb.GetBits(n);
                if (v >= _nbBlockSizes) throw new InvalidDataException("block_len_bits out of range");
                _blockLenBits = _frameLenBits - v;
            }
            else
            {
                _prevBlockLenBits = _blockLenBits;
                _blockLenBits = _nextBlockLenBits;
            }
            int vv = _gb.GetBits(n);
            if (vv >= _nbBlockSizes) throw new InvalidDataException("next_block_len_bits out of range");
            _nextBlockLenBits = _frameLenBits - vv;
        }
        else
        {
            _nextBlockLenBits = _frameLenBits;
            _prevBlockLenBits = _frameLenBits;
            _blockLenBits = _frameLenBits;
        }

        if (_frameLenBits - _blockLenBits >= _nbBlockSizes)
            throw new InvalidDataException("block_len_bits not initialized to a valid value");

        _blockLen = 1 << _blockLenBits;
        if (_blockPos + _blockLen > _frameLen)
            throw new InvalidDataException("frame_len overflow");

        if (channels == 2) _msStereo = _gb.GetBit() != 0;
        int vCoded = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int a = _gb.GetBit();
            _channelCoded[ch] = a != 0;
            vCoded |= a;
        }

        int bsize = _frameLenBits - _blockLenBits;
        if (vCoded == 0) goto Next;

        int totalGain = 1;
        for (;;)
        {
            if (_gb.BitsLeft < 7) throw new InvalidDataException("total_gain overread");
            int a = _gb.GetBits(7);
            totalGain += a;
            if (a != 127) break;
        }
        int coefNbBits = TotalGainToBits(totalGain);

        int nCoefsBase = _coefsEnd[bsize] - _coefsStart;
        var nbCoefs = new int[MaxChannels];
        for (int ch = 0; ch < channels; ch++) nbCoefs[ch] = nCoefsBase;

        if (_useNoiseCoding)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (!_channelCoded[ch]) continue;
                int n = _exponentHighSizes[bsize];
                for (int i = 0; i < n; i++)
                {
                    int a = _gb.GetBit();
                    _highBandCoded[ch][i] = a;
                    if (a != 0) nbCoefs[ch] -= _exponentHighBands[bsize][i];
                }
            }
            for (int ch = 0; ch < channels; ch++)
            {
                if (!_channelCoded[ch]) continue;
                int n = _exponentHighSizes[bsize];
                int val = int.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (_highBandCoded[ch][i] == 0) continue;
                    if (val == int.MinValue) val = _gb.GetBits(7) - 19;
                    else val += _hgainVlc!.Decode(_gb);
                    _highBandValues[ch][i] = val;
                }
            }
        }

        if (_blockLenBits == _frameLenBits || _gb.GetBit() != 0)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (!_channelCoded[ch]) continue;
                if (_useExpVlc)
                {
                    if (!DecodeExpVlc(ch)) throw new InvalidDataException("exponent out of range");
                }
                else DecodeExpLsp(ch);
                _exponentsBsize[ch] = bsize;
                _exponentsInitialized[ch] = true;
            }
        }

        for (int ch = 0; ch < channels; ch++)
            if (_channelCoded[ch] && !_exponentsInitialized[ch])
                throw new InvalidDataException("exponents not initialized");

        for (int ch = 0; ch < channels; ch++)
        {
            if (_channelCoded[ch])
            {
                int tindex = (ch == 1 && _msStereo) ? 1 : 0;
                Array.Clear(_coefs1[ch], 0, _blockLen);
                if (!RunLevelDecode(_coefVlc[tindex], _levelTable[tindex], _runTable[tindex],
                        _coefs1[ch], nbCoefs[ch], _blockLen, coefNbBits))
                    throw new InvalidDataException("overflow in spectral RLE");
            }
            if (_version == 1 && channels >= 2) _gb.AlignToByte();
        }

        float mdctNorm = 1.0f / (_blockLen / 2);
        if (_version == 1) mdctNorm *= (float)Math.Sqrt(_blockLen / 2);

        for (int ch = 0; ch < channels; ch++)
        {
            if (!_channelCoded[ch]) continue;
            float[] coefs1 = _coefs1[ch];
            float[] exponents = _exponents[ch];
            int esize = _exponentsBsize[ch];
            float mult = Exp10(totalGain * 0.05f) / _maxExponent[ch];
            mult *= mdctNorm;
            float[] coefs = _coefs[ch];
            int ci = 0;

            if (_useNoiseCoding)
            {
                float mult1 = mult;
                for (int i = 0; i < _coefsStart; i++)
                {
                    coefs[ci++] = _noiseTable[_noiseIndex] * exponents[(i << bsize) >> esize] * mult1;
                    _noiseIndex = (_noiseIndex + 1) & (NoiseTabSize - 1);
                }

                int n1 = _exponentHighSizes[bsize];
                var expPower = new float[HighBandMaxSize];
                int expIdx = (_highBandStart[bsize] << bsize) >> esize;
                int lastHighBand = 0;
                for (int j = 0; j < n1; j++)
                {
                    int n = _exponentHighBands[bsize][j];
                    if (_highBandCoded[ch][j] != 0)
                    {
                        float e2 = 0;
                        for (int i = 0; i < n; i++)
                        {
                            float ev = exponents[expIdx + ((i << bsize) >> esize)];
                            e2 += ev * ev;
                        }
                        expPower[j] = e2 / n;
                        lastHighBand = j;
                    }
                    expIdx += (n << bsize) >> esize;
                }

                expIdx = (_coefsStart << bsize) >> esize;
                int c1i = 0;
                for (int j = -1; j < n1; j++)
                {
                    int n = j < 0 ? _highBandStart[bsize] - _coefsStart : _exponentHighBands[bsize][j];
                    if (j >= 0 && _highBandCoded[ch][j] != 0)
                    {
                        mult1 = (float)Math.Sqrt(expPower[j] / expPower[lastHighBand]);
                        mult1 = mult1 * Exp10(_highBandValues[ch][j] * 0.05f);
                        mult1 = mult1 / (_maxExponent[ch] * _noiseMult);
                        mult1 *= mdctNorm;
                        for (int i = 0; i < n; i++)
                        {
                            float noise = _noiseTable[_noiseIndex];
                            _noiseIndex = (_noiseIndex + 1) & (NoiseTabSize - 1);
                            coefs[ci++] = noise * exponents[expIdx + ((i << bsize) >> esize)] * mult1;
                        }
                        expIdx += (n << bsize) >> esize;
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                        {
                            float noise = _noiseTable[_noiseIndex];
                            _noiseIndex = (_noiseIndex + 1) & (NoiseTabSize - 1);
                            coefs[ci++] = (coefs1[c1i++] + noise) * exponents[expIdx + ((i << bsize) >> esize)] * mult;
                        }
                        expIdx += (n << bsize) >> esize;
                    }
                }

                int nHigh = _blockLen - _coefsEnd[bsize];
                mult1 = mult * exponents[expIdx + ((-(1 << bsize)) >> esize)];
                for (int i = 0; i < nHigh; i++)
                {
                    coefs[ci++] = _noiseTable[_noiseIndex] * mult1;
                    _noiseIndex = (_noiseIndex + 1) & (NoiseTabSize - 1);
                }
            }
            else
            {
                for (int i = 0; i < _coefsStart; i++) coefs[ci++] = 0.0f;
                int n = nbCoefs[ch];
                for (int i = 0; i < n; i++)
                    coefs[ci++] = coefs1[i] * exponents[(i << bsize) >> esize] * mult;
                int n2 = _blockLen - _coefsEnd[bsize];
                for (int i = 0; i < n2; i++) coefs[ci++] = 0.0f;
            }
        }

        if (_msStereo && channels >= 2 && _channelCoded[1])
        {
            if (!_channelCoded[0])
            {
                Array.Clear(_coefs[0], 0, _blockLen);
                _channelCoded[0] = true;
            }
            for (int i = 0; i < _blockLen; i++)
            {
                float t = _coefs[0][i] - _coefs[1][i];
                _coefs[0][i] += _coefs[1][i];
                _coefs[1][i] = t;
            }
        }

    Next:
        var mdct = _mdcts[bsize];
        for (int ch = 0; ch < channels; ch++)
        {
            int n4 = _blockLen / 2;
            if (_channelCoded[ch]) mdct.Inverse(_coefs[ch], _output);
            else if (!(_msStereo && ch == 1)) Array.Clear(_output, 0, _output.Length);

            int index = (_frameLen / 2) + _blockPos - n4;
            WmaWindow(_frameOut[ch], index);
        }

        _blockNum++;
        _blockPos += _blockLen;
        return _blockPos >= _frameLen ? 1 : 0;
    }

    private float[][] DecodeFrame()
    {
        _blockNum = 0;
        _blockPos = 0;
        for (;;)
        {
            int ret = DecodeBlock();
            if (ret != 0) break;
        }
        var samples = new float[_channels][];
        for (int ch = 0; ch < _channels; ch++)
        {
            samples[ch] = new float[_frameLen];
            Array.Copy(_frameOut[ch], 0, samples[ch], 0, _frameLen);
            Array.Copy(_frameOut[ch], _frameLen, _frameOut[ch], 0, _frameLen);
        }
        return samples;
    }

    /// <summary>Decode one packet (block_align bytes). Returns zero or more frames;
    /// each frame is [channel][frameLength] planar float samples.</summary>
    public List<float[][]> DecodePacket(byte[] packet)
    {
        var frames = new List<float[][]>();
        if (packet == null || packet.Length == 0) return frames;
        if (packet.Length < _blockAlign)
            throw new InvalidDataException($"Input packet size too small ({packet.Length} < {_blockAlign})");
        int bufSize = _blockAlign;
        byte[] buf = packet;

        try
        {
            _gb.Init(buf, 0, bufSize * 8);
            int nbFrames;
            if (_useBitReservoir)
            {
                _gb.SkipBits(4);
                nbFrames = _gb.GetBits(4) - (_lastSuperframeLen <= 0 ? 1 : 0);
                if (nbFrames <= 0)
                {
                    if (nbFrames < 0 || _gb.BitsLeft <= 8)
                        throw new InvalidDataException($"nb_frames is {nbFrames}");
                    if (_lastSuperframeLen + bufSize - 1 > MaxCodedSuperframeSize)
                        throw new InvalidDataException("last superframe too large");
                    int qi = _lastSuperframeLen;
                    int len = bufSize - 1;
                    while (len > 0) { _lastSuperframe[qi++] = (byte)_gb.GetBits(8); len--; }
                    Array.Clear(_lastSuperframe, qi, 64);
                    _lastSuperframeLen += 8 * bufSize - 8;
                    return frames;
                }
            }
            else nbFrames = 1;

            if (_useBitReservoir)
            {
                int bitOffset = _gb.GetBits(_byteOffsetBits + 3);
                if (bitOffset > _gb.BitsLeft)
                    throw new InvalidDataException("Invalid last frame bit offset");

                if (_lastSuperframeLen > 0)
                {
                    if (_lastSuperframeLen + ((bitOffset + 7) >> 3) > MaxCodedSuperframeSize)
                        throw new InvalidDataException("last superframe too large");
                    int qi = _lastSuperframeLen;
                    int len = bitOffset;
                    while (len > 7) { _lastSuperframe[qi++] = (byte)_gb.GetBits(8); len -= 8; }
                    if (len > 0) _lastSuperframe[qi++] = (byte)(_gb.GetBits(len) << (8 - len));
                    Array.Clear(_lastSuperframe, qi, Math.Min(64, _lastSuperframe.Length - qi));

                    _gb.Init(_lastSuperframe, 0, _lastSuperframeLen * 8 + bitOffset);
                    if (_lastBitoffset > 0) _gb.SkipBits(_lastBitoffset);
                    frames.Add(DecodeFrame());
                    nbFrames--;
                }

                int pos = bitOffset + 4 + 4 + _byteOffsetBits + 3;
                if (pos >= MaxCodedSuperframeSize * 8 || pos > bufSize * 8)
                    throw new InvalidDataException("bit offset out of range");
                _gb.Init(buf, pos >> 3, (bufSize - (pos >> 3)) * 8);
                int len2 = pos & 7;
                if (len2 > 0) _gb.SkipBits(len2);

                _resetBlockLengths = true;
                for (int i = 0; i < nbFrames; i++)
                    frames.Add(DecodeFrame());

                pos = _gb.Position + ((bitOffset + 4 + 4 + _byteOffsetBits + 3) & ~7);
                _lastBitoffset = pos & 7;
                pos >>= 3;
                int len3 = bufSize - pos;
                if (len3 > MaxCodedSuperframeSize || len3 < 0)
                    throw new InvalidDataException("invalid trailing length");
                _lastSuperframeLen = len3;
                Array.Copy(buf, pos, _lastSuperframe, 0, len3);
            }
            else
            {
                frames.Add(DecodeFrame());
            }
        }
        catch
        {
            _lastSuperframeLen = 0;
            throw;
        }
        return frames;
    }

    /// <summary>Flush the final (delayed) frame, mirroring FFmpeg's EOF path.</summary>
    public float[][]? Flush()
    {
        if (_eofDone) return null;
        _eofDone = true;
        var samples = new float[_channels][];
        for (int ch = 0; ch < _channels; ch++)
        {
            samples[ch] = new float[_frameLen];
            Array.Copy(_frameOut[ch], 0, samples[ch], 0, _frameLen);
        }
        return samples;
    }
}
