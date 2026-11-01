using System;
using System.Collections.Generic;
using System.Text;

namespace F76ManagerApp.Managers
{
    public static class NifMeshReader
    {
        public const int MaxVertices = 500000;

        public sealed class Shape
        {
            public string Name { get; init; } = "";
            public string Material { get; init; } = "";
            public float[] Positions { get; init; }
            public float[] Normals { get; init; }
            public float[] Uvs { get; init; }
            public uint[] Indices { get; init; }
            public ushort[] Joints { get; init; }
            public float[] Weights { get; init; }
            public List<string> SkinBones { get; init; } = new List<string>();
            public int Texture { get; set; } = -1;
            public string TextureStatus { get; set; } = "ok";
            public string TexturePath { get; set; } = "";
            public string TextureFormat { get; set; } = "";
        }

        public sealed class Bone
        {
            public string Name { get; init; } = "";
            public int Parent { get; init; } = -1;
            public float X { get; init; }
            public float Y { get; init; }
            public float Z { get; init; }
        }

        public sealed class LocalBone
        {
            public string Name { get; init; } = "";
            public int Parent { get; init; } = -1;
            public float Tx { get; init; }
            public float Ty { get; init; }
            public float Tz { get; init; }
            public float Rx { get; init; }
            public float Ry { get; init; }
            public float Rz { get; init; }
            public float Rw { get; init; } = 1f;
            public float Sx { get; init; } = 1f;
            public float Sy { get; init; } = 1f;
            public float Sz { get; init; } = 1f;
        }

        public sealed class SkeletonResult
        {
            public bool Ok { get; init; }
            public string Error { get; init; }
            public List<LocalBone> Bones { get; init; } = new List<LocalBone>();
        }

        public sealed class Result
        {
            public bool Ok { get; init; }
            public bool TooLarge { get; init; }
            public string Error { get; init; }
            public List<Shape> Shapes { get; init; } = new List<Shape>();
            public List<Bone> Bones { get; init; } = new List<Bone>();
            public List<byte[]> TexturePngs { get; init; } = new List<byte[]>();
            public float MinX { get; init; }
            public float MinY { get; init; }
            public float MinZ { get; init; }
            public float MaxX { get; init; }
            public float MaxY { get; init; }
            public float MaxZ { get; init; }
            public int VertexCount { get; init; }
            public int TriangleCount { get; init; }
            public int Skipped { get; init; }
        }

        private sealed class Xform
        {
            public float Tx, Ty, Tz, Scale = 1f;
            public float[] Rot = Identity();
            public List<int> Children = new List<int>();
            public string Name = "";
            public bool IsNode;
        }

        public static Result Read(byte[] file, string contextArchiveOrFolder, Action<string> logger = null)
        {
            try
            {
                return ReadCore(file, contextArchiveOrFolder, logger, skinnedBindPose: false);
            }
            catch (Exception ex)
            {
                return new Result { Error = ex.Message };
            }
        }

        public static Result ReadSkinned(byte[] file, string contextArchiveOrFolder, Action<string> logger = null)
        {
            try
            {
                return ReadCore(file, contextArchiveOrFolder, logger, skinnedBindPose: true);
            }
            catch (Exception ex)
            {
                return new Result { Error = ex.Message };
            }
        }

        public static List<string> PeekSkinBoneNames(byte[] file, Action<string> logger = null)
        {
            try
            {
                if (!TryParseBlocks(file, out uint bsVersion, out int blockCount, out string[] types,
                    out int[] blockTypeIndex, out int[] blockSizes, out int[] blockOffset, out string[] strings))
                    return new List<string>();

                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < blockCount; i++)
                {
                    string type = blockTypeIndex[i] < types.Length ? types[blockTypeIndex[i]] : "";
                    if (type != "BSSkin::Instance") continue;
                    if (!TryReadSkinBones(file, blockOffset[i], blockOffset[i] + blockSizes[i],
                        blockOffset, blockSizes, blockCount, strings, out List<string> bones))
                        continue;
                    foreach (string bone in bones)
                    {
                        if (string.IsNullOrEmpty(bone) || !seen.Add(bone)) continue;
                        names.Add(bone);
                    }
                }
                return names;
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BUNDLE] NIF skin peek failed: {ex.Message}");
                return new List<string>();
            }
        }

        public static SkeletonResult ReadSkeletonLocal(byte[] file, Action<string> logger = null)
        {
            try
            {
                return ReadSkeletonLocalCore(file);
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BUNDLE] NIF skeleton read failed: {ex.Message}");
                return new SkeletonResult { Error = ex.Message };
            }
        }

        private static SkeletonResult ReadSkeletonLocalCore(byte[] file)
        {
            if (file == null || file.Length < 64) return new SkeletonResult { Error = "Not a NIF." };
            var reader = new BinaryCursor(file);
            string header = reader.ReadLine();
            if (!header.StartsWith("Gamebryo File Format", StringComparison.Ordinal))
                return new SkeletonResult { Error = "Not a NIF." };

            reader.ReadUInt32();
            byte endian = reader.ReadByte();
            if (endian != 1) return new SkeletonResult { Error = "Big-endian NIF files are not supported." };
            reader.ReadUInt32();
            uint blockCount = reader.ReadUInt32();
            uint bsVersion = reader.ReadUInt32();
            if (blockCount == 0 || blockCount > 200000) return new SkeletonResult { Error = "Unexpected NIF block count." };

            ReadExportString(reader);
            if (bsVersion > 130) reader.ReadUInt32();
            if (bsVersion < 131) ReadExportString(reader);
            ReadExportString(reader);
            if (bsVersion >= 103) ReadExportString(reader);

            ushort typeCount = reader.ReadUInt16();
            var types = new string[typeCount];
            for (int i = 0; i < typeCount; i++)
                types[i] = reader.ReadSizedString();

            var blockTypeIndex = new int[blockCount];
            for (int i = 0; i < blockCount; i++)
                blockTypeIndex[i] = reader.ReadUInt16() & 0x7FFF;

            var blockSizes = new int[blockCount];
            for (int i = 0; i < blockCount; i++)
                blockSizes[i] = (int)reader.ReadUInt32();

            uint stringCount = reader.ReadUInt32();
            reader.ReadUInt32();
            var strings = new string[stringCount];
            for (int i = 0; i < stringCount; i++)
                strings[i] = reader.ReadSizedString();

            uint groupCount = reader.ReadUInt32();
            for (int i = 0; i < groupCount; i++) reader.ReadUInt32();

            int blockStart = reader.Position;
            var blockOffset = new int[blockCount];
            int cursor = blockStart;
            for (int i = 0; i < blockCount; i++)
            {
                blockOffset[i] = cursor;
                cursor += blockSizes[i];
            }

            var xforms = new Xform[blockCount];
            for (int i = 0; i < blockCount; i++)
            {
                string type = blockTypeIndex[i] >= 0 && blockTypeIndex[i] < types.Length ? types[blockTypeIndex[i]] : "";
                if (type != "NiNode" && type != "BSFadeNode" && type != "BSLeafAnimNode") continue;
                var block = new BinaryCursor(file) { Position = blockOffset[i] };
                int blockEnd = blockOffset[i] + blockSizes[i];
                if (TryReadNode(block, blockEnd, bsVersion, strings, out Xform node))
                    xforms[i] = node;
            }

            var parent = new int[blockCount];
            for (int i = 0; i < blockCount; i++) parent[i] = -1;
            for (int i = 0; i < blockCount; i++)
            {
                if (xforms[i] == null) continue;
                foreach (int child in xforms[i].Children)
                {
                    if (child >= 0 && child < blockCount && parent[child] < 0)
                        parent[child] = i;
                }
            }

            var blocks = new List<int>();
            for (int i = 0; i < blockCount; i++)
            {
                if (xforms[i] != null && xforms[i].IsNode) blocks.Add(i);
            }
            if (blocks.Count == 0) return new SkeletonResult { Error = "No skeleton nodes." };

            var blockToBone = new Dictionary<int, int>();
            for (int i = 0; i < blocks.Count; i++) blockToBone[blocks[i]] = i;

            var bones = new List<LocalBone>(blocks.Count);
            foreach (int block in blocks)
            {
                Xform xf = xforms[block];
                float[] quat = MatrixToQuat(xf.Rot);
                int parentBlock = parent[block];
                int parentBone = parentBlock >= 0 && blockToBone.TryGetValue(parentBlock, out int mapped) ? mapped : -1;
                bones.Add(new LocalBone
                {
                    Name = xf.Name,
                    Parent = parentBone,
                    Tx = xf.Tx, Ty = xf.Ty, Tz = xf.Tz,
                    Rx = quat[0], Ry = quat[1], Rz = quat[2], Rw = quat[3],
                    Sx = xf.Scale, Sy = xf.Scale, Sz = xf.Scale
                });
            }
            return new SkeletonResult { Ok = true, Bones = bones };
        }

        private static float[] MatrixToQuat(float[] m)
        {
            if (m == null || m.Length < 9) return new float[] { 0, 0, 0, 1 };
            float m00 = m[0], m01 = m[1], m02 = m[2];
            float m10 = m[3], m11 = m[4], m12 = m[5];
            float m20 = m[6], m21 = m[7], m22 = m[8];
            float trace = m00 + m11 + m22;
            float x, y, z, w;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                w = 0.25f * s;
                x = (m21 - m12) / s;
                y = (m02 - m20) / s;
                z = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                w = (m21 - m12) / s;
                x = 0.25f * s;
                y = (m01 + m10) / s;
                z = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                w = (m02 - m20) / s;
                x = (m01 + m10) / s;
                y = 0.25f * s;
                z = (m12 + m21) / s;
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                w = (m10 - m01) / s;
                x = (m02 + m20) / s;
                y = (m12 + m21) / s;
                z = 0.25f * s;
            }
            float mag = MathF.Sqrt(x * x + y * y + z * z + w * w);
            if (mag < 1e-10f) return new float[] { 0, 0, 0, 1 };
            return new float[] { x / mag, y / mag, z / mag, w / mag };
        }

        private static Result ReadCore(byte[] file, string contextArchiveOrFolder, Action<string> logger, bool skinnedBindPose)
        {
            if (!TryParseBlocks(file, out uint bsVersion, out int blockCount, out string[] types,
                out int[] blockTypeIndex, out int[] blockSizes, out int[] blockOffset, out string[] strings))
                return new Result { Error = "Not a NIF." };

            var xforms = new Xform[blockCount];
            var shapes = new List<ShapeBuilder>();
            int skipped = 0;

            for (int i = 0; i < blockCount; i++)
            {
                string type = blockTypeIndex[i] >= 0 && blockTypeIndex[i] < types.Length ? types[blockTypeIndex[i]] : "";
                var block = new BinaryCursor(file) { Position = blockOffset[i] };
                int blockEnd = blockOffset[i] + blockSizes[i];
                if (type == "NiNode" || type == "BSFadeNode" || type == "BSLeafAnimNode")
                {
                    if (TryReadNode(block, blockEnd, bsVersion, strings, out Xform node))
                        xforms[i] = node;
                }
                else if (IsGeometry(type))
                {
                    if (TryReadShape(block, blockEnd, bsVersion, strings, type, out ShapeBuilder shape))
                    {
                        shape.Block = i;
                        if (shape.SkinRef >= 0 && shape.SkinRef < blockCount)
                        {
                            TryReadSkinBones(file, blockOffset[shape.SkinRef],
                                blockOffset[shape.SkinRef] + blockSizes[shape.SkinRef],
                                blockOffset, blockSizes, blockCount, strings, out shape.SkinBones);
                        }
                        shapes.Add(shape);
                        xforms[i] = new Xform { Tx = shape.Tx, Ty = shape.Ty, Tz = shape.Tz, Scale = shape.Scale, Rot = shape.Rot };
                    }
                    else skipped++;
                }
            }

            var parent = new int[blockCount];
            for (int i = 0; i < blockCount; i++) parent[i] = -1;
            for (int i = 0; i < blockCount; i++)
            {
                if (xforms[i] == null) continue;
                foreach (int child in xforms[i].Children)
                {
                    if (child >= 0 && child < blockCount && parent[child] < 0)
                        parent[child] = i;
                }
            }

            var textureIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var pngs = new List<byte[]>();
            var built = new List<Shape>();
            int vertexTotal = 0;
            int triangleTotal = 0;
            float minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            bool hasBounds = false;

            foreach (ShapeBuilder shape in shapes)
            {
                bool shapeSkinned = shape.Joints != null && shape.Weights != null && shape.SkinBones.Count > 0;
                if (skinnedBindPose && !shapeSkinned) continue;
                if (vertexTotal + shape.VertexCount > MaxVertices)
                    return new Result { TooLarge = true, Error = "mesh_too_large" };

                var positions = new float[shape.VertexCount * 3];
                for (int v = 0; v < shape.VertexCount; v++)
                {
                    float x = shape.Positions[v * 3];
                    float y = shape.Positions[v * 3 + 1];
                    float z = shape.Positions[v * 3 + 2];
                    if (!skinnedBindPose || !shapeSkinned)
                        ApplyChain(shape.Block, xforms, parent, ref x, ref y, ref z);
                    positions[v * 3] = x;
                    positions[v * 3 + 1] = y;
                    positions[v * 3 + 2] = z;
                    if (!hasBounds)
                    {
                        minX = maxX = x; minY = maxY = y; minZ = maxZ = z;
                        hasBounds = true;
                    }
                    else
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                        if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                    }
                }

                TexHit texture = ResolveShapeTexture(
                    file, shape, blockOffset, blockSizes, blockTypeIndex, types, blockCount, strings,
                    contextArchiveOrFolder, textureIndex, pngs, logger);
                if (texture.Index < 0)
                    logger?.Invoke($"[BUNDLE] {shape.Name}: {texture.Status} {texture.Path} {texture.Format}".Trim());

                built.Add(new Shape
                {
                    Name = shape.Name,
                    Material = texture.Material,
                    Positions = positions,
                    Normals = shape.Normals,
                    Uvs = shape.Uvs,
                    Indices = shape.Indices,
                    Joints = shapeSkinned ? shape.Joints : null,
                    Weights = shapeSkinned ? shape.Weights : null,
                    SkinBones = shapeSkinned ? shape.SkinBones : new List<string>(),
                    Texture = texture.Index,
                    TextureStatus = texture.Index >= 0 ? "ok" : texture.Status,
                    TexturePath = texture.Path ?? "",
                    TextureFormat = texture.Format ?? ""
                });
                vertexTotal += shape.VertexCount;
                triangleTotal += shape.Indices.Length / 3;
            }

            if (built.Count == 0)
            {
                if (skinnedBindPose)
                    return new Result { Error = skipped > 0 ? "No skinned shapes." : "No skinned geometry.", Skipped = skipped };
                List<Bone> bones = BuildBones(xforms, parent, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ, ref hasBounds);
                if (bones.Count > 0)
                {
                    return new Result
                    {
                        Ok = true,
                        Bones = bones,
                        MinX = minX, MinY = minY, MinZ = minZ,
                        MaxX = maxX, MaxY = maxY, MaxZ = maxZ,
                        Skipped = skipped
                    };
                }
                return new Result { Error = skipped > 0 ? "No drawable shapes." : "No geometry.", Skipped = skipped };
            }

            return new Result
            {
                Ok = true,
                Shapes = built,
                TexturePngs = pngs,
                MinX = minX, MinY = minY, MinZ = minZ,
                MaxX = maxX, MaxY = maxY, MaxZ = maxZ,
                VertexCount = vertexTotal,
                TriangleCount = triangleTotal,
                Skipped = skipped
            };
        }

        private static bool TryParseBlocks(byte[] file, out uint bsVersion, out int blockCount, out string[] types,
            out int[] blockTypeIndex, out int[] blockSizes, out int[] blockOffset, out string[] strings)
        {
            bsVersion = 0; blockCount = 0; types = null; blockTypeIndex = null;
            blockSizes = null; blockOffset = null; strings = null;
            if (file == null || file.Length < 64) return false;
            var reader = new BinaryCursor(file);
            string header = reader.ReadLine();
            if (!header.StartsWith("Gamebryo File Format", StringComparison.Ordinal))
                return false;

            reader.ReadUInt32();
            byte endian = reader.ReadByte();
            if (endian != 1) return false;
            reader.ReadUInt32();
            uint count = reader.ReadUInt32();
            bsVersion = reader.ReadUInt32();
            if (count == 0 || count > 200000) return false;
            blockCount = (int)count;

            ReadExportString(reader);
            if (bsVersion > 130) reader.ReadUInt32();
            if (bsVersion < 131) ReadExportString(reader);
            ReadExportString(reader);
            if (bsVersion >= 103) ReadExportString(reader);

            ushort typeCount = reader.ReadUInt16();
            types = new string[typeCount];
            for (int i = 0; i < typeCount; i++)
                types[i] = reader.ReadSizedString();

            blockTypeIndex = new int[blockCount];
            for (int i = 0; i < blockCount; i++)
                blockTypeIndex[i] = reader.ReadUInt16() & 0x7FFF;

            blockSizes = new int[blockCount];
            for (int i = 0; i < blockCount; i++)
                blockSizes[i] = (int)reader.ReadUInt32();

            uint stringCount = reader.ReadUInt32();
            reader.ReadUInt32();
            strings = new string[stringCount];
            for (int i = 0; i < stringCount; i++)
                strings[i] = reader.ReadSizedString();

            uint groupCount = reader.ReadUInt32();
            for (int i = 0; i < groupCount; i++) reader.ReadUInt32();

            int blockStart = reader.Position;
            blockOffset = new int[blockCount];
            int cursor = blockStart;
            for (int i = 0; i < blockCount; i++)
            {
                blockOffset[i] = cursor;
                cursor += blockSizes[i];
            }
            return true;
        }

        private sealed class ShapeBuilder
        {
            public int Block;
            public string Name = "";
            public int ShaderRef = -1;
            public int SkinRef = -1;
            public int VertexCount;
            public float[] Positions;
            public float[] Normals;
            public float[] Uvs;
            public uint[] Indices;
            public ushort[] Joints;
            public float[] Weights;
            public List<string> SkinBones = new List<string>();
            public float Tx, Ty, Tz, Scale = 1f;
            public float[] Rot = Identity();
        }

        private static bool IsGeometry(string type)
        {
            return type == "BSTriShape" || type == "BSSubIndexTriShape" ||
                   type == "BSMeshLODTriShape" || type == "BSDynamicTriShape";
        }

        private static bool TryReadNode(BinaryCursor block, int blockEnd, uint bsVersion, string[] strings, out Xform node)
        {
            node = null;
            if (!TryReadAvObject(block, blockEnd, bsVersion, out float tx, out float ty, out float tz, out float scale, out float[] rot, out int nameIndex))
                return false;
            if (block.Position + 4 > blockEnd) return false;
            uint count = block.ReadUInt32();
            if (count > 100000 || block.Position + count * 4 > blockEnd) return false;
            node = new Xform { Tx = tx, Ty = ty, Tz = tz, Scale = scale, Rot = rot, Name = NameAt(strings, nameIndex), IsNode = true };
            for (int i = 0; i < count; i++)
            {
                int child = block.ReadInt32();
                if (child >= 0) node.Children.Add(child);
            }
            return true;
        }

        private static bool TryReadShape(BinaryCursor block, int blockEnd, uint bsVersion, string[] strings, string type, out ShapeBuilder shape)
        {
            shape = null;
            if (!TryReadAvObject(block, blockEnd, bsVersion, out float tx, out float ty, out float tz, out float scale, out float[] rot, out int nameIndex))
                return false;
            if (block.Position + 16 > blockEnd) return false;
            block.ReadSingle(); block.ReadSingle(); block.ReadSingle(); block.ReadSingle();
            if (bsVersion >= 155)
            {
                if (block.Position + 24 > blockEnd) return false;
                for (int i = 0; i < 6; i++) block.ReadSingle();
            }
            if (block.Position + 20 > blockEnd) return false;
            int skinRef = block.ReadInt32();
            int shader = block.ReadInt32();
            block.ReadInt32();
            ulong desc = block.ReadUInt64();
            uint triangles = block.ReadUInt32();
            int vertices = block.ReadUInt16();
            block.ReadUInt32();
            if (vertices <= 0 || triangles > 5_000_000) return false;

            int stride = (int)((desc & 0xF) * 4);
            if (stride < 6 || stride > 128) return false;
            int uvOff = (int)(((desc >> 8) & 0xF) * 4);
            int nrmOff = (int)(((desc >> 16) & 0xF) * 4);
            int skinOff = (int)(((desc >> 28) & 0xF) * 4);
            ulong flags = desc >> 44;
            bool fullPrec = (flags & 0x400) != 0;
            bool hasUv = (flags & 0x2) != 0 && uvOff + 4 <= stride;
            bool hasNrm = (flags & 0x8) != 0 && nrmOff + 3 <= stride;
            bool hasSkin = (flags & 0x40) != 0 && skinOff + 12 <= stride;
            int posBytes = fullPrec ? 12 : 6;
            if (posBytes > stride) return false;
            if (block.Position + (long)vertices * stride + (long)triangles * 6 > blockEnd) return false;

            var positions = new float[vertices * 3];
            float[] normals = hasNrm ? new float[vertices * 3] : null;
            float[] uvs = hasUv ? new float[vertices * 2] : null;
            ushort[] joints = hasSkin ? new ushort[vertices * 4] : null;
            float[] weights = hasSkin ? new float[vertices * 4] : null;
            for (int v = 0; v < vertices; v++)
            {
                int start = block.Position;
                if (fullPrec)
                {
                    positions[v * 3] = block.ReadSingle();
                    positions[v * 3 + 1] = block.ReadSingle();
                    positions[v * 3 + 2] = block.ReadSingle();
                }
                else
                {
                    positions[v * 3] = HalfToFloat(block.ReadUInt16());
                    positions[v * 3 + 1] = HalfToFloat(block.ReadUInt16());
                    positions[v * 3 + 2] = HalfToFloat(block.ReadUInt16());
                }
                if (hasUv)
                {
                    block.Position = start + uvOff;
                    uvs[v * 2] = HalfToFloat(block.ReadUInt16());
                    uvs[v * 2 + 1] = HalfToFloat(block.ReadUInt16());
                }
                if (hasNrm)
                {
                    block.Position = start + nrmOff;
                    normals[v * 3] = block.ReadByte() / 255f * 2f - 1f;
                    normals[v * 3 + 1] = block.ReadByte() / 255f * 2f - 1f;
                    normals[v * 3 + 2] = block.ReadByte() / 255f * 2f - 1f;
                }
                if (hasSkin)
                {
                    block.Position = start + skinOff;
                    weights[v * 4] = HalfToFloat(block.ReadUInt16());
                    weights[v * 4 + 1] = HalfToFloat(block.ReadUInt16());
                    weights[v * 4 + 2] = HalfToFloat(block.ReadUInt16());
                    weights[v * 4 + 3] = HalfToFloat(block.ReadUInt16());
                    joints[v * 4] = block.ReadByte();
                    joints[v * 4 + 1] = block.ReadByte();
                    joints[v * 4 + 2] = block.ReadByte();
                    joints[v * 4 + 3] = block.ReadByte();
                }
                block.Position = start + stride;
            }

            var indices = new uint[triangles * 3];
            for (int t = 0; t < indices.Length; t++)
            {
                uint index = block.ReadUInt16();
                if (index >= vertices) return false;
                indices[t] = index;
            }

            if (type == "BSDynamicTriShape" && block.Position + 4 <= blockEnd)
            {
                uint dynamicSize = block.ReadUInt32();
                if (dynamicSize > 0 && block.Position + (long)vertices * 16 <= blockEnd)
                {
                    for (int v = 0; v < vertices; v++)
                    {
                        positions[v * 3] = block.ReadSingle();
                        positions[v * 3 + 1] = block.ReadSingle();
                        positions[v * 3 + 2] = block.ReadSingle();
                        block.ReadSingle();
                    }
                }
            }

            shape = new ShapeBuilder
            {
                Name = NameAt(strings, nameIndex),
                ShaderRef = shader,
                SkinRef = skinRef,
                VertexCount = vertices,
                Positions = positions,
                Normals = normals,
                Uvs = uvs,
                Indices = indices,
                Joints = joints,
                Weights = weights,
                Tx = tx, Ty = ty, Tz = tz, Scale = scale, Rot = rot
            };
            return true;
        }

        private static bool TryReadSkinBones(byte[] file, int start, int end,
            int[] blockOffset, int[] blockSizes, int blockCount, string[] strings, out List<string> bones)
        {
            bones = new List<string>();
            if (end - start < 12) return false;
            var block = new BinaryCursor(file) { Position = start };
            block.ReadInt32();
            int boneDataRef = block.ReadInt32();
            if (block.Position + 4 > end) return false;
            uint count = block.ReadUInt32();
            if (count > 4096 || block.Position + (long)count * 4 > end) return false;
            if (boneDataRef < 0 || boneDataRef >= blockCount) return false;
            for (uint i = 0; i < count; i++)
            {
                int boneRef = block.ReadInt32();
                if (boneRef < 0 || boneRef >= blockCount) { bones.Add(""); continue; }
                bones.Add(ReadBlockName(file, blockOffset[boneRef],
                    blockOffset[boneRef] + blockSizes[boneRef], strings));
            }
            return bones.Count > 0;
        }

        private static bool TryReadAvObject(BinaryCursor block, int blockEnd, uint bsVersion,
            out float tx, out float ty, out float tz, out float scale, out float[] rot)
        {
            return TryReadAvObject(block, blockEnd, bsVersion, out tx, out ty, out tz, out scale, out rot, out _);
        }

        private static bool TryReadAvObject(BinaryCursor block, int blockEnd, uint bsVersion,
            out float tx, out float ty, out float tz, out float scale, out float[] rot, out int nameIndex)
        {
            tx = ty = tz = 0; scale = 1; rot = Identity(); nameIndex = -1;
            if (block.Position + 8 > blockEnd) return false;
            nameIndex = block.ReadInt32();
            uint extra = block.ReadUInt32();
            if (extra > 10000 || block.Position + extra * 4 + 4 > blockEnd) return false;
            for (uint i = 0; i < extra; i++) block.ReadInt32();
            block.ReadInt32();
            if (block.Position + 4 + 12 + 36 + 4 + 4 > blockEnd) return false;
            block.ReadUInt32();
            tx = block.ReadSingle();
            ty = block.ReadSingle();
            tz = block.ReadSingle();
            rot = new float[9];
            for (int i = 0; i < 9; i++) rot[i] = block.ReadSingle();
            scale = block.ReadSingle();
            block.ReadInt32();
            return true;
        }

        private static void ApplyChain(int block, Xform[] xforms, int[] parent, ref float x, ref float y, ref float z)
        {
            var chain = new List<int>();
            int guard = 0;
            for (int i = block; i >= 0 && guard++ < 64; i = parent[i])
            {
                if (xforms[i] != null) chain.Add(i);
            }
            foreach (int i in chain)
            {
                Xform xf = xforms[i];
                float sx = x * xf.Scale;
                float sy = y * xf.Scale;
                float sz = z * xf.Scale;
                float[] r = xf.Rot;
                x = r[0] * sx + r[1] * sy + r[2] * sz + xf.Tx;
                y = r[3] * sx + r[4] * sy + r[5] * sz + xf.Ty;
                z = r[6] * sx + r[7] * sy + r[8] * sz + xf.Tz;
            }
        }

        private sealed class TexHit
        {
            public int Index = -1;
            public string Status = "no_material";
            public string Path = "";
            public string Format = "";
            public string Material = "";
        }

        private static TexHit ResolveShapeTexture(
            byte[] file, ShapeBuilder shape, int[] blockOffset, int[] blockSizes, int[] blockTypeIndex, string[] types,
            int blockCount, string[] strings, string context, Dictionary<string, int> index, List<byte[]> pngs, Action<string> logger)
        {
            var hit = new TexHit();
            int shader = shape.ShaderRef;
            bool haveShader = shader >= 0 && shader < blockCount;
            string materialPath = "";
            if (haveShader)
            {
                string shaderType = blockTypeIndex[shader] < types.Length ? types[blockTypeIndex[shader]] : "";
                if (shaderType.IndexOf("ShaderProperty", StringComparison.Ordinal) >= 0)
                    materialPath = ReadBlockName(file, blockOffset[shader], blockOffset[shader] + blockSizes[shader], strings);
            }
            hit.Material = materialPath ?? "";

            if (IsMaterialPath(materialPath))
            {
                TexHit fromMaterial = ResolveMaterialDiffuse(materialPath, context, index, pngs, logger, 0);
                fromMaterial.Material = materialPath;
                if (fromMaterial.Index >= 0) return fromMaterial;
                hit = fromMaterial;
            }
            else if (EndsWith(materialPath, ".dds"))
            {
                TexHit direct = ResolveTexture(materialPath, context, index, pngs, logger);
                direct.Material = materialPath;
                if (direct.Index >= 0) return direct;
                hit = direct;
            }

            if (haveShader)
            {
                int shaderEnd = blockOffset[shader] + blockSizes[shader];
                string embedded = PreferDiffuse(MaterialPreview.ScanStrings(file, blockOffset[shader], shaderEnd));
                if (!string.IsNullOrWhiteSpace(embedded))
                {
                    TexHit fromShader = ResolveTexture(embedded, context, index, pngs, logger);
                    fromShader.Material = hit.Material;
                    if (fromShader.Index >= 0) return fromShader;
                    if (hit.Status == "no_material") hit = fromShader;
                }

                int setRef = FindTextureSetRef(file, blockOffset[shader], shaderEnd, blockTypeIndex, types, blockCount);
                TexHit fromSet = TextureFromSet(file, setRef, blockOffset, blockSizes, context, index, pngs, logger, hit.Material);
                if (fromSet != null)
                {
                    if (fromSet.Index >= 0) return fromSet;
                    hit = fromSet;
                }
            }

            int next = shader + 1;
            if (next >= 0 && next < blockCount)
            {
                string nextType = blockTypeIndex[next] < types.Length ? types[blockTypeIndex[next]] : "";
                if (nextType == "BSShaderTextureSet")
                {
                    TexHit fromNext = TextureFromSet(file, next, blockOffset, blockSizes, context, index, pngs, logger, hit.Material);
                    if (fromNext != null) return fromNext;
                }
            }

            return hit;
        }

        private static TexHit ResolveMaterialDiffuse(string materialPath, string context, Dictionary<string, int> index, List<byte[]> pngs, Action<string> logger, int depth)
        {
            var hit = new TexHit { Status = "material_not_found", Path = materialPath, Material = materialPath };
            if (!GameTextureIndex.TryResolve(materialPath, context, out GameTextureIndex.TextureSource material))
                return hit;
            byte[] bytes;
            try { bytes = GameTextureIndex.ReadBytes(material, 1024, logger); }
            catch { return hit; }
            if (!MaterialPreview.TryParse(bytes, out MaterialPreview.Info info))
                return new TexHit { Status = "no_diffuse", Path = materialPath, Material = materialPath };

            string diffuse = null;
            foreach (MaterialPreview.TextureSlot slot in info.Textures)
            {
                if (slot.Role == "diffuse") { diffuse = slot.Path; break; }
            }
            if (string.IsNullOrWhiteSpace(diffuse) && depth < 3 && IsMaterialPath(info.Template))
            {
                TexHit followed = ResolveMaterialDiffuse(info.Template, context, index, pngs, logger, depth + 1);
                if (followed.Index >= 0)
                {
                    followed.Material = materialPath;
                    return followed;
                }
            }
            if (string.IsNullOrWhiteSpace(diffuse) && info.Textures.Count > 0)
                diffuse = info.Textures[0].Path;
            if (string.IsNullOrWhiteSpace(diffuse))
                return new TexHit { Status = "no_diffuse", Path = materialPath, Material = materialPath };

            TexHit texture = ResolveTexture(diffuse, context, index, pngs, logger);
            texture.Material = materialPath;
            return texture;
        }

        private static TexHit TextureFromSet(
            byte[] file, int setRef, int[] blockOffset, int[] blockSizes, string context,
            Dictionary<string, int> index, List<byte[]> pngs, Action<string> logger, string material)
        {
            if (setRef < 0 || setRef >= blockOffset.Length) return null;
            if (!TryFirstTexture(file, blockOffset[setRef], blockOffset[setRef] + blockSizes[setRef], out string texPath))
                return null;
            TexHit hit = ResolveTexture(texPath, context, index, pngs, logger);
            hit.Material = material ?? "";
            return hit;
        }

        private static int FindTextureSetRef(byte[] file, int start, int end, int[] blockTypeIndex, string[] types, int blockCount)
        {
            for (int pos = start + 12; pos + 4 <= end; pos += 4)
            {
                int idx = BitConverter.ToInt32(file, pos);
                if (idx < 0 || idx >= blockCount) continue;
                int typeIndex = blockTypeIndex[idx];
                if (typeIndex >= 0 && typeIndex < types.Length && types[typeIndex] == "BSShaderTextureSet")
                    return idx;
            }
            return -1;
        }

        private static string PreferDiffuse(List<string> strings)
        {
            if (strings == null) return null;
            string firstDds = null;
            foreach (string text in strings)
            {
                if (!EndsWith(text, ".dds")) continue;
                if (firstDds == null) firstDds = text;
                if (MaterialPreview.RoleFor(text) == "diffuse") return text;
            }
            return firstDds;
        }

        private static bool IsMaterialPath(string path)
        {
            return EndsWith(path, ".bgsm") || EndsWith(path, ".bgem");
        }

        private static bool EndsWith(string path, string extension)
        {
            return !string.IsNullOrWhiteSpace(path) && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }

        private static List<Bone> BuildBones(Xform[] xforms, int[] parent, ref float minX, ref float minY, ref float minZ, ref float maxX, ref float maxY, ref float maxZ, ref bool hasBounds)
        {
            var blocks = new List<int>();
            for (int i = 0; i < xforms.Length; i++)
            {
                if (xforms[i] != null && xforms[i].IsNode) blocks.Add(i);
            }
            var blockToBone = new Dictionary<int, int>();
            for (int i = 0; i < blocks.Count; i++) blockToBone[blocks[i]] = i;

            var bones = new List<Bone>(blocks.Count);
            foreach (int block in blocks)
            {
                float x = 0, y = 0, z = 0;
                ApplyChain(block, xforms, parent, ref x, ref y, ref z);
                int parentBlock = parent[block];
                int parentBone = parentBlock >= 0 && blockToBone.TryGetValue(parentBlock, out int mapped) ? mapped : -1;
                bones.Add(new Bone { Name = xforms[block].Name, Parent = parentBone, X = x, Y = y, Z = z });
                if (!hasBounds)
                {
                    minX = maxX = x; minY = maxY = y; minZ = maxZ = z;
                    hasBounds = true;
                }
                else
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }
            }
            return bones;
        }

        private static TexHit ResolveTexture(string texturePath, string context, Dictionary<string, int> index, List<byte[]> pngs, Action<string> logger)
        {
            var hit = new TexHit { Status = "texture_not_found", Path = texturePath ?? "" };
            string key = GameTextureIndex.Normalize(texturePath);
            if (index.TryGetValue(key, out int existing))
            {
                hit.Index = existing;
                hit.Status = "ok";
                return hit;
            }
            if (!GameTextureIndex.TryResolve(texturePath, context, out GameTextureIndex.TextureSource source))
                return hit;
            try
            {
                byte[] bytes = GameTextureIndex.ReadBytes(source, 1024, logger);
                DdsPreview.Result preview = DdsPreview.ToPng(bytes, 1024);
                if (!preview.Ok || preview.Png == null)
                    return new TexHit { Status = "decode_failed", Path = texturePath, Format = preview.Format ?? "" };
                int slot = pngs.Count;
                pngs.Add(preview.Png);
                index[key] = slot;
                hit.Index = slot;
                hit.Status = "ok";
                hit.Format = preview.Format ?? "";
                return hit;
            }
            catch
            {
                return new TexHit { Status = "decode_failed", Path = texturePath };
            }
        }

        private static string ReadBlockName(byte[] file, int start, int end, string[] strings)
        {
            if (end - start < 4) return "";
            int nameIndex = BitConverter.ToInt32(file, start);
            return NameAt(strings, nameIndex);
        }

        private static bool TryFirstTexture(byte[] file, int start, int end, out string path)
        {
            path = null;
            var block = new BinaryCursor(file) { Position = start };
            if (block.Position + 8 > end) return false;
            block.ReadInt32();
            uint extra = block.ReadUInt32();
            if (extra > 10000 || block.Position + extra * 4 + 4 > end) return false;
            for (uint i = 0; i < extra; i++) block.ReadInt32();
            block.ReadInt32();
            if (block.Position + 4 > end) return false;
            uint count = block.ReadUInt32();
            if (count == 0 || count > 32) return false;
            if (block.Position + 4 > end) return false;
            uint length = block.ReadUInt32();
            if (length == 0 || length > 512 || block.Position + length > end) return false;
            path = Encoding.ASCII.GetString(file, block.Position, (int)length).TrimEnd('\0').Replace('\\', '/');
            return path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
        }

        private static string NameAt(string[] strings, int index)
        {
            if (strings == null || index < 0 || index >= strings.Length) return "";
            return strings[index] ?? "";
        }

        private static string ReadExportString(BinaryCursor reader)
        {
            int length = reader.ReadByte();
            return Encoding.ASCII.GetString(reader.ReadBytes(length));
        }

        private static float[] Identity() => new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        private static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 1;
            int exp = (h >> 10) & 0x1F;
            int mant = h & 0x3FF;
            float value;
            if (exp == 0) value = mant / 1024f * MathF.Pow(2f, -14f);
            else if (exp == 31) value = mant == 0 ? float.PositiveInfinity : float.NaN;
            else value = (1f + mant / 1024f) * MathF.Pow(2f, exp - 15);
            return sign == 0 ? value : -value;
        }

        private sealed class BinaryCursor
        {
            private readonly byte[] _data;
            public int Position;

            public BinaryCursor(byte[] data) { _data = data; }

            public byte ReadByte() => _data[Position++];
            public ushort ReadUInt16()
            {
                ushort value = (ushort)(_data[Position] | (_data[Position + 1] << 8));
                Position += 2;
                return value;
            }
            public uint ReadUInt32()
            {
                uint value = (uint)(_data[Position] | (_data[Position + 1] << 8) | (_data[Position + 2] << 16) | (_data[Position + 3] << 24));
                Position += 4;
                return value;
            }
            public int ReadInt32() => (int)ReadUInt32();
            public ulong ReadUInt64()
            {
                ulong lo = ReadUInt32();
                ulong hi = ReadUInt32();
                return lo | (hi << 32);
            }
            public float ReadSingle()
            {
                float value = BitConverter.ToSingle(_data, Position);
                Position += 4;
                return value;
            }
            public byte[] ReadBytes(int count)
            {
                var bytes = new byte[count];
                Buffer.BlockCopy(_data, Position, bytes, 0, count);
                Position += count;
                return bytes;
            }
            public string ReadLine()
            {
                int start = Position;
                while (Position < _data.Length && _data[Position] != 10) Position++;
                int length = Position - start;
                if (Position < _data.Length && _data[Position] == 10) Position++;
                if (length > 0 && _data[start + length - 1] == 13) length--;
                return Encoding.ASCII.GetString(_data, start, length);
            }
            public string ReadSizedString()
            {
                int length = (int)ReadUInt32();
                if (length < 0 || Position + length > _data.Length) throw new InvalidOperationException("Bad NIF string.");
                string text = Encoding.ASCII.GetString(_data, Position, length);
                Position += length;
                return text;
            }
        }
    }
}
