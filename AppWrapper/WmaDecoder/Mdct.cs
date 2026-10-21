// FFT-based inverse MDCT matching FFmpeg's AV_TX_FLOAT_MDCT inverse
// with AV_TX_FULL_IMDCT (libavutil/tx_template.c, LGPL-2.1-or-later):
//   y[n] = -sum_k X[k] * cos((pi/N) * (n + 0.5 + N/2) * (k + 0.5)),
//   n = 0..2N-1, times FFmpeg's MDCT scale of 1/32768.
// Evaluated with one size-2N FFT:
//   B[k] = X[k] * exp(i*pi*k/(2N)) (zero past N),  S = DFT+(B),
//   y[n] = -Re( exp(i*(pi/(4N) + pi*m/(2N))) * S[m mod 2N] ),  m = n + N/2.
using System.Numerics;

namespace WmaDecoder;

internal sealed class Mdct
{
    public const float Scale = 1.0f / 32768.0f;

    private readonly int _n;
    private readonly Complex[] _fftBuffer;

    public Mdct(int inputSize)
    {
        _n = inputSize;
        _fftBuffer = new Complex[inputSize * 2];
    }

    /// <summary>Inverse MDCT: input has N coefficients, output 2N samples.</summary>
    public void Inverse(float[] input, float[] output)
    {
        int n = _n;
        int size = 2 * n;
        Array.Clear(_fftBuffer, 0, _fftBuffer.Length);
        for (int k = 0; k < n; k++)
        {
            double angle = Math.PI * k / size;
            _fftBuffer[k] = new Complex(input[k] * Math.Cos(angle), input[k] * Math.Sin(angle));
        }
        Fft(_fftBuffer, inverse: true);
        int half = n / 2;
        for (int i = 0; i < size; i++)
        {
            int m = i + half;
            double angle = Math.PI / (4.0 * n) + Math.PI * m / size;
            Complex v = _fftBuffer[m % size] * new Complex(Math.Cos(angle), Math.Sin(angle));
            output[i] = (float)(-v.Real * Scale);
        }
    }

    // Iterative radix-2 FFT. inverse=true uses the + exponent and no scaling.
    internal static void Fft(Complex[] data, bool inverse)
    {
        int n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    var u = data[i + j];
                    var v = data[i + j + len / 2] * w;
                    data[i + j] = u + v;
                    data[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }
}
