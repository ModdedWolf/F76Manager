// Bethesda FUZE (.fuz) container: "FUZE" + uint32 version + uint32 lipSize,
// followed by lip-sync data and then an xWMA (RIFF/XWMA) stream to EOF.
// (Layout verified against real game files; there is no xwm-size field.)
using System.Buffers.Binary;
using System.Text;

namespace WmaDecoder;

public sealed class FuzInfo
{
    public uint Version { get; set; }
    public int LipLength { get; set; }
    public int AudioOffset { get; set; }
    public int AudioLength { get; set; }
}

public static class FuzFile
{
    public static bool IsFuz(byte[] data) =>
        data != null && data.Length >= 12 && Encoding.ASCII.GetString(data, 0, 4) == "FUZE";

    public static FuzInfo Parse(byte[] data)
    {
        if (!IsFuz(data))
            throw new InvalidDataException("audio_decode_failed: missing FUZE header");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        int lipLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        int audioOffset = 12 + lipLength;
        if (audioOffset > data.Length)
            throw new InvalidDataException("audio_decode_failed: FUZ lip data runs past end of file");
        return new FuzInfo
        {
            Version = version,
            LipLength = lipLength,
            AudioOffset = audioOffset,
            AudioLength = data.Length - audioOffset,
        };
    }

    public static byte[] GetAudioBytes(byte[] data, FuzInfo info)
    {
        var audio = new byte[info.AudioLength];
        Array.Copy(data, info.AudioOffset, audio, 0, info.AudioLength);
        return audio;
    }

    /// <summary>Decode a whole .fuz file to interleaved PCM16.</summary>
    public static AudioDecodeResult Decode(byte[] data, out int lipBytes)
    {
        lipBytes = 0;
        FuzInfo info;
        try { info = Parse(data); }
        catch (Exception ex) { return new AudioDecodeResult { Error = ex.Message }; }
        lipBytes = info.LipLength;
        return XwmaFile.Decode(GetAudioBytes(data, info));
    }
}
