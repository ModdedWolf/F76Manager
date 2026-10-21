using System;
using System.IO;
using System.IO.Compression;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace F76ManagerApp.Managers
{
    public static class DdsPreview
    {
        public const int MaxEdge = 1024;
        private const int MaxDecodeEdge = 8192;

        public sealed class Result
        {
            public bool Ok { get; init; }
            public bool Unsupported { get; init; }
            public byte[]? Png { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public string Format { get; init; } = "";
            public int Mips { get; init; }
        }

        private enum PixelKind { None, Bc1, Bc2, Bc3, Bc4, Bc5, Bc6, Bc7, Bgra, Rgba, Masked, Half }

        private sealed class Info
        {
            public int Width;
            public int Height;
            public int Depth = 1;
            public int Mips;
            public int HeaderSize;
            public string FormatName = "";
            public PixelKind Kind;
            public bool Block;
            public int BlockBytes;
            public int BytesPerPixel;
            public uint RMask, GMask, BMask, AMask;
        }

        public static Result ToPng(byte[] file, int maxEdge = MaxEdge)
        {
            if (maxEdge < 1) maxEdge = MaxEdge;
            if (!TryParse(file, out Info info))
            {
                return new Result { Unsupported = true };
            }

            if (info.Kind == PixelKind.Bc6 || info.Kind == PixelKind.None)
            {
                return new Result
                {
                    Unsupported = true,
                    Format = info.FormatName,
                    Width = info.Width,
                    Height = info.Height,
                    Mips = info.Mips
                };
            }

            if (!TrySliceMip(file, info, maxEdge, out int mipW, out int mipH, out int offset, out int length))
            {
                return new Result
                {
                    Unsupported = true,
                    Format = info.FormatName,
                    Width = info.Width,
                    Height = info.Height,
                    Mips = info.Mips
                };
            }

            byte[] rgba;
            try
            {
                rgba = Decode(file, offset, length, mipW, mipH, info);
            }
            catch
            {
                rgba = null;
            }

            if (rgba == null || rgba.Length < mipW * mipH * 4)
            {
                return new Result
                {
                    Unsupported = true,
                    Format = info.FormatName,
                    Width = info.Width,
                    Height = info.Height,
                    Mips = info.Mips
                };
            }

            byte[] pngRgba = rgba;
            int pngW = mipW;
            int pngH = mipH;
            if (Math.Max(mipW, mipH) > maxEdge)
            {
                pngRgba = Downscale(rgba, mipW, mipH, maxEdge, out pngW, out pngH);
            }

            return new Result
            {
                Ok = true,
                Png = EncodePng(pngRgba, pngW, pngH),
                Width = info.Width,
                Height = info.Height,
                Format = info.FormatName,
                Mips = info.Mips
            };
        }

        private static byte[] Downscale(byte[] rgba, int width, int height, int maxEdge, out int outWidth, out int outHeight)
        {
            double scale = (double)maxEdge / Math.Max(width, height);
            outWidth = Math.Max(1, (int)Math.Round(width * scale));
            outHeight = Math.Max(1, (int)Math.Round(height * scale));

            var output = new byte[outWidth * outHeight * 4];
            for (int oy = 0; oy < outHeight; oy++)
            {
                int y0 = oy * height / outHeight;
                int y1 = Math.Max(y0 + 1, (oy + 1) * height / outHeight);
                for (int ox = 0; ox < outWidth; ox++)
                {
                    int x0 = ox * width / outWidth;
                    int x1 = Math.Max(x0 + 1, (ox + 1) * width / outWidth);

                    long r = 0, g = 0, b = 0, a = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * width * 4;
                        for (int x = x0; x < x1; x++)
                        {
                            int src = row + x * 4;
                            r += rgba[src];
                            g += rgba[src + 1];
                            b += rgba[src + 2];
                            a += rgba[src + 3];
                        }
                    }

                    long count = (long)(x1 - x0) * (y1 - y0);
                    int dest = (oy * outWidth + ox) * 4;
                    output[dest] = (byte)(r / count);
                    output[dest + 1] = (byte)(g / count);
                    output[dest + 2] = (byte)(b / count);
                    output[dest + 3] = (byte)(a / count);
                }
            }
            return output;
        }

        private static bool TryParse(byte[] file, out Info info)
        {
            info = null;
            if (file == null || file.Length < 128) return false;
            if (file[0] != (byte)'D' || file[1] != (byte)'D' || file[2] != (byte)'S' || file[3] != (byte)' ')
                return false;

            uint headerFlags = ReadU32(file, 8);
            int height = ReadI32(file, 12);
            int width = ReadI32(file, 16);
            int depth = ReadI32(file, 24);
            int mips = ReadI32(file, 28);
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return false;
            if ((headerFlags & 0x800000u) == 0 || depth <= 1) depth = 1;
            if (depth > 4096) return false;
            if (mips <= 0) mips = 1;
            if (mips > 16) mips = 16;

            uint pfFlags = ReadU32(file, 80);
            string fourCc = FourCc(file, 84);
            int bitCount = ReadI32(file, 88);
            uint rMask = ReadU32(file, 92);
            uint gMask = ReadU32(file, 96);
            uint bMask = ReadU32(file, 100);
            uint aMask = ReadU32(file, 104);

            var parsed = new Info
            {
                Width = width,
                Height = height,
                Depth = depth,
                Mips = mips,
                HeaderSize = 128
            };

            bool fourCcFormat = (pfFlags & 0x4u) != 0 && fourCc.Trim('\0', ' ').Length > 0;
            if (fourCcFormat && fourCc == "DX10")
            {
                if (file.Length < 148) return false;
                uint dxgi = ReadU32(file, 128);
                parsed.HeaderSize = 148;
                if (!MapDxgi(dxgi, parsed)) return false;
            }
            else if (fourCcFormat)
            {
                if (!MapFourCc(fourCc, parsed)) return false;
            }
            else if (TryMaskedPixelFormat(pfFlags, bitCount, rMask, gMask, bMask, aMask, parsed))
            {
            }
            else
            {
                return false;
            }

            info = parsed;
            return parsed.Kind != PixelKind.None || parsed.FormatName.Length > 0;
        }

        private static bool MapDxgi(uint dxgi, Info info)
        {
            switch (dxgi)
            {
                case 70:
                case 71:
                case 72:
                    return SetBlock(info, PixelKind.Bc1, 8, "BC1");
                case 73:
                case 74:
                case 75:
                    return SetBlock(info, PixelKind.Bc2, 16, "BC2");
                case 76:
                case 77:
                case 78:
                    return SetBlock(info, PixelKind.Bc3, 16, "BC3");
                case 79:
                case 80:
                case 81:
                    return SetBlock(info, PixelKind.Bc4, 8, "BC4");
                case 82:
                case 83:
                case 84:
                    return SetBlock(info, PixelKind.Bc5, 16, "BC5");
                case 94:
                case 95:
                case 96:
                    info.Kind = PixelKind.Bc6;
                    info.FormatName = "BC6H";
                    return true;
                case 97:
                case 98:
                case 99:
                    return SetBlock(info, PixelKind.Bc7, 16, "BC7");
                case 27:
                case 28:
                case 29:
                    info.Kind = PixelKind.Rgba;
                    info.BytesPerPixel = 4;
                    info.FormatName = "RGBA";
                    return true;
                case 87:
                case 90:
                case 91:
                    info.Kind = PixelKind.Bgra;
                    info.BytesPerPixel = 4;
                    info.FormatName = "BGRA";
                    return true;
                case 88:
                    return SetMasked(info, 4, 0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0u, "BGRX");
                case 85:
                    return SetMasked(info, 2, 0xF800u, 0x07E0u, 0x001Fu, 0u, "B5G6R5");
                case 86:
                    return SetMasked(info, 2, 0x7C00u, 0x03E0u, 0x001Fu, 0x8000u, "B5G5R5A1");
                case 115:
                    return SetMasked(info, 2, 0x0F00u, 0x00F0u, 0x000Fu, 0xF000u, "B4G4R4A4");
                case 61:
                    return SetMasked(info, 1, 0xFFu, 0xFFu, 0xFFu, 0u, "R8");
                case 65:
                    return SetMasked(info, 1, 0u, 0u, 0u, 0xFFu, "A8");
                case 49:
                    return SetMasked(info, 2, 0x00FFu, 0xFF00u, 0u, 0u, "R8G8");
                case 24:
                    return SetMasked(info, 4, 0x3FFu, 0xFFC00u, 0x3FF00000u, 0xC0000000u, "R10G10B10A2");
                case 10:
                    info.Kind = PixelKind.Half;
                    info.BytesPerPixel = 8;
                    info.FormatName = "RGBA16F";
                    return true;
                default:
                    info.Kind = PixelKind.None;
                    info.FormatName = $"DXGI {dxgi}";
                    return true;
            }
        }

        private static bool MapFourCc(string fourCc, Info info)
        {
            switch (fourCc)
            {
                case "DXT1":
                    return SetBlock(info, PixelKind.Bc1, 8, "BC1");
                case "DXT2":
                case "DXT3":
                    return SetBlock(info, PixelKind.Bc2, 16, "BC2");
                case "DXT4":
                case "DXT5":
                    return SetBlock(info, PixelKind.Bc3, 16, "BC3");
                case "ATI1":
                case "BC4U":
                case "BC4S":
                    return SetBlock(info, PixelKind.Bc4, 8, "BC4");
                case "ATI2":
                case "BC5U":
                case "BC5S":
                    return SetBlock(info, PixelKind.Bc5, 16, "BC5");
                case "BC7 ":
                case "BC7":
                    return SetBlock(info, PixelKind.Bc7, 16, "BC7");
                default:
                    info.Kind = PixelKind.None;
                    info.FormatName = fourCc.Trim();
                    return true;
            }
        }

        private static bool TryMaskedPixelFormat(uint pfFlags, int bitCount, uint rMask, uint gMask, uint bMask, uint aMask, Info info)
        {
            bool rgb = (pfFlags & 0x40u) != 0;
            bool luminance = (pfFlags & 0x20000u) != 0;
            bool alphaOnly = (pfFlags & 0x2u) != 0 && !rgb && !luminance;
            if (!rgb && !luminance && !alphaOnly) return false;
            if (bitCount != 8 && bitCount != 16 && bitCount != 24 && bitCount != 32) return false;
            if (bitCount % 8 != 0) return false;

            if (luminance && rMask == 0 && gMask == 0 && bMask == 0)
            {
                uint lum = bitCount >= 32 ? 0xFFu : (uint)((1 << bitCount) - 1);
                rMask = gMask = bMask = lum;
            }
            if (alphaOnly)
            {
                aMask = bitCount >= 32 ? 0xFFu : (uint)((1 << Math.Min(bitCount, 31)) - 1);
            }

            string name = aMask != 0 ? "RGBA" : luminance ? "Luminance" : "RGB";
            return SetMasked(info, bitCount / 8, rMask, gMask, bMask, aMask, name);
        }

        private static bool SetMasked(Info info, int bytesPerPixel, uint rMask, uint gMask, uint bMask, uint aMask, string name)
        {
            info.Kind = PixelKind.Masked;
            info.BytesPerPixel = bytesPerPixel;
            info.RMask = rMask;
            info.GMask = gMask;
            info.BMask = bMask;
            info.AMask = aMask;
            info.FormatName = name;
            return true;
        }

        private static bool SetBlock(Info info, PixelKind kind, int blockBytes, string name)
        {
            info.Kind = kind;
            info.Block = true;
            info.BlockBytes = blockBytes;
            info.FormatName = name;
            return true;
        }

        private static bool TrySliceMip(byte[] file, Info info, int maxEdge, out int width, out int height, out int offset, out int length)
        {
            width = info.Width;
            height = info.Height;
            offset = info.HeaderSize;
            length = 0;

            for (int mip = 0; mip < info.Mips; mip++)
            {
                int mw = Math.Max(1, info.Width >> mip);
                int mh = Math.Max(1, info.Height >> mip);
                int depth = Math.Max(1, info.Depth >> mip);
                int slice = SliceBytes(mw, mh, info);
                int size = slice * depth;
                if (slice <= 0 || size <= 0 || offset > file.Length - size) return false;

                bool smallEnough = Math.Max(mw, mh) <= maxEdge;
                bool last = mip == info.Mips - 1;
                if (smallEnough || last)
                {
                    // No mip fits the preview size (e.g. a single 2048px mip):
                    // decode the smallest mip anyway and downscale it in ToPng,
                    // unless it is so large that decoding it would be unsafe.
                    if (!smallEnough && Math.Max(mw, mh) > MaxDecodeEdge) return false;
                    width = mw;
                    height = mh;
                    length = slice;
                    return true;
                }

                offset += size;
            }

            return false;
        }

        private static int SliceBytes(int width, int height, Info info)
        {
            if (info.Block)
            {
                int blocksWide = Math.Max(1, (width + 3) / 4);
                int blocksHigh = Math.Max(1, (height + 3) / 4);
                return blocksWide * blocksHigh * info.BlockBytes;
            }
            return width * height * info.BytesPerPixel;
        }

        private static byte[] Decode(byte[] file, int offset, int length, int width, int height, Info info)
        {
            if (info.Kind == PixelKind.Rgba)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(file, offset, copy, 0, length);
                return copy;
            }
            if (info.Kind == PixelKind.Bgra)
                return SwizzleBgra(file, offset, width * height);
            if (info.Kind == PixelKind.Half)
                return DecodeHalf(file, offset, width * height);
            if (info.Kind == PixelKind.Masked)
                return SwizzleMasks(file, offset, width * height, info);

            CompressionFormat format = info.Kind switch
            {
                PixelKind.Bc1 => CompressionFormat.Bc1WithAlpha,
                PixelKind.Bc2 => CompressionFormat.Bc2,
                PixelKind.Bc3 => CompressionFormat.Bc3,
                PixelKind.Bc4 => CompressionFormat.Bc4,
                PixelKind.Bc5 => CompressionFormat.Bc5,
                PixelKind.Bc7 => CompressionFormat.Bc7,
                _ => CompressionFormat.Unknown
            };
            if (format == CompressionFormat.Unknown) return null;

            var slice = new byte[length];
            Buffer.BlockCopy(file, offset, slice, 0, length);
            ColorRgba32[] pixels = new BcDecoder().DecodeRaw(slice, width, height, format);
            if (pixels == null || pixels.Length < width * height) return null;

            var rgba = new byte[width * height * 4];
            int count = width * height;
            for (int i = 0; i < count; i++)
            {
                rgba[i * 4] = pixels[i].r;
                rgba[i * 4 + 1] = pixels[i].g;
                rgba[i * 4 + 2] = pixels[i].b;
                rgba[i * 4 + 3] = pixels[i].a;
            }
            return rgba;
        }

        private static byte[] SwizzleBgra(byte[] file, int offset, int pixels)
        {
            var rgba = new byte[pixels * 4];
            for (int i = 0; i < pixels; i++)
            {
                int src = offset + i * 4;
                rgba[i * 4] = file[src + 2];
                rgba[i * 4 + 1] = file[src + 1];
                rgba[i * 4 + 2] = file[src];
                rgba[i * 4 + 3] = file[src + 3];
            }
            return rgba;
        }

        private static byte[] SwizzleMasks(byte[] file, int offset, int pixels, Info info)
        {
            int bpp = info.BytesPerPixel;
            bool alphaOnly = info.RMask == 0 && info.GMask == 0 && info.BMask == 0 && info.AMask != 0;
            var rgba = new byte[pixels * 4];
            for (int i = 0; i < pixels; i++)
            {
                int src = offset + i * bpp;
                uint pixel = 0;
                int take = Math.Min(bpp, 4);
                for (int b = 0; b < take; b++)
                    pixel |= (uint)file[src + b] << (8 * b);
                byte r = Channel(pixel, info.RMask);
                byte g = Channel(pixel, info.GMask);
                byte bch = Channel(pixel, info.BMask);
                byte a = info.AMask == 0 ? (byte)255 : Channel(pixel, info.AMask);
                if (alphaOnly)
                {
                    r = g = bch = a;
                    a = 255;
                }
                rgba[i * 4] = r;
                rgba[i * 4 + 1] = g;
                rgba[i * 4 + 2] = bch;
                rgba[i * 4 + 3] = a;
            }
            return rgba;
        }

        private static byte[] DecodeHalf(byte[] file, int offset, int pixels)
        {
            var rgba = new byte[pixels * 4];
            for (int i = 0; i < pixels; i++)
            {
                int src = offset + i * 8;
                rgba[i * 4] = ClampHalf(file, src);
                rgba[i * 4 + 1] = ClampHalf(file, src + 2);
                rgba[i * 4 + 2] = ClampHalf(file, src + 4);
                rgba[i * 4 + 3] = ClampHalf(file, src + 6);
            }
            return rgba;
        }

        private static byte ClampHalf(byte[] file, int offset)
        {
            ushort h = (ushort)(file[offset] | (file[offset + 1] << 8));
            int sign = (h >> 15) & 1;
            int exp = (h >> 10) & 0x1F;
            int mant = h & 0x3FF;
            float value;
            if (exp == 0) value = mant / 1024f * MathF.Pow(2f, -14f);
            else if (exp == 31) value = mant == 0 ? float.PositiveInfinity : float.NaN;
            else value = (1f + mant / 1024f) * MathF.Pow(2f, exp - 15);
            if (sign != 0) value = -value;
            if (float.IsNaN(value) || value <= 0f) return 0;
            if (value >= 1f) return 255;
            return (byte)(value * 255f);
        }

        private static byte Channel(uint pixel, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            uint probe = mask;
            while ((probe & 1u) == 0)
            {
                probe >>= 1;
                shift++;
            }
            uint bits = mask >> shift;
            int nbits = 0;
            uint countProbe = bits;
            while (countProbe != 0)
            {
                nbits++;
                countProbe >>= 1;
            }
            uint value = (pixel & mask) >> shift;
            if (nbits >= 8) return (byte)(value >> (nbits - 8));
            if (bits == 0) return 0;
            return (byte)((value * 255u) / bits);
        }

        private static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int dest = y * (stride + 1);
                raw[dest] = 0;
                Buffer.BlockCopy(rgba, y * stride, raw, dest + 1, stride);
            }

            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    z.Write(raw, 0, raw.Length);
                compressed = ms.ToArray();
            }

            using var png = new MemoryStream(compressed.Length + 128);
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width);
            WriteBE(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", compressed);
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream png, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, data.Length);
            png.Write(len, 0, 4);
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            png.Write(typeBytes, 0, 4);
            if (data.Length > 0) png.Write(data, 0, data.Length);

            var crcBuf = new byte[4 + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcBuf, 0, 4);
            if (data.Length > 0) Buffer.BlockCopy(data, 0, crcBuf, 4, data.Length);
            var crc = new byte[4];
            WriteBE(crc, 0, (int)PngCrc(crcBuf));
            png.Write(crc, 0, 4);
        }

        private static void WriteBE(byte[] dest, int offset, int value)
        {
            dest[offset] = (byte)(value >> 24);
            dest[offset + 1] = (byte)(value >> 16);
            dest[offset + 2] = (byte)(value >> 8);
            dest[offset + 3] = (byte)value;
        }

        private static uint PngCrc(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc ^ 0xFFFFFFFFu;
        }

        private static int ReadI32(byte[] file, int offset) => (int)ReadU32(file, offset);

        private static uint ReadU32(byte[] file, int offset)
        {
            return (uint)(file[offset] | (file[offset + 1] << 8) | (file[offset + 2] << 16) | (file[offset + 3] << 24));
        }

        private static string FourCc(byte[] file, int offset)
        {
            return System.Text.Encoding.ASCII.GetString(file, offset, 4);
        }
    }
}
