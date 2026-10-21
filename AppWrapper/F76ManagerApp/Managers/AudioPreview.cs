using System;
using System.Linq;
using WmaDecoder;

namespace F76ManagerApp.Managers
{
    /// <summary>
    /// Builds playable audio for the bundle preview pane. MP3/WAV/OGG/FLAC
    /// are passed through for WebView2 to play natively; XWM and FUZ are
    /// decoded to WAV by the managed WmaDecoder (no external tools needed).
    /// </summary>
    public static class AudioPreview
    {
        public sealed class Result
        {
            public bool Ok { get; init; }
            public byte[]? Bytes { get; init; }
            public string Mime { get; init; } = "";
            public string Codec { get; init; } = "";
            public int SampleRate { get; init; }
            public int Channels { get; init; }
            public double DurationSec { get; init; }
            public int LipBytes { get; init; }
            public bool Silent { get; init; }
            public string? Error { get; init; }
        }

        private static readonly string[] AudioExtensions =
            { ".mp3", ".wav", ".ogg", ".flac", ".xwm", ".fuz" };

        public static bool IsAudioExtension(string ext)
        {
            foreach (string candidate in AudioExtensions)
                if (candidate.Equals(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static Result Build(string ext, byte[] bytes)
        {
            bytes ??= Array.Empty<byte>();
            try
            {
                if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                    return PassThrough(bytes, "audio/mpeg", "MP3");
                if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
                    return PassThrough(bytes, "audio/ogg", DetectOggCodec(bytes));
                if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase))
                    return BuildFlac(bytes);
                if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                    return BuildWav(bytes);
                if (ext.Equals(".xwm", StringComparison.OrdinalIgnoreCase))
                    return FromDecoded(XwmaFile.Decode(bytes), 0);
                if (ext.Equals(".fuz", StringComparison.OrdinalIgnoreCase))
                {
                    var decoded = FuzFile.Decode(bytes, out int lipBytes);
                    return FromDecoded(decoded, lipBytes);
                }
            }
            catch (Exception ex)
            {
                return new Result { Error = $"audio_decode_failed: {ex.Message}" };
            }
            return new Result { Error = "audio_codec_unsupported: not an audio file" };
        }

        private static Result PassThrough(byte[] bytes, string mime, string codec,
            int sampleRate = 0, int channels = 0, double durationSec = 0)
        {
            return new Result
            {
                Ok = bytes.Length > 0,
                Bytes = bytes,
                Mime = mime,
                Codec = codec,
                SampleRate = sampleRate,
                Channels = channels,
                DurationSec = durationSec,
                Error = bytes.Length > 0 ? null : "audio_decode_failed: empty file"
            };
        }

        private static Result FromDecoded(AudioDecodeResult decoded, int lipBytes)
        {
            if (!decoded.Ok)
                return new Result { Error = decoded.Error ?? "audio_decode_failed", LipBytes = lipBytes };
            return new Result
            {
                Ok = true,
                Bytes = WavWriter.WritePcm16(decoded.SampleRate, decoded.Channels, decoded.Pcm),
                Mime = "audio/wav",
                Codec = decoded.Codec,
                SampleRate = decoded.SampleRate,
                Channels = decoded.Channels,
                DurationSec = decoded.DurationSeconds,
                LipBytes = lipBytes,
                Silent = decoded.Pcm.Length > 0 && decoded.Pcm.All(s => s == 0)
            };
        }

        private static string DetectOggCodec(byte[] bytes)
        {
            // OpusHead / Vorbis identification headers live in the first packets.
            int limit = Math.Min(bytes.Length, 4096);
            for (int i = 0; i + 8 <= limit; i++)
            {
                if (bytes[i] == (byte)'O' && bytes[i + 1] == (byte)'p' && bytes[i + 2] == (byte)'u' && bytes[i + 3] == (byte)'s')
                    return "Opus";
                if (bytes[i] == 0x01 && bytes[i + 1] == (byte)'v' && bytes[i + 2] == (byte)'o')
                    return "Vorbis";
            }
            return "OGG";
        }

        private static Result BuildFlac(byte[] bytes)
        {
            int sampleRate = 0, channels = 0;
            double duration = 0;
            try
            {
                // "fLaC" + metadata block header (4) + STREAMINFO (34 bytes):
                // sample rate is 20 bits at STREAMINFO+10, channels 3 bits
                // after it, total samples the low 36 bits at STREAMINFO+13.
                if (bytes.Length >= 42 && bytes[0] == (byte)'f' && bytes[1] == (byte)'L' &&
                    bytes[2] == (byte)'a' && bytes[3] == (byte)'C')
                {
                    sampleRate = (bytes[18] << 12) | (bytes[19] << 4) | (bytes[20] >> 4);
                    channels = ((bytes[20] >> 1) & 0x7) + 1;
                    long totalSamples = ((long)(bytes[21] & 0x0F) << 32) |
                        ((long)bytes[22] << 24) | ((long)bytes[23] << 16) | ((long)bytes[24] << 8) | bytes[25];
                    if (sampleRate > 0 && totalSamples > 0) duration = (double)totalSamples / sampleRate;
                }
            }
            catch { /* metadata is best-effort only */ }
            return PassThrough(bytes, "audio/flac", "FLAC", sampleRate, channels, duration);
        }

        private static Result BuildWav(byte[] bytes)
        {
            ushort tag = 0;
            int channels = 0, sampleRate = 0, byteRate = 0, dataBytes = 0;
            try
            {
                if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' &&
                    bytes[2] == (byte)'F' && bytes[3] == (byte)'F')
                {
                    int pos = 12;
                    while (pos + 8 <= bytes.Length)
                    {
                        string chunk = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                        uint size = BitConverter.ToUInt32(bytes, pos + 4);
                        int chunkData = pos + 8;
                        if (chunk == "fmt " && size >= 16 && chunkData + 16 <= bytes.Length)
                        {
                            tag = BitConverter.ToUInt16(bytes, chunkData);
                            channels = BitConverter.ToUInt16(bytes, chunkData + 2);
                            sampleRate = (int)BitConverter.ToUInt32(bytes, chunkData + 4);
                            byteRate = (int)BitConverter.ToUInt32(bytes, chunkData + 8);
                        }
                        else if (chunk == "data")
                        {
                            dataBytes = (int)Math.Min(size, bytes.Length - chunkData);
                            break;
                        }
                        long next = (long)chunkData + size + (size & 1);
                        if (next <= pos) break;
                        pos = (int)Math.Min(next, bytes.Length);
                    }
                }
            }
            catch { /* fall through to the tag check below */ }

            if (tag == 1 || tag == 3)
            {
                double duration = byteRate > 0 && dataBytes > 0 ? (double)dataBytes / byteRate : 0;
                return PassThrough(bytes, "audio/wav", tag == 1 ? "PCM WAV" : "Float WAV",
                    sampleRate, channels, duration);
            }
            string codecName = tag switch
            {
                0x0055 => "MP3",
                0x0161 => "WMA v2",
                0x0162 => "WMA Pro",
                _ => tag == 0 ? "unknown" : $"0x{tag:X4}"
            };
            return new Result { Error = $"audio_codec_unsupported: WAV codec {codecName} cannot be played directly" };
        }
    }
}
