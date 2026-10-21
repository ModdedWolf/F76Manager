// xWMA RIFF container parsing, modelled on FFmpeg's libavformat/xwma.c
// (LGPL-2.1-or-later), plus whole-file decoding to interleaved PCM16.
using System.Buffers.Binary;
using System.Text;

namespace WmaDecoder;

public sealed class XwmaInfo
{
    public ushort Tag { get; set; }
    public int Channels { get; set; }
    public int SampleRate { get; set; }
    public int ByteRate { get; set; }
    public int BlockAlign { get; set; }
    public int BitsPerSample { get; set; }
    public byte[] ExtraData { get; set; } = Array.Empty<byte>();
    public int DataOffset { get; set; }
    public int DataLength { get; set; }
    public uint[] PacketCumulativeSizes { get; set; } = Array.Empty<uint>();
}

public sealed class AudioDecodeResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string Codec { get; set; } = "";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public short[] Pcm { get; set; } = Array.Empty<short>();
    public int SamplesPerChannel => Channels > 0 ? Pcm.Length / Channels : 0;
    public double DurationSeconds => SampleRate > 0 ? (double)SamplesPerChannel / SampleRate : 0;
}

public static class XwmaFile
{
    private static string FourCc(byte[] data, int offset) =>
        offset + 4 <= data.Length ? Encoding.ASCII.GetString(data, offset, 4) : "";

    /// <summary>Parse an xWMA (RIFF/XWMA) or plain WMA RIFF file.</summary>
    public static XwmaInfo Parse(byte[] data)
    {
        if (data == null || data.Length < 20)
            throw new InvalidDataException("audio_decode_failed: file too small");
        if (FourCc(data, 0) != "RIFF")
            throw new InvalidDataException("audio_decode_failed: missing RIFF header");
        string form = FourCc(data, 8);
        if (form != "XWMA" && form != "WAVE")
            throw new InvalidDataException($"audio_decode_failed: unexpected RIFF form '{form}'");

        var info = new XwmaInfo { DataOffset = -1 };
        int pos = 12;
        bool sawFmt = false;
        while (pos + 8 <= data.Length)
        {
            string chunk = FourCc(data, pos);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            int chunkData = pos + 8;
            long chunkEnd = (long)chunkData + size;
            if (chunkEnd > data.Length && chunk != "data") chunkEnd = data.Length;

            if (chunk == "fmt ")
            {
                if (size < 18) throw new InvalidDataException("audio_decode_failed: fmt chunk too small");
                info.Tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkData));
                info.Channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkData + 2));
                info.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkData + 4));
                info.ByteRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkData + 8));
                info.BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkData + 12));
                info.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkData + 14));
                int extraSize = size >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkData + 16)) : 0;
                if (extraSize > 0 && size >= 18 + extraSize)
                {
                    info.ExtraData = new byte[extraSize];
                    Array.Copy(data, chunkData + 18, info.ExtraData, 0, extraSize);
                }
                sawFmt = true;
            }
            else if (chunk == "dpds")
            {
                int count = (int)(size / 4);
                info.PacketCumulativeSizes = new uint[count];
                for (int i = 0; i < count; i++)
                    info.PacketCumulativeSizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkData + i * 4));
            }
            else if (chunk == "data")
            {
                info.DataOffset = chunkData;
                info.DataLength = (int)Math.Min(size, data.Length - chunkData);
                break; // data is assumed last, as in FFmpeg's demuxer
            }

            long next = chunkEnd + (size & 1); // RIFF chunks are word aligned
            if (next <= pos) break;
            pos = (int)Math.Min(next, data.Length);
        }

        if (!sawFmt) throw new InvalidDataException("audio_decode_failed: missing fmt chunk");
        if (info.DataOffset < 0) throw new InvalidDataException("audio_decode_failed: missing data chunk");
        return info;
    }

    // FFmpeg's xWMA demuxer normalises fake bitrates produced by some encoders.
    internal static int NormalizeByteRate(XwmaInfo info)
    {
        int br = info.ByteRate * 8;
        int ch = info.Channels, sr = info.SampleRate;
        if (info.Tag == 0x0161)
        {
            if (ch == 1)
            {
                if (sr == 22050 && (br == 48000 || br == 192000)) br = 20000;
                else if (sr == 32000 && (br == 48000 || br == 192000)) br = 20000;
                else if (sr == 44100 && (br == 96000 || br == 192000)) br = 48000;
            }
            else if (ch == 2)
            {
                if (sr == 22050 && (br == 48000 || br == 192000)) br = 32000;
                else if (sr == 32000 && br == 192000) br = 48000;
            }
        }
        return br / 8;
    }

    /// <summary>Decode a whole xWMA file to interleaved PCM16.</summary>
    public static AudioDecodeResult Decode(byte[] data)
    {
        var result = new AudioDecodeResult();
        XwmaInfo info;
        try { info = Parse(data); }
        catch (Exception ex) { result.Error = ex.Message; return result; }

        result.SampleRate = info.SampleRate;
        result.Channels = info.Channels;
        result.Codec = info.Tag switch
        {
            0x0160 => "WMA v1",
            0x0161 => "WMA v2",
            0x0162 => "WMA Pro",
            0x0163 => "WMA Lossless",
            _ => $"0x{info.Tag:X4}",
        };

        if (info.Tag == 0x0162 || info.Tag == 0x0163)
        {
            result.Error = $"audio_codec_unsupported: {result.Codec} XWM files cannot be previewed yet";
            return result;
        }
        if (info.Tag != 0x0160 && info.Tag != 0x0161)
        {
            result.Error = $"audio_codec_unsupported: format tag 0x{info.Tag:X4}";
            return result;
        }

        try
        {
            byte[] extra = info.ExtraData;
            if (extra.Length == 0)
            {
                // xWMA carries no extradata; FFmpeg synthesises 6 bytes with flags2 = 31.
                extra = new byte[6];
                extra[4] = 31;
            }
            var format = new WmaFormatInfo
            {
                Tag = info.Tag,
                Channels = info.Channels,
                SampleRate = info.SampleRate,
                ByteRate = NormalizeByteRate(info),
                BlockAlign = info.BlockAlign,
                BitsPerSample = info.BitsPerSample,
                ExtraData = extra,
            };
            var decoder = new WmaV2Decoder(format);
            var samples = new List<float[]>[info.Channels];
            for (int i = 0; i < info.Channels; i++) samples[i] = new List<float[]>();
            int totalFrames = 0;

            int blockAlign = info.BlockAlign;
            if (blockAlign <= 0) throw new InvalidDataException("audio_decode_failed: block_align is not set");
            int packetCount = info.DataLength / blockAlign;
            var packet = new byte[blockAlign];
            bool failed = false;
            for (int p = 0; p < packetCount; p++)
            {
                Array.Copy(data, info.DataOffset + p * blockAlign, packet, 0, blockAlign);
                List<float[][]> frames;
                try { frames = decoder.DecodePacket(packet); }
                catch { failed = true; break; }
                foreach (var frame in frames)
                {
                    for (int ch = 0; ch < info.Channels; ch++) samples[ch].Add(frame[ch]);
                    totalFrames++;
                }
            }
            var flush = decoder.Flush();
            if (flush != null)
            {
                for (int ch = 0; ch < info.Channels; ch++) samples[ch].Add(flush[ch]);
                totalFrames++;
            }

            if (totalFrames == 0)
            {
                result.Error = failed ? "audio_decode_failed: decoder rejected the stream"
                                      : "audio_decode_failed: no audio frames decoded";
                return result;
            }

            int frameLen = decoder.FrameLength;
            // FFmpeg reports a delay of two frames for WMA; drop them so the
            // preview lines up with (and is as long as) a reference decode.
            int skipFrames = Math.Min(2, totalFrames - 1);
            int outFrames = totalFrames - skipFrames;
            var pcm = new short[outFrames * frameLen * info.Channels];
            int w = 0;
            for (int f = skipFrames; f < totalFrames; f++)
            {
                for (int i = 0; i < frameLen; i++)
                {
                    for (int ch = 0; ch < info.Channels; ch++)
                        pcm[w++] = WavWriter.FloatToPcm16(samples[ch][f][i]);
                }
            }
            result.Pcm = pcm;
            result.Ok = true;
            return result;
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("audio_codec_unsupported", StringComparison.Ordinal))
        {
            result.Error = ex.Message;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"audio_decode_failed: {ex.Message}";
            return result;
        }
    }
}
