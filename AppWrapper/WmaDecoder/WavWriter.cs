using System.Text;

namespace WmaDecoder;

/// <summary>Minimal RIFF/WAV writer for 16-bit PCM output.</summary>
public static class WavWriter
{
    public static byte[] WritePcm16(int sampleRate, int channels, ReadOnlySpan<short> interleaved)
    {
        int dataBytes = interleaved.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        var buffer = new byte[Math.Min(dataBytes, 65536)];
        int offset = 0;
        while (offset < interleaved.Length)
        {
            int count = Math.Min(buffer.Length / 2, interleaved.Length - offset);
            for (int i = 0; i < count; i++)
            {
                short s = interleaved[offset + i];
                buffer[i * 2] = (byte)(s & 0xFF);
                buffer[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            bw.Write(buffer, 0, count * 2);
            offset += count;
        }
        bw.Flush();
        return ms.ToArray();
    }

    public static short FloatToPcm16(float value)
    {
        if (float.IsNaN(value)) return 0;
        float clamped = Math.Clamp(value, -1.0f, 1.0f);
        return (short)Math.Round(clamped * 32767.0f);
    }
}
