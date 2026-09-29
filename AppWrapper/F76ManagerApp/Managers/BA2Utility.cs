using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.IO.Compression;
using System.Diagnostics;

namespace F76ManagerApp.Managers
{
    public static class BA2Utility
    {
        public class BA2FileRecord
        {
            public string Name { get; set; }
            public uint Hash { get; set; }
            public byte[] Ext { get; set; } = new byte[4];
            public uint DirHash { get; set; }
            public uint Flags { get; set; }
            public ulong Offset { get; set; }
            public uint PackedSize { get; set; }
            public uint UnpackedSize { get; set; }
            public uint Align { get; set; }

            public byte Unk8 { get; set; }
            public byte NumChunks { get; set; }
            public ushort ChunkHdrLen { get; set; }
            public ushort Height { get; set; }
            public ushort Width { get; set; }
            public byte NumMips { get; set; }
            public byte Format { get; set; }
            public byte IsCubemap { get; set; }
            public byte TileMode { get; set; }
            public List<BA2TextureChunk> Chunks { get; set; } = new List<BA2TextureChunk>();
        }

        public class BA2TextureChunk
        {
            public ulong Offset { get; set; }
            public uint PackSize { get; set; }
            public uint FullSize { get; set; }
            public ushort StartMip { get; set; }
            public ushort EndMip { get; set; }
            public uint Align { get; set; }
        }

        private const uint BA2_ALIGN_MARKER = 0xBAADF00Du;
        private const uint BA2_GNRL_FLAGS = 0x00100100u;
        private const byte BA2_TILE_MODE = 0x08;
        private const int ARCHIVE2_MAX_CHUNKS = 4;
        private const int ARCHIVE2_MIP_CHUNK_THRESHOLD = 512;

        public class BA2EntryInfo
        {
            public string Path { get; set; } = "";
            public uint UnpackedSize { get; set; }
        }

        public class BA2ListResult
        {
            public string ArchiveType { get; set; } = "";
            public List<BA2EntryInfo> Entries { get; set; } = new List<BA2EntryInfo>();
        }

        public class BA2Fingerprint
        {
            public string ArchiveType { get; set; } = "";
            public uint UnpackedSize { get; set; }
            public string Md5Hex { get; set; } = "";
            public bool Ok { get; set; }
        }

        public static BA2Fingerprint TryGetEntryFingerprint(string ba2Path, string entryRelativePath, Action<string> logger = null)
        {
            var result = new BA2Fingerprint();
            try
            {
                string want = (entryRelativePath ?? "").Replace("\\", "/").TrimStart('/').ToLowerInvariant();
                using var fs = new FileStream(ba2Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var br = new BinaryReader(fs);
                var (type, records) = ReadArchiveIndex(fs, br);
                result.ArchiveType = type;

                var rec = records.FirstOrDefault(r =>
                    (r.Name ?? "").Replace("\\", "/").TrimStart('/').Equals(want, StringComparison.OrdinalIgnoreCase));
                if (rec == null) return result;

                byte[] payload;
                if (type == "GNRL")
                {
                    result.UnpackedSize = rec.UnpackedSize;
                    fs.Seek((long)rec.Offset, SeekOrigin.Begin);
                    if (rec.PackedSize == 0)
                    {
                        payload = rec.UnpackedSize == 0
                            ? Array.Empty<byte>()
                            : br.ReadBytes((int)rec.UnpackedSize);
                    }
                    else
                    {
                        byte[] data = br.ReadBytes((int)rec.PackedSize);
                        if (rec.PackedSize < rec.UnpackedSize)
                        {
                            data = DecompressZlibData(data, rec.UnpackedSize, logger, rec.Name);
                            if (data == null) return result;
                        }
                        payload = data;
                    }
                }
                else
                {
                    using var ms = new MemoryStream();
                    foreach (var chunk in rec.Chunks)
                    {
                        fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                        uint readSize = chunk.PackSize == 0 ? chunk.FullSize : chunk.PackSize;
                        byte[] chunkData = br.ReadBytes((int)readSize);
                        if (chunk.PackSize != 0 && chunk.PackSize < chunk.FullSize)
                        {
                            chunkData = DecompressZlibData(chunkData, chunk.FullSize, logger, rec.Name);
                            if (chunkData == null) return result;
                        }
                        ms.Write(chunkData, 0, chunkData.Length);
                        result.UnpackedSize += chunk.FullSize;
                    }
                    payload = ms.ToArray();
                }

                using var md5 = System.Security.Cryptography.MD5.Create();
                byte[] hash = md5.ComputeHash(payload ?? Array.Empty<byte>());
                result.Md5Hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BA2] Fingerprint failed for {Path.GetFileName(ba2Path)}::{entryRelativePath}: {ex.Message}");
                return result;
            }
        }

        private static (string type, List<BA2FileRecord> records) ReadArchiveIndex(FileStream fs, BinaryReader br)
        {
            byte[] sig = br.ReadBytes(4);
            if (Encoding.ASCII.GetString(sig) != "BTDX") throw new Exception("Invalid BA2 signature");
            br.ReadUInt32();
            string type = Encoding.ASCII.GetString(br.ReadBytes(4)).TrimEnd('\0', ' ');
            uint numFiles = br.ReadUInt32();
            ulong nameTableOffset = br.ReadUInt64();

            var records = new List<BA2FileRecord>();
            for (int i = 0; i < numFiles; i++)
            {
                var rec = new BA2FileRecord
                {
                    Hash = br.ReadUInt32(),
                    Ext = br.ReadBytes(4),
                    DirHash = br.ReadUInt32()
                };
                if (type == "GNRL")
                {
                    rec.Flags = br.ReadUInt32();
                    rec.Offset = br.ReadUInt64();
                    rec.PackedSize = br.ReadUInt32();
                    rec.UnpackedSize = br.ReadUInt32();
                    rec.Align = br.ReadUInt32();
                }
                else
                {
                    rec.Unk8 = br.ReadByte();
                    rec.NumChunks = br.ReadByte();
                    rec.ChunkHdrLen = br.ReadUInt16();
                    rec.Height = br.ReadUInt16();
                    rec.Width = br.ReadUInt16();
                    rec.NumMips = br.ReadByte();
                    rec.Format = br.ReadByte();
                    rec.IsCubemap = br.ReadByte();
                    rec.TileMode = br.ReadByte();

                    int hdrLen = rec.ChunkHdrLen == 0 ? 24 : rec.ChunkHdrLen;
                    for (int j = 0; j < rec.NumChunks; j++)
                    {
                        long chunkStart = fs.Position;
                        rec.Chunks.Add(new BA2TextureChunk
                        {
                            Offset = br.ReadUInt64(),
                            PackSize = br.ReadUInt32(),
                            FullSize = br.ReadUInt32(),
                            StartMip = br.ReadUInt16(),
                            EndMip = br.ReadUInt16(),
                            Align = br.ReadUInt32()
                        });
                        fs.Seek(chunkStart + hdrLen, SeekOrigin.Begin);
                    }
                }
                records.Add(rec);
            }

            fs.Seek((long)nameTableOffset, SeekOrigin.Begin);
            for (int i = 0; i < numFiles; i++)
            {
                ushort len = br.ReadUInt16();
                records[i].Name = Encoding.UTF8.GetString(br.ReadBytes(len)).TrimEnd('\0');
            }

            return (type, records);
        }

        public static BA2ListResult ListEntries(string ba2Path, Action<string> logger = null)
        {
            try
            {
                using (var fs = new FileStream(ba2Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    var (type, records) = ReadArchiveIndex(fs, br);
                    var result = new BA2ListResult { ArchiveType = type };
                    foreach (var rec in records)
                    {
                        uint size = type == "GNRL"
                            ? rec.UnpackedSize
                            : rec.Chunks.Aggregate(0u, (sum, c) => sum + c.FullSize);
                        result.Entries.Add(new BA2EntryInfo
                        {
                            Path = rec.Name,
                            UnpackedSize = size
                        });
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BA2] List error: {ex.Message}");
                throw;
            }
        }

        public static void Extract(string ba2Path, string targetDir, Action<string> logger = null)
        {
            Extract(ba2Path, targetDir, logger, null);
        }

        public static void Extract(string ba2Path, string targetDir, Action<string> logger, IReadOnlySet<string>? includePaths)
        {
            try
            {
                using (var fs = new FileStream(ba2Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    var (type, records) = ReadArchiveIndex(fs, br);

                    foreach (var rec in records)
                    {
                        if (includePaths != null)
                        {
                            string normalized = rec.Name.Replace("\\", "/").ToLowerInvariant();
                            if (!includePaths.Contains(normalized)) continue;
                        }

                        string outPath = Path.Combine(targetDir, rec.Name.Replace("/", "\\"));
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                        if (type == "GNRL")
                        {
                            fs.Seek((long)rec.Offset, SeekOrigin.Begin);

                            if (rec.PackedSize == 0)
                            {
                                byte[] data = rec.UnpackedSize == 0
                                    ? Array.Empty<byte>()
                                    : br.ReadBytes((int)rec.UnpackedSize);
                                File.WriteAllBytes(outPath, data);
                            }
                            else
                            {
                                byte[] data = br.ReadBytes((int)rec.PackedSize);

                                if (rec.PackedSize < rec.UnpackedSize)
                                {
                                    data = DecompressZlibData(data, rec.UnpackedSize, logger, rec.Name);
                                    if (data == null)
                                    {
                                        continue;
                                    }
                                }
                                File.WriteAllBytes(outPath, data);
                            }
                        }
                        else
                        {
                            byte[] pixelData = new byte[0];
                            foreach (var chunk in rec.Chunks)
                            {
                                fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                                uint readSize = chunk.PackSize == 0 ? chunk.FullSize : chunk.PackSize;
                                byte[] chunkData = br.ReadBytes((int)readSize);
                                if (chunk.PackSize != 0 && chunk.PackSize < chunk.FullSize)
                                {
                                    chunkData = DecompressZlibData(chunkData, chunk.FullSize, logger, rec.Name);
                                    if (chunkData == null)
                                    {
                                        logger?.Invoke($"Skipping {rec.Name} due to decompression failure.");
                                        break;
                                    }
                                }
                                pixelData = pixelData.Concat(chunkData).ToArray();
                            }
                            if (pixelData.Length == 0) continue;

                            byte[] header = BuildDDSHeader(rec);
                            byte[] fullFile = header.Concat(pixelData).ToArray();
                            File.WriteAllBytes(outPath, fullFile);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BA2] Extraction error: {ex.Message}");
                throw;
            }
        }

        private static byte[] BuildDDSHeader(BA2FileRecord rec)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("DDS "));
                bw.Write(124u);
                uint flags = 0x1u | 0x2u | 0x4u | 0x1000u | 0x80000u;
                if (rec.NumMips > 1) flags |= 0x20000u;
                bw.Write(flags);
                bw.Write((uint)rec.Height);
                bw.Write((uint)rec.Width);
                if (!TryGetDxgiLayout(rec.Format, out int bytesPerBlock, out bool blockCompressed))
                    throw new Exception($"Unsupported DX10 texture format (DXGI {rec.Format}).");
                uint linearSize;
                if (!blockCompressed)
                {
                    linearSize = (uint)rec.Width * (uint)rec.Height * (uint)bytesPerBlock;
                }
                else
                {
                    uint blocksWide = Math.Max(1u, ((uint)rec.Width + 3u) / 4u);
                    uint blocksHigh = Math.Max(1u, ((uint)rec.Height + 3u) / 4u);
                    linearSize = blocksWide * blocksHigh * (uint)bytesPerBlock;
                }
                bw.Write(linearSize);
                bw.Write(0u);
                bw.Write((uint)rec.NumMips);
                for (int i = 0; i < 11; i++) bw.Write(0u);

                bw.Write(32u);

                string fourCC = "";
                bool useLegacy = true;
                switch (rec.Format)
                {
                    case 71: fourCC = "DXT1"; break;
                    case 74: fourCC = "DXT3"; break;
                    case 77: fourCC = "DXT5"; break;
                    case 80: fourCC = "BC4U"; break;
                    case 81: fourCC = "BC4S"; break;
                    case 83: fourCC = "BC5U"; break;
                    case 84: fourCC = "BC5S"; break;
                    case 95: fourCC = "BC6H"; break;
                    case 98: fourCC = "BC7 "; break;
                    default: useLegacy = false; break;
                }

                uint pfFlags = 0x4u;
                bw.Write(pfFlags);
                bw.Write(Encoding.ASCII.GetBytes(useLegacy && fourCC != "" ? fourCC : "DX10"));
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(0u);

                uint caps1 = 0x1000u;
                if (rec.NumMips > 1) caps1 |= 0x8u | 0x400000u;
                bw.Write(caps1);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(0u);

                if (!useLegacy || fourCC == "")
                {
                    bw.Write((uint)rec.Format);
                    bw.Write(3u);
                    bw.Write(0u);
                    bw.Write(1u);
                    bw.Write(0u);
                }

                return ms.ToArray();
            }
        }

        private static byte[] DecompressZlibData(byte[] data, uint unpackedSize, Action<string> logger, string fileName)
        {
            if (data.Length < 6) return null;

            try
            {
                using (var ms = new MemoryStream(data, 2, data.Length - 6))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    ds.CopyTo(output);
                    byte[] result = output.ToArray();
                    if (result.Length == unpackedSize) return result;
                }
            }
            catch (Exception ex) { logger?.Invoke($"[BA2] Decompress mode 1 failed for {fileName}: {ex.Message}"); }

            try
            {
                using (var ms = new MemoryStream(data))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    ds.CopyTo(output);
                    byte[] result = output.ToArray();
                    if (result.Length == unpackedSize) return result;
                }
            }
            catch (Exception ex) { logger?.Invoke($"[BA2] Decompress mode 2 failed for {fileName}: {ex.Message}"); }

            try
            {
                using (var ms = new MemoryStream(data))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    ds.CopyTo(output);
                    byte[] result = output.ToArray();
                    if (result.Length == unpackedSize) return result;
                }
            }
            catch (Exception ex) { logger?.Invoke($"[BA2] Decompress mode 3 failed for {fileName}: {ex.Message}"); }

            logger?.Invoke($"[BA2] Decompression failed for {fileName} in all modes. Skipping.");
            return null;
        }

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
                table[i] = c;
            }
            return table;
        }

        private static uint GetHash(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            uint crc = 0;
            foreach (char ch in name.ToLowerInvariant())
            {
                byte b = ch < 256 ? (byte)ch : (byte)'?';
                crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }

        private static uint GetDirHash(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return 0;
            return GetHash(dir.Replace('/', '\\').Trim('\\'));
        }

        private static byte[] TryZlibCompress(byte[] data, int offset, int count, bool enabled)
        {
            if (!enabled || count == 0) return null;
            try
            {
                using (var ms = new MemoryStream(count))
                {
                    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                        z.Write(data, offset, count);
                    if (ms.Length >= count) return null;
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private struct DdsDesc
        {
            public ushort Width;
            public ushort Height;
            public byte NumMips;
            public byte Format;
            public int HeaderSize;
            public bool IsCubemap;
            public bool Block;
            public bool ExpandToBgra;
            public bool HalfFloatRgba;
            public int SrcBpp;
            public uint RMask, GMask, BMask, AMask;
            public bool HasAlpha;
            public bool Pitched;
            public int Pitch;
        }

        private struct PreparedDds
        {
            public ushort Width;
            public ushort Height;
            public byte NumMips;
            public byte Format;
            public bool IsCubemap;
            public byte[] Data;
            public int PixelOffset;
            public int PixelLength;
        }

        private static (ushort Width, ushort Height, byte NumMips, byte Format, int HeaderSize) ParseDDSHeader(byte[] data)
        {
            var desc = DescribeDds(data);
            return (desc.Width, desc.Height, desc.NumMips, desc.Format, desc.HeaderSize);
        }

        private static PreparedDds ReadDdsForPack(byte[] data, string relPath)
        {
            try
            {
                return MaterializeDds(data, DescribeDds(data));
            }
            catch (Exception ex)
            {
                throw new Exception($"{ex.Message}: {relPath}", ex);
            }
        }

        private static DdsDesc DescribeDds(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                if (data.Length < 128 || Encoding.ASCII.GetString(br.ReadBytes(4)) != "DDS ")
                    throw new Exception("Invalid DDS magic");
                uint size = br.ReadUInt32();
                if (size != 124) throw new Exception("Invalid DDS header size");
                uint headerFlags = br.ReadUInt32();
                uint height = br.ReadUInt32();
                uint width = br.ReadUInt32();
                uint pitchOrLinear = br.ReadUInt32();
                br.ReadUInt32();
                uint mipmapCount = br.ReadUInt32();
                byte numMips = mipmapCount == 0 ? (byte)1 : (byte)Math.Min(mipmapCount, 255u);
                ms.Seek(44, SeekOrigin.Current);
                br.ReadUInt32();
                uint pfFlags = br.ReadUInt32();
                byte[] fourBytes = br.ReadBytes(4);
                string fourCC = Encoding.ASCII.GetString(fourBytes).Trim('\0', ' ').ToUpperInvariant();
                bool numericD3d = TryReadNumericD3dFormat(fourBytes, out uint d3dFormat);
                uint rgbBitCount = br.ReadUInt32();
                uint rMask = br.ReadUInt32();
                uint gMask = br.ReadUInt32();
                uint bMask = br.ReadUInt32();
                uint aMask = br.ReadUInt32();
                br.ReadUInt32();
                uint caps2 = br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt32();

                if (width == 0 || height == 0 || width > ushort.MaxValue || height > ushort.MaxValue)
                    throw new Exception("Invalid DDS dimensions");

                var desc = new DdsDesc
                {
                    Width = (ushort)width,
                    Height = (ushort)height,
                    NumMips = numMips,
                    HeaderSize = 128,
                    IsCubemap = (caps2 & 0x200u) != 0
                };

                if (fourCC == "DX10")
                {
                    if (ms.Position + 20 > ms.Length) throw new Exception("Truncated DX10 DDS header");
                    uint dxgi = br.ReadUInt32();
                    br.ReadUInt32();
                    uint misc = br.ReadUInt32();
                    br.ReadUInt32();
                    br.ReadUInt32();
                    if (dxgi > 255) throw new Exception($"Unsupported DXGI format {dxgi}");
                    if ((misc & 0x4u) != 0) desc.IsCubemap = true;
                    desc.Format = (byte)dxgi;
                    desc.HeaderSize = 148;
                    if (!TryGetDxgiLayout(desc.Format, out int bpp, out bool block))
                        throw new Exception($"Unsupported DXGI format {dxgi}");
                    desc.Block = block;
                    desc.SrcBpp = block ? 0 : bpp;
                    if (!block)
                        ApplyUncompressedPitch(ref desc, headerFlags, pitchOrLinear);
                }
                else if (numericD3d && d3dFormat == 113)
                {
                    desc.Format = 87;
                    desc.SrcBpp = 8;
                    desc.HalfFloatRgba = true;
                    ApplyUncompressedPitch(ref desc, headerFlags, pitchOrLinear);
                }
                else if (fourCC.Length > 0 && TryMapLegacyFourCC(fourCC, out byte legacy))
                {
                    desc.Format = legacy;
                    desc.Block = true;
                }
                else if (fourCC.Length > 0 && (pfFlags & 0x4u) != 0)
                {
                    if (numericD3d)
                        throw new Exception($"Unsupported DDS FourCC '{fourCC}' (D3DFMT {d3dFormat})");
                    throw new Exception($"Unsupported DDS FourCC '{fourCC}'");
                }
                else
                {
                    bool rgb = (pfFlags & 0x40u) != 0;
                    bool lum = (pfFlags & 0x20000u) != 0;
                    bool alpha = (pfFlags & 0x2u) != 0;
                    if (rgb && rgbBitCount == 32)
                    {
                        if (rMask == 0x00FF0000u && gMask == 0x0000FF00u && bMask == 0x000000FFu) desc.Format = 87;
                        else if (rMask == 0x000000FFu && gMask == 0x0000FF00u && bMask == 0x00FF0000u) desc.Format = 28;
                        else throw new Exception("Unsupported uncompressed DDS channel layout");
                        desc.SrcBpp = 4;
                    }
                    else if (rgb && rgbBitCount == 24)
                    {
                        if (rMask == 0 && gMask == 0 && bMask == 0)
                        {
                            rMask = 0x00FF0000u;
                            gMask = 0x0000FF00u;
                            bMask = 0x000000FFu;
                        }
                        desc.Format = 87;
                        desc.SrcBpp = 3;
                        desc.ExpandToBgra = true;
                        desc.RMask = rMask;
                        desc.GMask = gMask;
                        desc.BMask = bMask;
                    }
                    else if (rgb && rgbBitCount == 16)
                    {
                        if (rMask == 0 && gMask == 0 && bMask == 0)
                        {
                            rMask = 0xF800u;
                            gMask = 0x07E0u;
                            bMask = 0x001Fu;
                        }
                        desc.Format = 87;
                        desc.SrcBpp = 2;
                        desc.ExpandToBgra = true;
                        desc.HasAlpha = aMask != 0;
                        desc.RMask = rMask;
                        desc.GMask = gMask;
                        desc.BMask = bMask;
                        desc.AMask = aMask;
                    }
                    else if (rgbBitCount == 8 && (lum || alpha || (rgb && IsSingleChannel8(rMask, gMask, bMask))))
                    {
                        desc.Format = 61;
                        desc.SrcBpp = 1;
                    }
                    else
                    {
                        throw new Exception("Unsupported DDS format");
                    }

                    ApplyUncompressedPitch(ref desc, headerFlags, pitchOrLinear);
                }

                return desc;
            }
        }

        private static bool IsSingleChannel8(uint rMask, uint gMask, uint bMask)
        {
            if (rMask == 0 && gMask == 0 && bMask == 0) return true;
            int channels = (rMask == 0xFFu ? 1 : 0) + (gMask == 0xFFu ? 1 : 0) + (bMask == 0xFFu ? 1 : 0);
            return channels == 1 && rMask <= 0xFFu && gMask <= 0xFFu && bMask <= 0xFFu;
        }

        private static bool TryReadNumericD3dFormat(byte[] fourBytes, out uint format)
        {
            format = 0;
            if (fourBytes.Length < 4 || fourBytes[1] != 0 || fourBytes[2] != 0 || fourBytes[3] != 0 || fourBytes[0] == 0)
                return false;
            format = fourBytes[0];
            return true;
        }

        private static bool TryMapLegacyFourCC(string fourCC, out byte format)
        {
            switch (fourCC)
            {
                case "DXT1":
                case "BC1":
                case "BC1U": format = 71; return true;
                case "DXT2":
                case "DXT3":
                case "BC2": format = 74; return true;
                case "DXT4":
                case "DXT5":
                case "BC3": format = 77; return true;
                case "ATI1":
                case "BC4":
                case "BC4U": format = 80; return true;
                case "BC4S": format = 81; return true;
                case "ATI2":
                case "BC5":
                case "BC5U": format = 83; return true;
                case "BC5S": format = 84; return true;
                case "BC6H": format = 95; return true;
                case "BC7": format = 98; return true;
                default: format = 0; return false;
            }
        }

        private static PreparedDds MaterializeDds(byte[] data, DdsDesc desc)
        {
            int faces = desc.IsCubemap ? 6 : 1;
            int chain = GetMipRangeByteSize(desc.Width, desc.Height, desc.Format, 0, desc.NumMips - 1);
            long expected = (long)chain * faces;
            if (expected > int.MaxValue) throw new Exception("DDS mip is too large");
            bool padded = desc.Pitched && desc.Pitch != desc.Width * Math.Max(desc.SrcBpp, 1);
            bool rewrite = desc.ExpandToBgra || desc.HalfFloatRgba || padded;

            if (!rewrite)
            {
                long available = desc.HeaderSize > data.Length ? 0 : (long)data.Length - desc.HeaderSize;
                if (available < expected)
                    throw new Exception($"DDS pixel data shorter than mip chain: {available} < {expected}");
                return new PreparedDds
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    NumMips = desc.NumMips,
                    Format = desc.Format,
                    IsCubemap = desc.IsCubemap,
                    Data = data,
                    PixelOffset = desc.HeaderSize,
                    PixelLength = (int)expected
                };
            }

            byte[] pixels = new byte[(int)expected];
            int src = desc.HeaderSize;
            int dst = 0;
            for (int face = 0; face < faces; face++)
            {
                int cw = desc.Width;
                int ch = desc.Height;
                for (int mip = 0; mip < desc.NumMips; mip++)
                {
                    int stride = UncompressedRowStride(cw, desc.SrcBpp, desc.Pitched, desc.Width, desc.Pitch, mip);
                    int mipBytes = GetMipByteSize(cw, ch, desc.Format);
                    long need = (long)stride * ch;
                    long available = src > data.Length ? 0 : (long)data.Length - src;
                    if (stride < cw * desc.SrcBpp || need > available)
                        throw new Exception($"DDS pixel data shorter than mip chain: {Math.Max(0, data.Length - desc.HeaderSize)} < {expected}");

                    if (desc.HalfFloatRgba)
                        ExpandHalfFloatRowsToBgra(data, src, pixels, dst, cw, ch, stride);
                    else if (desc.ExpandToBgra)
                        ExpandRowsToBgra(data, src, pixels, dst, cw, ch, stride, desc);
                    else
                        CopyTightRows(data, src, pixels, dst, cw, ch, stride, desc.SrcBpp);

                    src += (int)need;
                    dst += mipBytes;
                    cw = Math.Max(1, cw / 2);
                    ch = Math.Max(1, ch / 2);
                }
            }

            if (dst != pixels.Length)
                throw new Exception("Internal error: DDS pixel size mismatch");

            return new PreparedDds
            {
                Width = desc.Width,
                Height = desc.Height,
                NumMips = desc.NumMips,
                Format = desc.Format,
                IsCubemap = desc.IsCubemap,
                Data = pixels,
                PixelOffset = 0,
                PixelLength = pixels.Length
            };
        }

        private static void ApplyUncompressedPitch(ref DdsDesc desc, uint headerFlags, uint pitchOrLinear)
        {
            desc.Pitched = (headerFlags & 0x8u) != 0;
            if (!desc.Pitched) return;
            int tight = desc.Width * desc.SrcBpp;
            if (pitchOrLinear < (uint)tight || pitchOrLinear > int.MaxValue)
                throw new Exception("Invalid DDS pitch");
            desc.Pitch = (int)pitchOrLinear;
        }

        private static int UncompressedRowStride(int mipWidth, int srcBpp, bool pitched, int topWidth, int topPitch, int mip)
        {
            int tight = mipWidth * srcBpp;
            if (!pitched) return tight;
            if (mip == 0) return topPitch;
            if (topPitch <= topWidth * srcBpp) return tight;
            return (tight + 3) & ~3;
        }

        private static void CopyTightRows(byte[] src, int srcOffset, byte[] dst, int dstOffset, int width, int height, int stride, int bpp)
        {
            int rowBytes = width * bpp;
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(src, srcOffset + y * stride, dst, dstOffset + y * rowBytes, rowBytes);
        }

        private static void ExpandHalfFloatRowsToBgra(byte[] src, int srcOffset, byte[] dst, int dstOffset, int width, int height, int stride)
        {
            for (int y = 0; y < height; y++)
            {
                int row = srcOffset + y * stride;
                int dstRow = dstOffset + y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int p = row + x * 8;
                    byte r = QuantizeHalf(src, p);
                    byte g = QuantizeHalf(src, p + 2);
                    byte b = QuantizeHalf(src, p + 4);
                    byte a = QuantizeHalf(src, p + 6);
                    int o = dstRow + x * 4;
                    dst[o] = b;
                    dst[o + 1] = g;
                    dst[o + 2] = r;
                    dst[o + 3] = a;
                }
            }
        }

        private static byte QuantizeHalf(byte[] src, int offset)
        {
            float value = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(src, offset));
            if (float.IsNaN(value)) return 0;
            int quantized = (int)MathF.Round(value * 255f);
            if (quantized < 0) return 0;
            if (quantized > 255) return 255;
            return (byte)quantized;
        }

        private static void ExpandRowsToBgra(byte[] src, int srcOffset, byte[] dst, int dstOffset, int width, int height, int stride, DdsDesc desc)
        {
            for (int y = 0; y < height; y++)
            {
                int row = srcOffset + y * stride;
                int dstRow = dstOffset + y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    uint pixel = ReadPackedPixel(src, row + x * desc.SrcBpp, desc.SrcBpp);
                    byte r = ExpandChannel(pixel, desc.RMask);
                    byte g = ExpandChannel(pixel, desc.GMask);
                    byte b = ExpandChannel(pixel, desc.BMask);
                    byte a = desc.HasAlpha ? ExpandChannel(pixel, desc.AMask) : (byte)255;
                    int o = dstRow + x * 4;
                    dst[o] = b;
                    dst[o + 1] = g;
                    dst[o + 2] = r;
                    dst[o + 3] = a;
                }
            }
        }

        private static uint ReadPackedPixel(byte[] data, int offset, int bpp)
        {
            uint value = 0;
            for (int i = 0; i < bpp; i++)
                value |= (uint)data[offset + i] << (8 * i);
            return value;
        }

        private static byte ExpandChannel(uint pixel, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            uint bits = mask;
            while ((bits & 1u) == 0) { bits >>= 1; shift++; }
            int count = 0;
            uint value = (pixel & mask) >> shift;
            while ((bits & 1u) != 0) { bits >>= 1; count++; }
            if (count >= 8) return (byte)(value >> (count - 8));
            uint max = (1u << count) - 1u;
            return (byte)((value * 255u + max / 2u) / max);
        }

        private static bool TryGetDxgiLayout(byte format, out int bytesPerBlock, out bool blockCompressed)
        {
            switch (format)
            {
                case 71: case 72: case 80: case 81:
                    bytesPerBlock = 8; blockCompressed = true; return true;
                case 74: case 75: case 77: case 78:
                case 83: case 84: case 95: case 96:
                case 98: case 99:
                    bytesPerBlock = 16; blockCompressed = true; return true;
                case 61:
                    bytesPerBlock = 1; blockCompressed = false; return true;
                case 28: case 29:
                case 87: case 91:
                    bytesPerBlock = 4; blockCompressed = false; return true;
                default:
                    bytesPerBlock = 0; blockCompressed = false; return false;
            }
        }

        private static int GetDxgiBytesPerBlock(byte format)
        {
            if (TryGetDxgiLayout(format, out int bytes, out _)) return bytes;
            throw new Exception($"Unsupported DXGI format {format}");
        }

        private static bool IsBlockCompressed(byte format) =>
            TryGetDxgiLayout(format, out _, out bool block) && block;

        private static int GetMipByteSize(int width, int height, byte format)
        {
            if (IsBlockCompressed(format))
            {
                int bw = Math.Max(1, (width + 3) / 4);
                int bh = Math.Max(1, (height + 3) / 4);
                return bw * bh * GetDxgiBytesPerBlock(format);
            }
            return Math.Max(1, width) * Math.Max(1, height) * GetDxgiBytesPerBlock(format);
        }

        private static List<(int StartMip, int EndMip)> PlanArchive2MipChunks(int width, int height, int numMips)
        {
            var chunks = new List<(int, int)>();
            int cw = width, ch = height;
            int i = 0;
            while (i < numMips)
            {
                int slotsLeft = ARCHIVE2_MAX_CHUNKS - chunks.Count;
                if (slotsLeft <= 1)
                {
                    chunks.Add((i, numMips - 1));
                    break;
                }
                if (Math.Max(cw, ch) >= ARCHIVE2_MIP_CHUNK_THRESHOLD)
                {
                    chunks.Add((i, i));
                    i++;
                    cw = Math.Max(1, cw / 2);
                    ch = Math.Max(1, ch / 2);
                }
                else
                {
                    chunks.Add((i, numMips - 1));
                    break;
                }
            }
            return chunks;
        }

        private static int GetMipRangeByteSize(int width, int height, byte format, int startMip, int endMip)
        {
            int total = 0;
            int cw = width, ch = height;
            for (int m = 0; m <= endMip; m++)
            {
                if (m >= startMip)
                    total += GetMipByteSize(cw, ch, format);
                cw = Math.Max(1, cw / 2);
                ch = Math.Max(1, ch / 2);
            }
            return total;
        }

        private static int GetMipRangeByteOffset(int width, int height, byte format, int startMip)
        {
            if (startMip <= 0) return 0;
            return GetMipRangeByteSize(width, height, format, 0, startMip - 1);
        }

        private static string[] FilterExistingFiles(string[] files, Action<string> logger)
        {
            var keep = new List<string>(files.Length);
            int skipped = 0;
            foreach (var f in files)
            {
                try
                {
                    if (!File.Exists(f))
                    {
                        skipped++;
                        logger?.Invoke($"[BA2] Skipping missing file: {f}");
                        continue;
                    }
                    keep.Add(f);
                }
                catch (Exception ex)
                {
                    skipped++;
                    logger?.Invoke($"[BA2] Skipping unreadable file '{f}': {ex.Message}");
                }
            }
            if (skipped > 0)
                logger?.Invoke($"[BA2] Skipped {skipped} missing/unreadable file(s); packing {keep.Count} file(s).");
            return keep.ToArray();
        }

        public static int Pack(string sourceDir, string targetBa2Path, Action<string> logger = null, string type = "GNRL", string compression = "Default")
        {
            if (IsArchive2Available)
            {
                try
                {
                    logger?.Invoke("[BA2] Archive2.exe found. Using official packer.");
                    return PackWithArchive2(sourceDir, targetBa2Path, logger, type, compression);
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[BA2] Archive2 failed ({ex.Message}); falling back to built-in packer (Archive2-compatible chunking).");
                    try { if (File.Exists(targetBa2Path)) File.Delete(targetBa2Path); } catch {  }
                }
            }
            else
            {
                logger?.Invoke("[BA2] Archive2.exe not found; using built-in packer.");
            }

            try
            {
                var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
                if (type == "GNRL")
                    files = files.Where(f => !f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToArray();
                else if (type == "DX10")
                    files = files.Where(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToArray();

                files = FilterExistingFiles(files, logger);
                uint numFiles = (uint)files.Length;

                if (numFiles == 0) throw new Exception("No files to pack.");

                if (type != "GNRL" && type != "DX10")
                    throw new Exception($"Unsupported archive type '{type}'.");

                bool compress = !string.Equals(compression, "None", StringComparison.OrdinalIgnoreCase);
                long rawTotal = 0, storedTotal = 0;

                var prepared = new List<(BA2FileRecord Rec, byte[]? PixelData, int PixelOffset)>();
                foreach (var file in files)
                {
                    string relPath = Path.GetRelativePath(sourceDir, file).Replace("\\", "/").ToLowerInvariant();
                    byte[] data = File.ReadAllBytes(file);

                    string fileName = Path.GetFileNameWithoutExtension(relPath);
                    string ext = Path.GetExtension(relPath).TrimStart('.').ToLowerInvariant();
                    string dir = Path.GetDirectoryName(relPath) ?? "";

                    var rec = new BA2FileRecord
                    {
                        Name = relPath,
                        Hash = GetHash(fileName),
                        Ext = Encoding.ASCII.GetBytes(ext.PadRight(4, '\0').Substring(0, 4)),
                        DirHash = GetDirHash(dir)
                    };

                    if (type == "GNRL")
                    {
                        rec.UnpackedSize = (uint)data.Length;
                        rec.Flags = BA2_GNRL_FLAGS;
                        rec.Align = BA2_ALIGN_MARKER;
                        prepared.Add((rec, data, 0));
                    }
                    else
                    {
                        if (data.Length == 0)
                            throw new Exception($"Empty DDS cannot be packed as DX10: {relPath}");

                        var tex = ReadDdsForPack(data, relPath);
                        var plan = tex.IsCubemap
                            ? new List<(int StartMip, int EndMip)> { (0, tex.NumMips - 1) }
                            : PlanArchive2MipChunks(tex.Width, tex.Height, tex.NumMips);

                        rec.Unk8 = 0;
                        rec.NumChunks = (byte)plan.Count;
                        rec.ChunkHdrLen = 24;
                        rec.Height = tex.Height;
                        rec.Width = tex.Width;
                        rec.NumMips = tex.NumMips;
                        rec.Format = tex.Format;
                        rec.IsCubemap = tex.IsCubemap ? (byte)1 : (byte)0;
                        rec.TileMode = BA2_TILE_MODE;

                        foreach (var (startMip, endMip) in plan)
                        {
                            int full = tex.IsCubemap
                                ? tex.PixelLength
                                : GetMipRangeByteSize(tex.Width, tex.Height, tex.Format, startMip, endMip);
                            rec.Chunks.Add(new BA2TextureChunk
                            {
                                FullSize = (uint)full,
                                StartMip = (ushort)startMip,
                                EndMip = (ushort)endMip,
                                Align = BA2_ALIGN_MARKER
                            });
                        }

                        prepared.Add((rec, tex.Data, tex.PixelOffset));
                    }
                }

                long indexSize = 0;
                if (type == "GNRL")
                    indexSize = prepared.Count * 36L;
                else
                    indexSize = prepared.Sum(p => 24L + 24L * p.Rec.Chunks.Count);

                using (var fs = new FileStream(targetBa2Path, FileMode.Create))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(Encoding.ASCII.GetBytes("BTDX"));
                    bw.Write((uint)1);
                    bw.Write(Encoding.ASCII.GetBytes(type));
                    bw.Write(numFiles);
                    bw.Write((ulong)0);

                    long recordsPos = fs.Position;
                    fs.SetLength(recordsPos + indexSize);
                    fs.Seek(recordsPos + indexSize, SeekOrigin.Begin);

                    var records = new List<BA2FileRecord>();
                    foreach (var (rec, payload, pixelOffset) in prepared)
                    {
                        if (type == "GNRL")
                        {
                            byte[] data = payload!;
                            rec.Offset = (ulong)fs.Position;
                            byte[] packed = TryZlibCompress(data, 0, data.Length, compress);
                            if (packed != null)
                            {
                                rec.PackedSize = (uint)packed.Length;
                                bw.Write(packed);
                            }
                            else
                            {
                                rec.PackedSize = 0;
                                bw.Write(data);
                            }
                            rawTotal += data.Length;
                            storedTotal += packed?.Length ?? data.Length;
                        }
                        else
                        {
                            byte[] data = payload!;
                            int width = rec.Width, height = rec.Height;
                            byte format = rec.Format;
                            foreach (var chunk in rec.Chunks)
                            {
                                int mipOff = GetMipRangeByteOffset(width, height, format, chunk.StartMip);
                                int full = (int)chunk.FullSize;
                                int srcOff = pixelOffset + mipOff;
                                if (srcOff + full > data.Length)
                                    throw new Exception($"Mip slice out of range for {rec.Name}");

                                chunk.Offset = (ulong)fs.Position;
                                byte[] packed = TryZlibCompress(data, srcOff, full, compress);
                                if (packed != null)
                                {
                                    chunk.PackSize = (uint)packed.Length;
                                    bw.Write(packed);
                                }
                                else
                                {
                                    chunk.PackSize = 0;
                                    bw.Write(data, srcOff, full);
                                }
                                rawTotal += full;
                                storedTotal += packed?.Length ?? full;
                            }
                        }
                        records.Add(rec);
                    }

                    long nameTableOffset = fs.Position;
                    foreach (var rec in records)
                    {
                        byte[] nameBytes = Encoding.ASCII.GetBytes(rec.Name);
                        bw.Write((ushort)nameBytes.Length);
                        bw.Write(nameBytes);
                    }

                    fs.Seek(16, SeekOrigin.Begin);
                    bw.Write((ulong)nameTableOffset);

                    fs.Seek(recordsPos, SeekOrigin.Begin);
                    foreach (var rec in records)
                    {
                        bw.Write(rec.Hash);
                        bw.Write(rec.Ext);
                        bw.Write(rec.DirHash);
                        if (type == "GNRL")
                        {
                            bw.Write(rec.Flags);
                            bw.Write(rec.Offset);
                            bw.Write(rec.PackedSize);
                            bw.Write(rec.UnpackedSize);
                            bw.Write(rec.Align);
                        }
                        else
                        {
                            bw.Write(rec.Unk8);
                            bw.Write(rec.NumChunks);
                            bw.Write(rec.ChunkHdrLen);
                            bw.Write(rec.Height);
                            bw.Write(rec.Width);
                            bw.Write(rec.NumMips);
                            bw.Write(rec.Format);
                            bw.Write(rec.IsCubemap);
                            bw.Write(rec.TileMode);
                            foreach (var chunk in rec.Chunks)
                            {
                                bw.Write(chunk.Offset);
                                bw.Write(chunk.PackSize);
                                bw.Write(chunk.FullSize);
                                bw.Write(chunk.StartMip);
                                bw.Write(chunk.EndMip);
                                bw.Write(chunk.Align);
                            }
                        }
                    }

                    if (fs.Position != recordsPos + indexSize)
                        throw new Exception("Internal error: BA2 index size mismatch.");
                }

                string ratio = rawTotal > 0 ? $"{(100.0 * storedTotal / rawTotal):0}%" : "n/a";
                logger?.Invoke($"[BA2] Built-in packer wrote {numFiles} file(s) to {Path.GetFileName(targetBa2Path)} ({type}, zlib {(compress ? "on" : "off")}, {storedTotal:N0}/{rawTotal:N0} bytes = {ratio}).");
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BA2] Packing error: {ex.Message}");
                throw;
            }

            return 0;
        }

        public static string Archive2Path
        {
            get
            {
                string subPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Archive2", "Archive2.exe");
                if (File.Exists(subPath)) return subPath;
                
                string toolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Archive2.exe");
                if (File.Exists(toolsPath)) return toolsPath;

                string rootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Archive2.exe");
                if (File.Exists(rootPath)) return rootPath;

                return "";
            }
        }

        public static bool IsArchive2Available => !string.IsNullOrEmpty(Archive2Path);

        private static int PackWithArchive2(string sourceDir, string targetBa2Path, Action<string> logger, string type, string compression)
        {
            try
            {
                string exePath = Archive2Path;
                string format = (type == "DX10") ? "DDS" : "General";
                string absSource = Path.GetFullPath(sourceDir);
                string absTarget = Path.GetFullPath(targetBa2Path);
                string exeDir = Path.GetDirectoryName(exePath) ?? absSource;

                var files = Directory.GetFiles(absSource, "*.*", SearchOption.AllDirectories);
                files = FilterExistingFiles(files, logger);

                if (files.Length == 0)
                    throw new Exception("No files to pack.");

                int expectedCount = files.Length;
                int largeTexCount = 0;
                if (type == "DX10")
                {
                    foreach (var f in files)
                    {
                        try
                        {
                            var hdr = ParseDDSHeader(File.ReadAllBytes(f));
                            if (Math.Max(hdr.Width, hdr.Height) >= ARCHIVE2_MIP_CHUNK_THRESHOLD)
                                largeTexCount++;
                        }
                        catch {  }
                    }
                }

                int zeroByteCount = files.Count(f =>
                {
                    try { return new FileInfo(f).Length == 0; }
                    catch { return false; }
                });
                if (zeroByteCount > 0)
                    logger?.Invoke($"[BA2] Including {zeroByteCount} 0-byte placeholder file(s) in Archive2 pack.");

                string responseFile = Path.Combine(Path.GetTempPath(), "Archive2List_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllLines(responseFile, files, Encoding.Default);

                var args =
                    $"-sourceFile=\"{responseFile}\" -create=\"{absTarget}\" -root=\"{absSource}\" " +
                    $"-format={format} -compression={compression} " +
                    $"-maxChunkCount={ARCHIVE2_MAX_CHUNKS} " +
                    $"-singleMipChunkX={ARCHIVE2_MIP_CHUNK_THRESHOLD} " +
                    $"-singleMipChunkY={ARCHIVE2_MIP_CHUNK_THRESHOLD}";

                logger?.Invoke($"[BA2] Running: {exePath} {args}");

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    WorkingDirectory = exeDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = new Process { StartInfo = psi })
                {
                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();

                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    const int timeoutMs = 10 * 60 * 1000;
                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch (Exception killEx) { logger?.Invoke($"[BA2] Failed to kill timed-out Archive2 process: {killEx.Message}"); }
                        logger?.Invoke("[BA2] Error: Archive2 timed out after 10 minutes.");
                        throw new Exception("Archive2 process timed out.");
                    }

                    string output = outputBuilder.ToString();
                    string error = errorBuilder.ToString();

                    try { File.Delete(responseFile); } catch (Exception deleteEx) { logger?.Invoke($"[BA2] Failed to delete Archive2 response file: {deleteEx.Message}"); }

                    if (proc.ExitCode != 0)
                    {
                        logger?.Invoke($"[BA2] Archive2 Error: {error}");
                        throw new Exception($"Archive2 failed with exit code {proc.ExitCode}: {error}");
                    }

                    logger?.Invoke($"[BA2] Archive2 Output: {output}");

                    if (!File.Exists(absTarget) || new FileInfo(absTarget).Length < 24)
                        throw new Exception("Archive2 reported success but produced no usable archive.");

                    try
                    {
                        using (var fs = new FileStream(absTarget, FileMode.Open, FileAccess.ReadWrite))
                        {
                            fs.Seek(4, SeekOrigin.Begin);
                            int version = fs.ReadByte();
                            if (version != 1)
                            {
                                fs.Seek(4, SeekOrigin.Begin);
                                fs.WriteByte(1);
                                logger?.Invoke($"[BA2] patched header version from {version} to 1 for compatibility.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[BA2] Warning: Failed to patch header version: {ex.Message}");
                    }

                    ValidatePackedArchive(absTarget, type, expectedCount, largeTexCount, logger);

                    logger?.Invoke($"[BA2] Packed successfully with Archive2 (Compression: {compression}).");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BA2] Archive2 execution failed: {ex.Message}");
                throw;
            }
        }

        private static void ValidatePackedArchive(string ba2Path, string type, int expectedCount, int largeTexCount, Action<string> logger)
        {
            var listed = ListEntries(ba2Path, logger);
            if (listed.Entries.Count != expectedCount)
                throw new Exception($"Archive validation failed: expected {expectedCount} entries, got {listed.Entries.Count}.");

            if (!string.Equals(type, "DX10", StringComparison.OrdinalIgnoreCase) || largeTexCount == 0)
                return;

            using var fs = new FileStream(ba2Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            var (archType, records) = ReadArchiveIndex(fs, br);
            if (!string.Equals(archType, "DX10", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Archive validation failed: expected DX10, got {archType}.");

            bool anyMulti = records.Any(r => r.NumChunks > 1);
            if (!anyMulti)
                throw new Exception("Archive validation failed: expected multi-mip chunks for textures >= 512px, but all textures are single-chunk.");

            logger?.Invoke($"[BA2] Validation OK: {listed.Entries.Count} entries, multi-chunk textures present.");
        }

    }
}
