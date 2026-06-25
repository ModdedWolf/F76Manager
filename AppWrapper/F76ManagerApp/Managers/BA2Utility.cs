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

            public byte NumChunks { get; set; }
            public byte TileMode { get; set; }
            public ushort ChunkHdrLen { get; set; }
            public ushort Height { get; set; }
            public ushort Width { get; set; }
            public byte NumMips { get; set; }
            public byte Format { get; set; }
            public ushort CubeMaps { get; set; }
            public List<BA2TextureChunk> Chunks { get; set; } = new List<BA2TextureChunk>();
        }

        public class BA2TextureChunk
        {
            public ulong Offset { get; set; }
            public uint PackSize { get; set; }
            public uint FullSize { get; set; }
            public uint Unknown { get; set; }
            public ushort StartMip { get; set; }
            public ushort EndMip { get; set; }
            public uint Align { get; set; }
        }

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
                    rec.NumChunks = br.ReadByte();
                    rec.TileMode = br.ReadByte();
                    rec.ChunkHdrLen = br.ReadUInt16();
                    rec.Height = br.ReadUInt16();
                    rec.Width = br.ReadUInt16();
                    rec.NumMips = br.ReadByte();
                    rec.Format = br.ReadByte();
                    rec.CubeMaps = br.ReadUInt16();
                }
                records.Add(rec);
            }

            if (type == "DX10")
            {
                foreach (var rec in records)
                {
                    for (int j = 0; j < rec.NumChunks; j++)
                    {
                        rec.Chunks.Add(new BA2TextureChunk
                        {
                            Offset = br.ReadUInt64(),
                            PackSize = br.ReadUInt32(),
                            FullSize = br.ReadUInt32(),
                            Unknown = br.ReadUInt32(),
                            StartMip = br.ReadUInt16(),
                            EndMip = br.ReadUInt16(),
                            Align = br.ReadUInt32()
                        });
                    }
                }
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
                                if (rec.UnpackedSize == 0) continue;
                                byte[] data = br.ReadBytes((int)rec.UnpackedSize);
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
                uint blockSize;
                switch (rec.Format)
                {
                    case 71: blockSize = 8u; break;
                    case 74: blockSize = 16u; break;
                    case 77: blockSize = 16u; break;
                    case 80: blockSize = 8u; break;
                    case 81: blockSize = 8u; break;
                    case 83: blockSize = 16u; break;
                    case 84: blockSize = 16u; break;
                    case 95: blockSize = 16u; break;
                    case 98: blockSize = 16u; break;
                    default: throw new Exception($"Unsupported DX10 texture format (DXGI {rec.Format}).");
                }
                uint blocksWide = Math.Max(1u, ((uint)rec.Width + 3u) / 4u);
                uint blocksHigh = Math.Max(1u, ((uint)rec.Height + 3u) / 4u);
                uint linearSize = blocksWide * blocksHigh * blockSize;
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

        private static uint GetHash(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            uint hash = 0;
            foreach (char c in name.ToLowerInvariant())
            {
                hash = (hash * 0x1003F) + (uint)c;
            }
            return hash;
        }

        private static (ushort Width, ushort Height, byte NumMips, byte Format, int HeaderSize) ParseDDSHeader(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "DDS ") throw new Exception("Invalid DDS magic");
                uint size = br.ReadUInt32();
                if (size != 124) throw new Exception("Invalid DDS header size");
                uint flags = br.ReadUInt32();
                uint _height = br.ReadUInt32();
                uint _width = br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt32();
                uint _mipmapCount = br.ReadUInt32();
                byte numMips = (byte)_mipmapCount;
                if (numMips == 0) numMips = 1;
                ms.Seek(44, SeekOrigin.Current);
                br.ReadUInt32();
                br.ReadUInt32();
                string fourCC = Encoding.ASCII.GetString(br.ReadBytes(4));
                ms.Seek(40, SeekOrigin.Current);
                int headerSize = 128;
                byte format = 0;
                if (fourCC == "DX10")
                {
                    format = (byte)br.ReadUInt32();
                    br.ReadUInt32();
                    br.ReadUInt32();
                    br.ReadUInt32();
                    br.ReadUInt32();
                    headerSize = 148;
                }
                else
                {
                    switch (fourCC)
                    {
                        case "DXT1": format = 71; break;
                        case "DXT3": format = 74; break;
                        case "DXT5": format = 77; break;
                        case "BC4U": format = 80; break;
                        case "BC4S": format = 81; break;
                        case "BC5U": format = 83; break;
                        case "BC5S": format = 84; break;
                        case "BC6H": format = 95; break;
                        case "BC7 ": format = 98; break;
                        default: throw new Exception("Unsupported DDS format");
                    }
                }
                return ((ushort)_width, (ushort)_height, numMips, format, headerSize);
            }
        }

        public static void Pack(string sourceDir, string targetBa2Path, Action<string> logger = null, string type = "GNRL", string compression = "Default")
        {
            if (IsArchive2Available)
            {
                logger?.Invoke("[BA2] Archive2.exe found. Using official packer.");
                PackWithArchive2(sourceDir, targetBa2Path, logger, type, compression);
                return;
            }

            try
            {
                var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
                if (type == "GNRL")
                    files = files.Where(f => !f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToArray();
                else if (type == "DX10")
                    files = files.Where(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToArray();
                uint numFiles = (uint)files.Length;

                if (numFiles == 0) throw new Exception("No files to pack.");

                using (var fs = new FileStream(targetBa2Path, FileMode.Create))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(Encoding.ASCII.GetBytes("BTDX"));
                    bw.Write((uint)1);
                    bw.Write(Encoding.ASCII.GetBytes(type.PadRight(4, ' ').Substring(0, 4))); 
                    bw.Write(numFiles);
                    bw.Write((ulong)0);

                    long recordsPos = fs.Position;

                    int recordSize = type == "GNRL" ? 36 : 24;
                    for (int i = 0; i < numFiles ; i++)
                        bw.Write(new byte[recordSize]);

                    long chunkHeadersPos = 0;
                    if (type == "DX10")
                    {
                        chunkHeadersPos = fs.Position;
                        int chunkHdrSize = 24;
                        fs.Seek(numFiles * chunkHdrSize, SeekOrigin.Current);
                    }

                    var records = new List<BA2FileRecord>();
                    long tempDataPos = fs.Position;
                    foreach (var file in files)
                    {
                        string relPath = Path.GetRelativePath(sourceDir, file).Replace("\\", "/").ToLowerInvariant();
                        byte[] data = File.ReadAllBytes(file);

                        string fileName = Path.GetFileNameWithoutExtension(relPath);
                        string ext = Path.GetExtension(relPath).TrimStart('.').ToLower();
                        string dir = Path.GetDirectoryName(relPath) ?? "";

                        var rec = new BA2FileRecord
                        {
                            Name = relPath,
                            Hash = GetHash(fileName),
                            Ext = Encoding.ASCII.GetBytes(ext.PadRight(4, '\0').Substring(0, 4)),
                            DirHash = GetHash(dir)
                        };

                        if (type == "GNRL")
                        {
                            rec.Offset = (ulong)tempDataPos;
                            rec.UnpackedSize = (uint)data.Length;
                            rec.PackedSize = 0;
                            rec.Flags = 0;
                            rec.Align = 0;
                            tempDataPos += data.Length;
                        }
                        else if (type == "DX10")
                        {
                            var (width, height, numMips, format, headerSize) = ParseDDSHeader(data);
                            byte[] pixelData = data.Skip(headerSize).ToArray();
                            rec.NumChunks = 1;
                            rec.TileMode = 0;
                            rec.ChunkHdrLen = 24;
                            rec.Height = height;
                            rec.Width = width;
                            rec.NumMips = numMips;
                            rec.Format = format;
                            rec.CubeMaps = 0;

                            var chunk = new BA2TextureChunk
                            {
                                Offset = (ulong)tempDataPos,
                                PackSize = 0,
                                FullSize = (uint)pixelData.Length,
                                Unknown = 0,
                                StartMip = 0,
                                EndMip = (ushort)(numMips - 1),
                                Align = 0
                            };
                            rec.Chunks.Add(chunk);
                            tempDataPos += pixelData.Length;
                        }
                        records.Add(rec);
                    }

                    records = records.OrderBy(r => r.DirHash)
                                     .ThenBy(r => r.Hash)
                                     .ThenBy(r => BitConverter.ToUInt32(r.Ext, 0))
                                     .ToList();

                    long actualDataPos = fs.Position;
                    foreach (var rec in records)
                    {
                        string filePath = Path.Combine(sourceDir, rec.Name.Replace("/", "\\"));
                        byte[] data = File.ReadAllBytes(filePath);

                        if (type == "DX10")
                        {
                            var (width, height, numMips, format, headerSize) = ParseDDSHeader(data);
                            data = data.Skip(headerSize).ToArray();
                            rec.Chunks[0].Offset = (ulong)actualDataPos;
                        }
                        else
                        {
                            rec.Offset = (ulong)actualDataPos;
                        }

                        bw.Write(data);
                        actualDataPos += data.Length;
                    }

                    long nameTableOffset = fs.Position;
                    foreach (var rec in records)
                    {
                        byte[] nameBytes = Encoding.UTF8.GetBytes(rec.Name);
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
                        else if (type == "DX10")
                        {
                            bw.Write(rec.NumChunks);
                            bw.Write(rec.TileMode);
                            bw.Write(rec.ChunkHdrLen);
                            bw.Write(rec.Height);
                            bw.Write(rec.Width);
                            bw.Write(rec.NumMips);
                            bw.Write(rec.Format);
                            bw.Write(rec.CubeMaps);
                        }
                    }

                    if (type == "DX10")
                    {
                        fs.Seek(chunkHeadersPos, SeekOrigin.Begin);
                        foreach (var rec in records)
                        {
                            foreach (var chunk in rec.Chunks)
                            {
                                bw.Write(chunk.Offset);
                                bw.Write(chunk.PackSize);
                                bw.Write(chunk.FullSize);
                                bw.Write(chunk.Unknown);
                                bw.Write(chunk.StartMip);
                                bw.Write(chunk.EndMip);
                                bw.Write(chunk.Align);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BA2] Packing error: {ex.Message}");
                throw;
            }
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

        private static void PackWithArchive2(string sourceDir, string targetBa2Path, Action<string> logger, string type, string compression)
        {
            try
            {
                string exePath = Archive2Path;
                string format = (type == "DX10") ? "DDS" : "General";
                
                string absSource = Path.GetFullPath(sourceDir);
                string absTarget = Path.GetFullPath(targetBa2Path);

                var files = Directory.GetFiles(absSource, "*.*", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    logger?.Invoke($"[BA2] Warning: Source directory {absSource} is empty. Archive2 will likely fail.");
                }

                
                string responseFile = Path.Combine(Path.GetTempPath(), "Archive2List_" + Guid.NewGuid().ToString("N") + ".txt");
                var lines = new List<string>();
                foreach (var f in files)
                {
                    lines.Add(f);
                }
                File.WriteAllLines(responseFile, lines, Encoding.Default);

                var args = $"-sourcefile=\"{responseFile}\" -create=\"{absTarget}\" -root=\"{absSource}\" -format={format} -compression={compression}";

                logger?.Invoke($"[BA2] Running: {exePath} {args}");

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
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

                    if (!proc.WaitForExit(60000))
                    {
                        try { proc.Kill(); } catch (Exception killEx) { logger?.Invoke($"[BA2] Failed to kill timed-out Archive2 process: {killEx.Message}"); }
                        logger?.Invoke("[BA2] Error: Archive2 timed out after 60 seconds.");
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

                    logger?.Invoke($"[BA2] Packed successfully with Archive2 (Compression: Default).");
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BA2] Archive2 execution failed: {ex.Message}");
                throw;
            }
        }

    }
}