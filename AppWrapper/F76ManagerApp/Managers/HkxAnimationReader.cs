using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace F76ManagerApp.Managers
{
    public static partial class HkxAnimationReader
    {
        public const float MaxDurationSec = 60f;
        public const int MaxModelFrames = 300;

        public enum Failure
        {
            None,
            NotHkx,
            UnsupportedVersion,
            BehaviorGraph,
            CharacterSetup,
            ProjectData,
            NotClip,
            NoClip,
            NoSkeleton,
            DecodeFailed
        }

        public sealed class Bone
        {
            public string Name { get; init; } = "";
            public int Parent { get; init; } = -1;
            public float X { get; init; }
            public float Y { get; init; }
            public float Z { get; init; }
        }

        public sealed class Result
        {
            public bool Ok { get; init; }
            public Failure Error { get; init; }
            public string Message { get; init; } = "";
            public float Duration { get; init; }
            public int FrameCount { get; init; }
            public int TrackCount { get; init; }
            public bool Trimmed { get; init; }
            public List<Bone> Bones { get; init; } = new List<Bone>();
            public float[] WorldPositions { get; init; }
            public float[] WorldMatrices { get; init; }
            public float[] InverseBind { get; init; }
            public float MinX { get; init; }
            public float MinY { get; init; }
            public float MinZ { get; init; }
            public float MaxX { get; init; }
            public float MaxY { get; init; }
            public float MaxZ { get; init; }
        }

        private sealed class Qs
        {
            public float Tx, Ty, Tz;
            public float Rx, Ry, Rz, Rw = 1f;
            public float Sx = 1f, Sy = 1f, Sz = 1f;
            public bool HasT;
            public bool HasR;
            public bool HasS;
        }

        private sealed class SkeletonData
        {
            public string Name = "";
            public List<string> Names = new List<string>();
            public List<int> Parents = new List<int>();
            public List<Qs> Reference = new List<Qs>();
        }

        private sealed class ClipData
        {
            public float Duration;
            public int NumFrames;
            public int NumTracks;
            public int NumBlocks;
            public int MaxFramesPerBlock = 256;
            public int MaskAndQuantSize;
            public float FrameDuration;
            public List<uint> BlockOffsets = new List<uint>();
            public byte[] Data = Array.Empty<byte>();
            public List<string> TrackNames = new List<string>();
            public List<short> TrackToBone = new List<short>();
            public bool Interleaved;
            public List<Qs> InterleavedTransforms = new List<Qs>();
        }

        private sealed class Packfile
        {
            public byte[] Data = Array.Empty<byte>();
            public int ClassnamesStart;
            public int DataAbs;
            public Dictionary<int, int> Fixups = new Dictionary<int, int>();
            public List<(int Rel, string Class)> Objects = new List<(int, string)>();
        }

        private static readonly object SkeletonCacheGate = new object();
        private static readonly Dictionary<string, SkeletonData> SkeletonCache =
            new Dictionary<string, SkeletonData>(StringComparer.OrdinalIgnoreCase);

        private static readonly byte[] Magic = { 0x57, 0xE0, 0xE0, 0x57 };

        public static Result Read(byte[] file, string entryPath, string contextArchiveOrFolder, Action<string> logger = null)
            => Read(file, entryPath, contextArchiveOrFolder, logger, withMatrices: false);

        public static Result Read(byte[] file, string entryPath, string contextArchiveOrFolder, Action<string> logger, bool withMatrices)
        {
            try
            {
                if (file == null || file.Length < 0x40) return Fail(Failure.NotHkx, "Not an HKX file.");

                if (IsTagfile(file))
                    return ReadTagfile(file, entryPath, contextArchiveOrFolder, logger, withMatrices);

                if (!StartsWith(file, Magic)) return Fail(Failure.NotHkx, "Not an HKX file.");

                uint fileVersion = U32(file, 0x0C);
                string contentsVersion = ReadAscii(file, 0x28, 16);
                if (fileVersion != 11 || !contentsVersion.StartsWith("hk_2014.1.0", StringComparison.Ordinal))
                    return Fail(Failure.UnsupportedVersion, contentsVersion);

                if (!TryParsePackfile(file, out Packfile pack))
                    return Fail(Failure.DecodeFailed, "Invalid packfile.");

                bool hasBehavior = false;
                bool hasSpline = false;
                bool hasInterleaved = false;
                foreach (var (_, cls) in pack.Objects)
                {
                    if (cls == "hkbBehaviorGraph") hasBehavior = true;
                    if (cls == "hkaSplineCompressedAnimation") hasSpline = true;
                    if (cls == "hkaInterleavedUncompressedAnimation") hasInterleaved = true;
                }

                if (hasBehavior && !hasSpline && !hasInterleaved)
                    return Fail(Failure.BehaviorGraph, "Behavior graph.");

                if (!TryReadClip(pack, out ClipData clip))
                    return hasBehavior
                        ? Fail(Failure.BehaviorGraph, "Behavior graph.")
                        : Fail(Failure.NoClip, "No animation clip.");

                SkeletonData skeleton = FindSkeletonInPack(pack);
                if (skeleton == null || skeleton.Names.Count == 0)
                    skeleton = ResolveExternalSkeleton(entryPath, contextArchiveOrFolder, logger);

                return BuildPreview(clip, skeleton, withMatrices);
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BUNDLE] HKX preview failed: {ex.Message}");
                return Fail(Failure.DecodeFailed, ex.Message);
            }
        }

        private static Result BuildRestPosePreview(SkeletonData skeleton, bool withMatrices = false)
        {
            if (skeleton == null || skeleton.Names.Count == 0)
                return Fail(Failure.NoClip, "No animation clip.");

            var clip = new ClipData
            {
                Duration = 0f,
                NumFrames = 1,
                NumTracks = skeleton.Names.Count,
                Interleaved = true,
                InterleavedTransforms = new List<Qs>(skeleton.Reference)
            };
            while (clip.InterleavedTransforms.Count < skeleton.Names.Count)
                clip.InterleavedTransforms.Add(new Qs { HasT = true, HasR = true, HasS = true });

            for (int i = 0; i < skeleton.Names.Count; i++)
                clip.TrackToBone.Add((short)i);

            return BuildPreview(clip, skeleton, withMatrices);
        }

        private static Result BuildPreview(ClipData clip, SkeletonData skeleton, bool withMatrices = false)
        {
            if (skeleton == null || skeleton.Names.Count == 0)
            {
                return new Result
                {
                    Error = Failure.NoSkeleton,
                    Message = "No skeleton.",
                    Duration = clip.Duration,
                    FrameCount = clip.NumFrames,
                    TrackCount = clip.NumTracks
                };
            }

            List<Qs[]> frames = SampleClip(clip);
            if (frames.Count == 0)
                return Fail(Failure.DecodeFailed, "Empty clip.");

            bool trimmed = false;
            float duration = clip.Duration;
            if (duration > MaxDurationSec && frames.Count > 1)
            {
                float frameDur = duration / Math.Max(1, frames.Count - 1);
                int keep = Math.Max(2, (int)Math.Floor(MaxDurationSec / Math.Max(frameDur, 1e-6f)) + 1);
                if (keep < frames.Count)
                {
                    frames = frames.GetRange(0, keep);
                    duration = MaxDurationSec;
                    trimmed = true;
                }
            }

            if (withMatrices && frames.Count > MaxModelFrames)
            {
                frames = SubsampleFrames(frames, MaxModelFrames);
                trimmed = true;
            }

            int[] map = BuildTrackMap(clip, skeleton);
            int boneCount = skeleton.Names.Count;
            float[] world = new float[frames.Count * boneCount * 3];
            float[] matrices = withMatrices ? new float[frames.Count * boneCount * 12] : null;
            float minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            bool hasBounds = false;
            var restBones = new List<Bone>(boneCount);

            for (int f = 0; f < frames.Count; f++)
            {
                Qs[] locals = BuildLocalPose(skeleton, frames[f], map);
                var worldQs = new Qs[boneCount];
                float[] worldFrame = new float[boneCount * 3];
                ConcatenateWorldQs(skeleton, locals, worldQs, worldFrame,
                    ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ, ref hasBounds);
                Buffer.BlockCopy(worldFrame, 0, world, f * worldFrame.Length * sizeof(float), worldFrame.Length * sizeof(float));
                if (matrices != null)
                {
                    int baseMat = f * boneCount * 12;
                    for (int b = 0; b < boneCount; b++)
                        WriteMatrix3x4(matrices, baseMat + b * 12, worldQs[b]);
                }
                if (f == 0)
                {
                    for (int b = 0; b < boneCount; b++)
                    {
                        restBones.Add(new Bone
                        {
                            Name = skeleton.Names[b],
                            Parent = b < skeleton.Parents.Count ? skeleton.Parents[b] : -1,
                            X = worldFrame[b * 3],
                            Y = worldFrame[b * 3 + 1],
                            Z = worldFrame[b * 3 + 2]
                        });
                    }
                }
            }

            if (!hasBounds)
            {
                minX = maxX = 0; minY = maxY = 0; minZ = maxZ = 0;
            }

            float[] inverseBind = null;
            if (withMatrices)
                inverseBind = BuildInverseBind(skeleton);

            return new Result
            {
                Ok = true,
                Duration = duration,
                FrameCount = frames.Count,
                TrackCount = clip.NumTracks,
                Trimmed = trimmed,
                Bones = restBones,
                WorldPositions = world,
                WorldMatrices = matrices,
                InverseBind = inverseBind,
                MinX = minX, MinY = minY, MinZ = minZ,
                MaxX = maxX, MaxY = maxY, MaxZ = maxZ
            };
        }

        private static List<Qs[]> SubsampleFrames(List<Qs[]> frames, int maxFrames)
        {
            if (frames.Count <= maxFrames) return frames;
            var sampled = new List<Qs[]>(maxFrames);
            for (int i = 0; i < maxFrames; i++)
            {
                int index = maxFrames == 1 ? 0 : (int)Math.Round(i * (frames.Count - 1) / (double)(maxFrames - 1));
                sampled.Add(frames[index]);
            }
            return sampled;
        }

        private static float[] BuildInverseBind(SkeletonData skeleton)
        {
            int boneCount = skeleton.Names.Count;
            var locals = new Qs[boneCount];
            for (int i = 0; i < boneCount; i++)
                locals[i] = i < skeleton.Reference.Count ? CloneQs(skeleton.Reference[i]) : new Qs { HasT = true, HasR = true, HasS = true };
            var worldQs = new Qs[boneCount];
            float[] unused = new float[boneCount * 3];
            float minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            bool hasBounds = false;
            ConcatenateWorldQs(skeleton, locals, worldQs, unused,
                ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ, ref hasBounds);
            var inverse = new float[boneCount * 12];
            for (int b = 0; b < boneCount; b++)
                WriteInverseMatrix3x4(inverse, b * 12, worldQs[b]);
            return inverse;
        }

        private static void WriteMatrix3x4(float[] dest, int offset, Qs qs)
        {
            float[] q = { qs.Rx, qs.Ry, qs.Rz, qs.Rw };
            float c0x = 1, c0y = 0, c0z = 0; RotateVec(q, ref c0x, ref c0y, ref c0z);
            float c1x = 0, c1y = 1, c1z = 0; RotateVec(q, ref c1x, ref c1y, ref c1z);
            float c2x = 0, c2y = 0, c2z = 1; RotateVec(q, ref c2x, ref c2y, ref c2z);
            c0x *= qs.Sx; c0y *= qs.Sx; c0z *= qs.Sx;
            c1x *= qs.Sy; c1y *= qs.Sy; c1z *= qs.Sy;
            c2x *= qs.Sz; c2y *= qs.Sz; c2z *= qs.Sz;
            dest[offset] = c0x; dest[offset + 1] = c1x; dest[offset + 2] = c2x; dest[offset + 3] = qs.Tx;
            dest[offset + 4] = c0y; dest[offset + 5] = c1y; dest[offset + 6] = c2y; dest[offset + 7] = qs.Ty;
            dest[offset + 8] = c0z; dest[offset + 9] = c1z; dest[offset + 10] = c2z; dest[offset + 11] = qs.Tz;
        }

        private static void WriteInverseMatrix3x4(float[] dest, int offset, Qs qs)
        {
            // World linear is R * diag(s). Columns are R's axes scaled by s.
            // Inverse linear is diag(1/s) * R^T, i.e. column components / s^2.
            float sx = MathF.Abs(qs.Sx) < 1e-8f ? 1f : qs.Sx;
            float sy = MathF.Abs(qs.Sy) < 1e-8f ? 1f : qs.Sy;
            float sz = MathF.Abs(qs.Sz) < 1e-8f ? 1f : qs.Sz;
            float[] q = { qs.Rx, qs.Ry, qs.Rz, qs.Rw };
            float c0x = 1, c0y = 0, c0z = 0; RotateVec(q, ref c0x, ref c0y, ref c0z);
            float c1x = 0, c1y = 1, c1z = 0; RotateVec(q, ref c1x, ref c1y, ref c1z);
            float c2x = 0, c2y = 0, c2z = 1; RotateVec(q, ref c2x, ref c2y, ref c2z);
            c0x *= qs.Sx; c0y *= qs.Sx; c0z *= qs.Sx;
            c1x *= qs.Sy; c1y *= qs.Sy; c1z *= qs.Sy;
            c2x *= qs.Sz; c2y *= qs.Sz; c2z *= qs.Sz;
            float i00 = c0x / (sx * sx), i01 = c0y / (sx * sx), i02 = c0z / (sx * sx);
            float i10 = c1x / (sy * sy), i11 = c1y / (sy * sy), i12 = c1z / (sy * sy);
            float i20 = c2x / (sz * sz), i21 = c2y / (sz * sz), i22 = c2z / (sz * sz);
            float tx = -(i00 * qs.Tx + i01 * qs.Ty + i02 * qs.Tz);
            float ty = -(i10 * qs.Tx + i11 * qs.Ty + i12 * qs.Tz);
            float tz = -(i20 * qs.Tx + i21 * qs.Ty + i22 * qs.Tz);
            dest[offset] = i00; dest[offset + 1] = i01; dest[offset + 2] = i02; dest[offset + 3] = tx;
            dest[offset + 4] = i10; dest[offset + 5] = i11; dest[offset + 6] = i12; dest[offset + 7] = ty;
            dest[offset + 8] = i20; dest[offset + 9] = i21; dest[offset + 10] = i22; dest[offset + 11] = tz;
        }

        private static Result Fail(Failure error, string message) =>
            new Result { Error = error, Message = message ?? "" };

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (data[i] != prefix[i]) return false;
            return true;
        }

        private static bool TryParsePackfile(byte[] data, out Packfile pack)
        {
            pack = null;
            if (data.Length < 0x110) return false;
            var sections = new Dictionary<string, Section>(StringComparer.Ordinal);
            int secStart = 0x50;
            for (int i = 0; i < 3; i++)
            {
                int baseOff = secStart + i * 0x40;
                string name = ReadAscii(data, baseOff, 16);
                int abs = (int)U32(data, baseOff + 0x14);
                sections[name] = new Section
                {
                    Offset = abs,
                    Data1 = abs + (int)U32(data, baseOff + 0x18),
                    Data2 = abs + (int)U32(data, baseOff + 0x1C),
                    Data3 = abs + (int)U32(data, baseOff + 0x20),
                    Exports = abs + (int)U32(data, baseOff + 0x24),
                    End = abs + (int)U32(data, baseOff + 0x2C)
                };
            }
            if (!sections.TryGetValue("__classnames__", out Section cn) ||
                !sections.TryGetValue("__data__", out Section dataSec))
                return false;

            var fixups = new Dictionary<int, int>();
            int pos = dataSec.Data1;
            while (pos + 8 <= dataSec.Data2 && pos + 8 <= data.Length)
            {
                uint src = U32(data, pos);
                uint dst = U32(data, pos + 4);
                if (src == 0xFFFFFFFFu) break;
                fixups[(int)src] = (int)dst;
                pos += 8;
            }

            var objects = new List<(int, string)>();
            pos = dataSec.Data3;
            while (pos + 12 <= dataSec.Exports && pos + 12 <= data.Length)
            {
                uint src = U32(data, pos);
                uint nameOff = U32(data, pos + 8);
                if (src == 0xFFFFFFFFu) break;
                objects.Add(((int)src, ReadAscii(data, cn.Offset + (int)nameOff, 256)));
                pos += 12;
            }

            pack = new Packfile
            {
                Data = data,
                ClassnamesStart = cn.Offset,
                DataAbs = dataSec.Offset,
                Fixups = fixups,
                Objects = objects
            };
            return true;
        }

        private sealed class Section
        {
            public int Offset, Data1, Data2, Data3, Exports, End;
        }

        private static bool TryReadClip(Packfile pack, out ClipData clip)
        {
            clip = null;
            foreach (var (rel, cls) in pack.Objects)
            {
                if (cls == "hkaSplineCompressedAnimation")
                {
                    clip = ReadSplineClip(pack, rel);
                    if (clip != null) return true;
                }
            }
            foreach (var (rel, cls) in pack.Objects)
            {
                if (cls == "hkaInterleavedUncompressedAnimation")
                {
                    clip = ReadInterleavedClip(pack, rel);
                    if (clip != null) return true;
                }
            }
            return false;
        }

        private static ClipData ReadSplineClip(Packfile pack, int animRel)
        {
            byte[] data = pack.Data;
            int a = pack.DataAbs + animRel;
            if (a + 0xA8 > data.Length) return null;

            var clip = new ClipData
            {
                Duration = F32(data, a + 0x14),
                NumTracks = (int)U32(data, a + 0x18),
                NumFrames = (int)U32(data, a + 0x38),
                NumBlocks = (int)U32(data, a + 0x3C),
                MaxFramesPerBlock = (int)U32(data, a + 0x40),
                MaskAndQuantSize = (int)U32(data, a + 0x44),
                FrameDuration = F32(data, a + 0x50),
                BlockOffsets = ReadArrayU32(pack, animRel, 0x58),
                Data = ReadArrayU8(pack, animRel, 0x98)
            };
            if (clip.NumTracks <= 0 || clip.NumFrames <= 0 || clip.NumBlocks <= 0) return null;
            if (clip.BlockOffsets.Count == 0 || clip.Data.Length == 0) return null;
            if (clip.MaxFramesPerBlock <= 0) clip.MaxFramesPerBlock = 256;
            if (clip.MaskAndQuantSize <= 0) clip.MaskAndQuantSize = Align(4 * clip.NumTracks, 4);
            ReadAnnotationsAndBinding(pack, animRel, clip);
            return clip;
        }

        private static ClipData ReadInterleavedClip(Packfile pack, int animRel)
        {
            byte[] data = pack.Data;
            int a = pack.DataAbs + animRel;
            if (a + 0x58 > data.Length) return null;

            int numTracks = (int)U32(data, a + 0x18);
            float duration = F32(data, a + 0x14);
            List<Qs> transforms = ReadArrayQs(pack, animRel, 0x38);
            if (numTracks <= 0 || transforms.Count == 0) return null;
            int numFrames = transforms.Count / numTracks;
            if (numFrames <= 0) return null;

            var clip = new ClipData
            {
                Duration = duration,
                NumTracks = numTracks,
                NumFrames = numFrames,
                NumBlocks = 1,
                MaxFramesPerBlock = numFrames,
                FrameDuration = numFrames > 1 ? duration / (numFrames - 1) : duration,
                Interleaved = true,
                InterleavedTransforms = transforms
            };
            ReadAnnotationsAndBinding(pack, animRel, clip);
            return clip;
        }

        private static void ReadAnnotationsAndBinding(Packfile pack, int animRel, ClipData clip)
        {
            byte[] data = pack.Data;
            int annCount = ArrayCount(pack, animRel + 0x28);
            int annContent = Fixup(pack, animRel + 0x28);
            if (annContent >= 0 && annCount > 0)
            {
                const int stride = 0x18;
                for (int i = 0; i < annCount; i++)
                {
                    int trackRel = annContent + i * stride;
                    int strRel = Fixup(pack, trackRel);
                    clip.TrackNames.Add(strRel >= 0 ? ReadAscii(data, pack.DataAbs + strRel, 256) : "");
                }
            }

            foreach (var (rel, cls) in pack.Objects)
            {
                if (cls != "hkaAnimationBinding") continue;
                int idxCount = ArrayCount(pack, rel + 0x20);
                int idxContent = Fixup(pack, rel + 0x20);
                if (idxContent >= 0 && idxCount > 0)
                {
                    int abs = pack.DataAbs + idxContent;
                    for (int i = 0; i < idxCount && abs + i * 2 + 2 <= data.Length; i++)
                        clip.TrackToBone.Add(BitConverter.ToInt16(data, abs + i * 2));
                }
                break;
            }
        }

        private static SkeletonData FindSkeletonInPack(Packfile pack)
        {
            SkeletonData best = null;
            foreach (var (rel, cls) in pack.Objects)
            {
                if (cls != "hkaSkeleton") continue;
                SkeletonData skel = ReadSkeletonObject(pack, rel);
                if (skel == null || skel.Names.Count == 0) continue;
                if (skel.Name.IndexOf("Ragdoll", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (best == null) best = skel;
                    continue;
                }
                return skel;
            }
            return best;
        }

        private static SkeletonData ReadSkeletonObject(Packfile pack, int skelRel)
        {
            byte[] data = pack.Data;
            int s = pack.DataAbs + skelRel;
            if (s + 0x48 > data.Length) return null;
            var skel = new SkeletonData();
            int nameRel = Fixup(pack, skelRel + 0x10);
            if (nameRel >= 0) skel.Name = ReadAscii(data, pack.DataAbs + nameRel, 256);

            int piCount = ArrayCount(pack, skelRel + 0x18);
            int piContent = Fixup(pack, skelRel + 0x18);
            if (piContent >= 0 && piCount > 0)
            {
                int abs = pack.DataAbs + piContent;
                for (int i = 0; i < piCount && abs + i * 2 + 2 <= data.Length; i++)
                    skel.Parents.Add(BitConverter.ToInt16(data, abs + i * 2));
            }

            int boneCount = ArrayCount(pack, skelRel + 0x28);
            int boneContent = Fixup(pack, skelRel + 0x28);
            if (boneContent >= 0 && boneCount > 0)
            {
                const int stride = 0x10;
                for (int i = 0; i < boneCount; i++)
                {
                    int boneRel = boneContent + i * stride;
                    int strRel = Fixup(pack, boneRel);
                    skel.Names.Add(strRel >= 0 ? ReadAscii(data, pack.DataAbs + strRel, 256) : "");
                }
            }

            int poseCount = ArrayCount(pack, skelRel + 0x38);
            int poseContent = Fixup(pack, skelRel + 0x38);
            if (poseContent >= 0 && poseCount > 0)
            {
                const int stride = 0x30;
                for (int i = 0; i < poseCount; i++)
                {
                    int p = pack.DataAbs + poseContent + i * stride;
                    if (p + stride > data.Length) break;
                    skel.Reference.Add(new Qs
                    {
                        Tx = F32(data, p), Ty = F32(data, p + 4), Tz = F32(data, p + 8),
                        Rx = F32(data, p + 16), Ry = F32(data, p + 20), Rz = F32(data, p + 24), Rw = F32(data, p + 28),
                        Sx = F32(data, p + 32), Sy = F32(data, p + 36), Sz = F32(data, p + 40),
                        HasT = true, HasR = true, HasS = true
                    });
                }
            }

            while (skel.Parents.Count < skel.Names.Count) skel.Parents.Add(-1);
            while (skel.Reference.Count < skel.Names.Count)
                skel.Reference.Add(new Qs { HasT = true, HasR = true, HasS = true });
            return skel.Names.Count > 0 ? skel : null;
        }

        private static SkeletonData ResolveExternalSkeleton(string entryPath, string context, Action<string> logger)
        {
            string normalized = (entryPath ?? "").Replace('\\', '/').Trim().TrimStart('/');
            string actorRoot = ActorRoot(normalized);
            if (string.IsNullOrEmpty(actorRoot)) return null;

            string[] candidates =
            {
                actorRoot + "characterassets/skeleton.hkx",
                actorRoot + "characterassets/skeleton_female.hkx",
                actorRoot + "characterassets/skeleton.nif",
                actorRoot + "character assets/skeleton.hkx",
                actorRoot + "character assets/skeleton_female.hkx",
                actorRoot + "character assets/skeleton.nif"
            };

            foreach (string candidate in candidates)
            {
                SkeletonData skel = TryLoadSkeletonPath(candidate, context, logger);
                if (skel != null) return skel;
            }
            return null;
        }

        private static string ActorRoot(string path)
        {
            string lower = path.ToLowerInvariant();
            const string marker = "meshes/actors/";
            int at = lower.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return "";
            int start = at + marker.Length;
            int slash = lower.IndexOf('/', start);
            if (slash < 0) return "";
            return path.Substring(0, slash + 1).Replace('\\', '/');
        }

        private static SkeletonData TryLoadSkeletonPath(string relativePath, string context, Action<string> logger)
        {
            string key = relativePath.Replace('\\', '/').ToLowerInvariant();
            lock (SkeletonCacheGate)
            {
                if (SkeletonCache.TryGetValue(key, out SkeletonData cached))
                    return cached;
            }

            SkeletonData loaded = null;
            try
            {
                if (Directory.Exists(context))
                {
                    string loose = Path.Combine(context, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(loose))
                        loaded = LoadSkeletonBytes(File.ReadAllBytes(loose), relativePath, logger);
                }

                if (loaded == null && File.Exists(context) &&
                    string.Equals(Path.GetExtension(context), ".ba2", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        byte[] bytes = BA2Utility.ReadEntryBytes(context, relativePath, logger);
                        loaded = LoadSkeletonBytes(bytes, relativePath, logger);
                    }
                    catch { }
                }

                string dataPath = AppPaths.DataPath;
                if (loaded == null && !string.IsNullOrWhiteSpace(dataPath))
                {
                    string loose = Path.Combine(dataPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(loose))
                        loaded = LoadSkeletonBytes(File.ReadAllBytes(loose), relativePath, logger);
                }

                if (loaded == null && !string.IsNullOrWhiteSpace(dataPath) && Directory.Exists(dataPath))
                {
                    string[] patterns = { "Fallout76 - Meshes*.ba2", "*Animations*.ba2", "SeventySix - *.ba2" };
                    foreach (string pattern in patterns)
                    {
                        foreach (string archive in Directory.GetFiles(dataPath, pattern, SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                byte[] bytes = BA2Utility.ReadEntryBytes(archive, relativePath, logger);
                                loaded = LoadSkeletonBytes(bytes, relativePath, logger);
                                if (loaded != null) break;
                            }
                            catch { }
                        }
                        if (loaded != null) break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[BUNDLE] Skeleton resolve failed for {relativePath}: {ex.Message}");
            }

            lock (SkeletonCacheGate)
            {
                SkeletonCache[key] = loaded;
            }
            return loaded;
        }

        private static SkeletonData LoadSkeletonBytes(byte[] bytes, string path, Action<string> logger)
        {
            if (bytes == null || bytes.Length == 0) return null;
            string ext = Path.GetExtension(path);
            if (ext.Equals(".hkx", StringComparison.OrdinalIgnoreCase))
            {
                if (IsTagfile(bytes))
                    return TryReadTagfileSkeleton(bytes, logger);
                if (!TryParsePackfile(bytes, out Packfile pack)) return null;
                return FindSkeletonInPack(pack);
            }
            if (ext.Equals(".nif", StringComparison.OrdinalIgnoreCase))
            {
                NifMeshReader.SkeletonResult nif = NifMeshReader.ReadSkeletonLocal(bytes, logger);
                if (!nif.Ok || nif.Bones.Count == 0) return null;
                var skel = new SkeletonData { Name = "nif" };
                foreach (NifMeshReader.LocalBone bone in nif.Bones)
                {
                    skel.Names.Add(bone.Name);
                    skel.Parents.Add(bone.Parent);
                    skel.Reference.Add(new Qs
                    {
                        Tx = bone.Tx, Ty = bone.Ty, Tz = bone.Tz,
                        Rx = bone.Rx, Ry = bone.Ry, Rz = bone.Rz, Rw = bone.Rw,
                        Sx = bone.Sx, Sy = bone.Sy, Sz = bone.Sz,
                        HasT = true, HasR = true, HasS = true
                    });
                }
                return skel;
            }
            return null;
        }

        private static int[] BuildTrackMap(ClipData clip, SkeletonData skeleton)
        {
            var map = new int[clip.NumTracks];
            for (int i = 0; i < map.Length; i++) map[i] = -1;

            if (clip.TrackToBone.Count > 0)
            {
                for (int i = 0; i < clip.NumTracks && i < clip.TrackToBone.Count; i++)
                {
                    int bone = clip.TrackToBone[i];
                    if (bone >= 0 && bone < skeleton.Names.Count) map[i] = bone;
                }
                return map;
            }

            if (clip.TrackNames.Count > 0)
            {
                var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < skeleton.Names.Count; i++)
                {
                    string name = skeleton.Names[i];
                    if (!string.IsNullOrEmpty(name) && !byName.ContainsKey(name))
                        byName[name] = i;
                }
                for (int i = 0; i < clip.NumTracks && i < clip.TrackNames.Count; i++)
                {
                    string name = clip.TrackNames[i];
                    if (!string.IsNullOrEmpty(name) && byName.TryGetValue(name, out int bone))
                        map[i] = bone;
                }
                return map;
            }

            for (int i = 0; i < clip.NumTracks && i < skeleton.Names.Count; i++)
                map[i] = i;
            return map;
        }

        private static List<Qs[]> SampleClip(ClipData clip)
        {
            if (clip.Interleaved)
            {
                var frames = new List<Qs[]>(clip.NumFrames);
                for (int f = 0; f < clip.NumFrames; f++)
                {
                    var tracks = new Qs[clip.NumTracks];
                    for (int t = 0; t < clip.NumTracks; t++)
                    {
                        int idx = f * clip.NumTracks + t;
                        Qs src = idx < clip.InterleavedTransforms.Count
                            ? clip.InterleavedTransforms[idx]
                            : new Qs { HasT = true, HasR = true, HasS = true };
                        tracks[t] = CloneQs(src);
                        tracks[t].HasT = true;
                        tracks[t].HasR = true;
                        tracks[t].HasS = true;
                    }
                    frames.Add(tracks);
                }
                return frames;
            }

            List<TrackSamples> samples = DecompressSpline(clip);
            var result = new List<Qs[]>(clip.NumFrames);
            for (int f = 0; f < clip.NumFrames; f++)
            {
                var tracks = new Qs[clip.NumTracks];
                for (int t = 0; t < clip.NumTracks; t++)
                {
                    TrackSamples s = samples[t];
                    var qs = new Qs();
                    if (f < s.Translations.Count)
                    {
                        float[] p = s.Translations[f];
                        qs.Tx = p[0]; qs.Ty = p[1]; qs.Tz = p[2];
                        qs.HasT = s.PosAuthored;
                    }
                    if (f < s.Rotations.Count)
                    {
                        float[] r = s.Rotations[f];
                        qs.Rx = r[0]; qs.Ry = r[1]; qs.Rz = r[2]; qs.Rw = r[3];
                        qs.HasR = s.RotAuthored;
                    }
                    if (f < s.Scales.Count)
                    {
                        float[] sc = s.Scales[f];
                        qs.Sx = sc[0]; qs.Sy = sc[1]; qs.Sz = sc[2];
                        qs.HasS = s.ScaleAuthored;
                    }
                    tracks[t] = qs;
                }
                result.Add(tracks);
            }
            return result;
        }

        private sealed class TrackSamples
        {
            public List<float[]> Translations = new List<float[]>();
            public List<float[]> Rotations = new List<float[]>();
            public List<float[]> Scales = new List<float[]>();
            public bool PosAuthored;
            public bool RotAuthored;
            public bool ScaleAuthored;
        }

        private static List<TrackSamples> DecompressSpline(ClipData clip)
        {
            var all = new List<TrackSamples>(clip.NumTracks);
            for (int i = 0; i < clip.NumTracks; i++) all.Add(new TrackSamples());
            byte[] data = clip.Data;

            for (int blockIdx = 0; blockIdx < clip.NumBlocks; blockIdx++)
            {
                int blockStart = (int)clip.BlockOffsets[blockIdx];
                int firstFrame = blockIdx * (clip.MaxFramesPerBlock - 1);
                int framesInBlock = Math.Min(clip.MaxFramesPerBlock, clip.NumFrames - firstFrame);
                int skip = blockIdx > 0 ? 1 : 0;
                if (framesInBlock <= 0 || blockStart < 0 || blockStart >= data.Length) continue;

                var masks = new TrackMask[clip.NumTracks];
                int off = blockStart;
                for (int t = 0; t < clip.NumTracks; t++)
                {
                    if (off + 4 > data.Length) return all;
                    masks[t] = new TrackMask(data[off], data[off + 1], data[off + 2], data[off + 3]);
                    off += 4;
                }
                off = blockStart + clip.MaskAndQuantSize;

                for (int trackIdx = 0; trackIdx < clip.NumTracks; trackIdx++)
                {
                    TrackMask mask = masks[trackIdx];
                    TrackSamples track = all[trackIdx];

                    List<float[]> posFrames = ReadPositionBlock(data, ref off, mask, framesInBlock, out bool posAuth);
                    track.PosAuthored |= posAuth;
                    for (int i = skip; i < posFrames.Count; i++) track.Translations.Add(posFrames[i]);

                    List<float[]> rotFrames = ReadRotationBlock(data, ref off, mask, framesInBlock, out bool rotAuth);
                    track.RotAuthored |= rotAuth;
                    for (int i = skip; i < rotFrames.Count; i++) track.Rotations.Add(rotFrames[i]);

                    List<float[]> scaleFrames = ReadScaleBlock(data, ref off, mask, framesInBlock, out bool scaleAuth);
                    track.ScaleAuthored |= scaleAuth;
                    for (int i = skip; i < scaleFrames.Count; i++) track.Scales.Add(scaleFrames[i]);
                }
            }
            return all;
        }

        private readonly struct TrackMask
        {
            public readonly int PosQuant, RotQuant, ScaleQuant;
            public readonly int PosFlags, RotFlags, ScaleFlags;
            public TrackMask(byte b0, byte b1, byte b2, byte b3)
            {
                PosQuant = b0 & 0x03;
                RotQuant = (b0 >> 2) & 0x0F;
                ScaleQuant = (b0 >> 6) & 0x03;
                PosFlags = b1;
                RotFlags = b2;
                ScaleFlags = b3;
            }
            public string PosType(int axis)
            {
                if (((PosFlags >> (axis + 4)) & 1) != 0) return "spline";
                if (((PosFlags >> axis) & 1) != 0) return "static";
                return "identity";
            }
            public string RotType()
            {
                if (((RotFlags >> 4) & 0x0F) != 0) return "spline";
                if ((RotFlags & 0x0F) != 0) return "static";
                return "identity";
            }
            public string ScaleType(int axis)
            {
                if (((ScaleFlags >> (axis + 4)) & 1) != 0) return "spline";
                if (((ScaleFlags >> axis) & 1) != 0) return "static";
                return "identity";
            }
            public bool HasAnyPosSpline()
            {
                for (int a = 0; a < 3; a++) if (PosType(a) == "spline") return true;
                return false;
            }
            public bool HasAnyScaleSpline()
            {
                for (int a = 0; a < 3; a++) if (ScaleType(a) == "spline") return true;
                return false;
            }
        }

        private static List<float[]> ReadPositionBlock(byte[] data, ref int off, TrackMask mask, int framesInBlock, out bool authored)
        {
            authored = false;
            var frames = new List<float[]>(framesInBlock);
            if (mask.HasAnyPosSpline())
            {
                if (off + 3 > data.Length) return PadVec3(framesInBlock, 0, 0, 0);
                int numItems = BitConverter.ToUInt16(data, off);
                int degree = data[off + 2];
                off += 3;
                int numKnots = numItems + degree + 2;
                if (off + numKnots > data.Length) return PadVec3(framesInBlock, 0, 0, 0);
                float[] knots = new float[numKnots];
                for (int k = 0; k < numKnots; k++) knots[k] = data[off + k];
                off += numKnots;
                off = Align(off, 4);

                var axisInfo = new (string Type, float Mn, float Mx)[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    string ptype = mask.PosType(axis);
                    if (ptype == "spline")
                    {
                        authored = true;
                        float mn = F32(data, off); off += 4;
                        float mx = F32(data, off); off += 4;
                        axisInfo[axis] = ("spline", mn, mx);
                    }
                    else if (ptype == "static")
                    {
                        authored = true;
                        float val = F32(data, off); off += 4;
                        axisInfo[axis] = ("static", val, val);
                    }
                    else axisInfo[axis] = ("identity", 0, 0);
                }

                var cps = new List<float>[3] { new List<float>(), new List<float>(), new List<float>() };
                for (int i = 0; i < numItems + 1; i++)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (axisInfo[axis].Type != "spline") continue;
                        float val;
                        if (mask.PosQuant == 0) { val = Read8(data, ref off, axisInfo[axis].Mn, axisInfo[axis].Mx); }
                        else { val = Read16(data, ref off, axisInfo[axis].Mn, axisInfo[axis].Mx); }
                        cps[axis].Add(val);
                    }
                }
                off = Align(off, 4);

                for (int f = 0; f < framesInBlock; f++)
                {
                    float ft = f;
                    float[] pos = { 0, 0, 0 };
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (axisInfo[axis].Type == "spline")
                        {
                            int span = FindKnotSpan(degree, ft, cps[axis].Count, knots);
                            pos[axis] = EvalBSpline(span, degree, ft, knots, cps[axis], null);
                        }
                        else if (axisInfo[axis].Type == "static")
                            pos[axis] = axisInfo[axis].Mn;
                    }
                    frames.Add(pos);
                }
            }
            else
            {
                float[] pos = { 0, 0, 0 };
                for (int axis = 0; axis < 3; axis++)
                {
                    if (mask.PosType(axis) == "static")
                    {
                        authored = true;
                        pos[axis] = F32(data, off); off += 4;
                    }
                }
                for (int f = 0; f < framesInBlock; f++) frames.Add(new[] { pos[0], pos[1], pos[2] });
                off = Align(off, 4);
            }
            return frames;
        }

        private static List<float[]> ReadRotationBlock(byte[] data, ref int off, TrackMask mask, int framesInBlock, out bool authored)
        {
            authored = false;
            var frames = new List<float[]>(framesInBlock);
            string rotType = mask.RotType();
            int qfmt = mask.RotQuant;
            int qalign = QuatAlign(qfmt);

            if (rotType == "spline")
            {
                authored = true;
                if (off + 3 > data.Length) return PadQuat(framesInBlock);
                int numItems = BitConverter.ToUInt16(data, off);
                int degree = data[off + 2];
                off += 3;
                int numKnots = numItems + degree + 2;
                if (off + numKnots > data.Length) return PadQuat(framesInBlock);
                float[] knots = new float[numKnots];
                for (int k = 0; k < numKnots; k++) knots[k] = data[off + k];
                off += numKnots;
                if (qalign > 1) off = Align(off, qalign);

                var quatCps = new List<float[]>();
                for (int i = 0; i < numItems + 1; i++)
                {
                    float[] q = ReadQuat(qfmt, data, ref off);
                    if (quatCps.Count > 0)
                    {
                        float[] prev = quatCps[quatCps.Count - 1];
                        float dot = prev[0] * q[0] + prev[1] * q[1] + prev[2] * q[2] + prev[3] * q[3];
                        if (dot < 0) { q[0] = -q[0]; q[1] = -q[1]; q[2] = -q[2]; q[3] = -q[3]; }
                    }
                    quatCps.Add(q);
                }

                for (int f = 0; f < framesInBlock; f++)
                {
                    float ft = f;
                    int span = FindKnotSpan(degree, ft, quatCps.Count, knots);
                    float[] q = EvalBSplineVec(span, degree, ft, knots, quatCps);
                    frames.Add(NormalizeQuat(q));
                }
            }
            else if (rotType == "static")
            {
                authored = true;
                if (qalign > 1) off = Align(off, qalign);
                float[] q = ReadQuat(qfmt, data, ref off);
                for (int f = 0; f < framesInBlock; f++) frames.Add(new[] { q[0], q[1], q[2], q[3] });
            }
            else
            {
                for (int f = 0; f < framesInBlock; f++) frames.Add(new[] { 0f, 0f, 0f, 1f });
            }
            off = Align(off, 4);
            return frames;
        }

        private static List<float[]> ReadScaleBlock(byte[] data, ref int off, TrackMask mask, int framesInBlock, out bool authored)
        {
            authored = false;
            var frames = new List<float[]>(framesInBlock);
            if (mask.HasAnyScaleSpline())
            {
                if (off + 3 > data.Length) return PadVec3(framesInBlock, 1, 1, 1);
                int numItems = BitConverter.ToUInt16(data, off);
                int degree = data[off + 2];
                off += 3;
                int numKnots = numItems + degree + 2;
                if (off + numKnots > data.Length) return PadVec3(framesInBlock, 1, 1, 1);
                float[] knots = new float[numKnots];
                for (int k = 0; k < numKnots; k++) knots[k] = data[off + k];
                off += numKnots;
                off = Align(off, 4);

                var axisInfo = new (string Type, float Mn, float Mx)[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    string stype = mask.ScaleType(axis);
                    if (stype == "spline")
                    {
                        authored = true;
                        float mn = F32(data, off); off += 4;
                        float mx = F32(data, off); off += 4;
                        axisInfo[axis] = ("spline", mn, mx);
                    }
                    else if (stype == "static")
                    {
                        authored = true;
                        float val = F32(data, off); off += 4;
                        axisInfo[axis] = ("static", val, val);
                    }
                    else axisInfo[axis] = ("identity", 1, 1);
                }

                var cps = new List<float>[3] { new List<float>(), new List<float>(), new List<float>() };
                for (int i = 0; i < numItems + 1; i++)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (axisInfo[axis].Type != "spline") continue;
                        float val = mask.ScaleQuant == 0
                            ? Read8(data, ref off, axisInfo[axis].Mn, axisInfo[axis].Mx)
                            : Read16(data, ref off, axisInfo[axis].Mn, axisInfo[axis].Mx);
                        cps[axis].Add(val);
                    }
                }
                off = Align(off, 4);

                for (int f = 0; f < framesInBlock; f++)
                {
                    float ft = f;
                    float[] s = { 1, 1, 1 };
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (axisInfo[axis].Type == "spline")
                        {
                            int span = FindKnotSpan(degree, ft, cps[axis].Count, knots);
                            s[axis] = EvalBSpline(span, degree, ft, knots, cps[axis], null);
                        }
                        else if (axisInfo[axis].Type == "static")
                            s[axis] = axisInfo[axis].Mn;
                    }
                    frames.Add(s);
                }
            }
            else
            {
                float[] s = { 1, 1, 1 };
                for (int axis = 0; axis < 3; axis++)
                {
                    if (mask.ScaleType(axis) == "static")
                    {
                        authored = true;
                        s[axis] = F32(data, off); off += 4;
                    }
                }
                for (int f = 0; f < framesInBlock; f++) frames.Add(new[] { s[0], s[1], s[2] });
                off = Align(off, 4);
            }
            return frames;
        }

        private static List<float[]> PadVec3(int count, float x, float y, float z)
        {
            var list = new List<float[]>(count);
            for (int i = 0; i < count; i++) list.Add(new[] { x, y, z });
            return list;
        }

        private static List<float[]> PadQuat(int count)
        {
            var list = new List<float[]>(count);
            for (int i = 0; i < count; i++) list.Add(new[] { 0f, 0f, 0f, 1f });
            return list;
        }

        private static Qs[] BuildLocalPose(SkeletonData skeleton, Qs[] tracks, int[] map)
        {
            int boneCount = skeleton.Names.Count;
            var locals = new Qs[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                Qs rest = b < skeleton.Reference.Count ? skeleton.Reference[b] : new Qs { HasT = true, HasR = true, HasS = true };
                locals[b] = CloneQs(rest);
                locals[b].HasT = true;
                locals[b].HasR = true;
                locals[b].HasS = true;
            }
            for (int t = 0; t < tracks.Length && t < map.Length; t++)
            {
                int bone = map[t];
                if (bone < 0 || bone >= boneCount) continue;
                Qs track = tracks[t];
                Qs local = locals[bone];
                if (track.HasT) { local.Tx = track.Tx; local.Ty = track.Ty; local.Tz = track.Tz; }
                if (track.HasR) { local.Rx = track.Rx; local.Ry = track.Ry; local.Rz = track.Rz; local.Rw = track.Rw; }
                if (track.HasS) { local.Sx = track.Sx; local.Sy = track.Sy; local.Sz = track.Sz; }
            }
            return locals;
        }

        private static void ConcatenateWorld(SkeletonData skeleton, Qs[] locals, float[] world,
            ref float minX, ref float minY, ref float minZ, ref float maxX, ref float maxY, ref float maxZ, ref bool hasBounds)
        {
            var worldQs = new Qs[skeleton.Names.Count];
            ConcatenateWorldQs(skeleton, locals, worldQs, world,
                ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ, ref hasBounds);
        }

        private static void ConcatenateWorldQs(SkeletonData skeleton, Qs[] locals, Qs[] worldQs, float[] world,
            ref float minX, ref float minY, ref float minZ, ref float maxX, ref float maxY, ref float maxZ, ref bool hasBounds)
        {
            int count = skeleton.Names.Count;
            for (int i = 0; i < count; i++)
            {
                int parent = i < skeleton.Parents.Count ? skeleton.Parents[i] : -1;
                if (parent < 0 || parent >= count)
                    worldQs[i] = CloneQs(locals[i]);
                else
                    worldQs[i] = MulQs(worldQs[parent], locals[i]);

                float x = worldQs[i].Tx;
                float y = worldQs[i].Ty;
                float z = worldQs[i].Tz;
                world[i * 3] = x;
                world[i * 3 + 1] = y;
                world[i * 3 + 2] = z;
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
        }

        private static Qs MulQs(Qs a, Qs b)
        {
            float[] ra = { a.Rx, a.Ry, a.Rz, a.Rw };
            float[] rb = { b.Rx, b.Ry, b.Rz, b.Rw };
            float[] r = MulQuat(ra, rb);
            float sx = b.Tx * a.Sx;
            float sy = b.Ty * a.Sy;
            float sz = b.Tz * a.Sz;
            RotateVec(ra, ref sx, ref sy, ref sz);
            return new Qs
            {
                Tx = a.Tx + sx,
                Ty = a.Ty + sy,
                Tz = a.Tz + sz,
                Rx = r[0], Ry = r[1], Rz = r[2], Rw = r[3],
                Sx = a.Sx * b.Sx,
                Sy = a.Sy * b.Sy,
                Sz = a.Sz * b.Sz,
                HasT = true, HasR = true, HasS = true
            };
        }

        private static float[] MulQuat(float[] a, float[] b)
        {
            return NormalizeQuat(new[]
            {
                a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
                a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
                a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]
            });
        }

        private static void RotateVec(float[] q, ref float x, ref float y, ref float z)
        {
            float qx = q[0], qy = q[1], qz = q[2], qw = q[3];
            float ix = qw * x + qy * z - qz * y;
            float iy = qw * y + qz * x - qx * z;
            float iz = qw * z + qx * y - qy * x;
            float iw = -qx * x - qy * y - qz * z;
            x = ix * qw + iw * -qx + iy * -qz - iz * -qy;
            y = iy * qw + iw * -qy + iz * -qx - ix * -qz;
            z = iz * qw + iw * -qz + ix * -qy - iy * -qx;
        }

        private static Qs CloneQs(Qs src) => new Qs
        {
            Tx = src.Tx, Ty = src.Ty, Tz = src.Tz,
            Rx = src.Rx, Ry = src.Ry, Rz = src.Rz, Rw = src.Rw,
            Sx = src.Sx, Sy = src.Sy, Sz = src.Sz,
            HasT = src.HasT, HasR = src.HasR, HasS = src.HasS
        };

        private static int FindKnotSpan(int degree, float value, int numCp, float[] knots)
        {
            if (numCp <= 0) return 0;
            if (value >= knots[numCp]) return numCp - 1;
            int low = degree, high = numCp;
            int mid = (low + high) / 2;
            for (int guard = 0; guard < 100; guard++)
            {
                if (value < knots[mid]) high = mid;
                else if (value >= knots[mid + 1]) low = mid;
                else break;
                mid = (low + high) / 2;
            }
            return mid;
        }

        private static float EvalBSpline(int knotSpan, int degree, float t, float[] knots, List<float> controlPoints, List<float[]> unused)
        {
            if (controlPoints.Count == 0) return 0;
            if (controlPoints.Count == 1) return controlPoints[0];
            float[] N = Basis(knotSpan, degree, t, knots);
            float result = 0;
            for (int i = 0; i <= degree; i++)
            {
                int idx = knotSpan - i;
                if (idx >= 0 && idx < controlPoints.Count) result += controlPoints[idx] * N[i];
            }
            return result;
        }

        private static float[] EvalBSplineVec(int knotSpan, int degree, float t, float[] knots, List<float[]> controlPoints)
        {
            if (controlPoints.Count == 0) return new[] { 0f, 0f, 0f, 1f };
            if (controlPoints.Count == 1) return (float[])controlPoints[0].Clone();
            float[] N = Basis(knotSpan, degree, t, knots);
            float[] result = new float[4];
            for (int i = 0; i <= degree; i++)
            {
                int idx = knotSpan - i;
                if (idx < 0 || idx >= controlPoints.Count) continue;
                float[] cp = controlPoints[idx];
                for (int d = 0; d < 4; d++) result[d] += cp[d] * N[i];
            }
            return result;
        }

        private static float[] Basis(int knotSpan, int degree, float t, float[] knots)
        {
            float[] N = new float[degree + 1];
            N[0] = 1f;
            for (int i = 1; i <= degree; i++)
            {
                for (int j = i - 1; j >= 0; j--)
                {
                    float denom = knots[knotSpan + i - j] - knots[knotSpan - j];
                    float A = denom >= 1e-10f ? (t - knots[knotSpan - j]) / denom : 0f;
                    float tmp = N[j] * A;
                    if (j + 1 < N.Length) N[j + 1] += N[j] - tmp;
                    N[j] = tmp;
                }
            }
            return N;
        }

        private static float Read8(byte[] data, ref int off, float mn, float mx)
        {
            float val = mn + (mx - mn) * (data[off] / 255f);
            off += 1;
            return val;
        }

        private static float Read16(byte[] data, ref int off, float mn, float mx)
        {
            float val = mn + (mx - mn) * (BitConverter.ToUInt16(data, off) / 65535f);
            off += 2;
            return val;
        }

        private static int QuatAlign(int fmt) => fmt switch { 0 => 4, 1 => 1, 2 => 2, 5 => 4, _ => 4 };

        private static float[] ReadQuat(int fmt, byte[] data, ref int off)
        {
            return fmt switch
            {
                0 => Read32Quat(data, ref off),
                1 => Read40Quat(data, ref off),
                2 => Read48Quat(data, ref off),
                5 => ReadUncompressedQuat(data, ref off),
                _ => Read40Quat(data, ref off)
            };
        }

        private static float[] Read32Quat(byte[] data, ref int off)
        {
            uint cval = U32(data, off); off += 4;
            const int rMask = (1 << 10) - 1;
            float rFrac = 1f / rMask;
            float R = ((cval >> 18) & rMask) * rFrac;
            R = 1f - R * R;
            float phiTheta = cval & 0x3FFFFu;
            float phi = (float)Math.Floor(Math.Sqrt(phiTheta));
            float theta = 0f;
            if (phi > 0f) theta = (float)(Math.PI / 4.0) * (phiTheta - phi * phi) / phi;
            phi = (float)(Math.PI / 2.0 / 511.0) * phi;
            float magnitude = (float)Math.Sqrt(Math.Max(0, 1.0 - R * R));
            float sp = (float)Math.Sin(phi), cp = (float)Math.Cos(phi);
            float st = (float)Math.Sin(theta), ct = (float)Math.Cos(theta);
            float[] result = { sp * ct * magnitude, sp * st * magnitude, cp * magnitude, R };
            uint[] signs = { 0x10000000, 0x20000000, 0x40000000, 0x80000000 };
            for (int i = 0; i < 4; i++) if ((cval & signs[i]) != 0) result[i] = -result[i];
            return NormalizeQuat(result);
        }

        private static float[] Read40Quat(byte[] data, ref int off)
        {
            const float fractal = 0.000345436f;
            ulong raw = 0;
            for (int i = 0; i < 5; i++) raw |= (ulong)data[off + i] << (8 * i);
            off += 5;
            int a = (int)((raw >> 0) & 0xFFF);
            int b = (int)((raw >> 12) & 0xFFF);
            int c = (int)((raw >> 24) & 0xFFF);
            float[] vals = { (a - 2049) * fractal, (b - 2049) * fractal, (c - 2049) * fractal };
            float w = (float)Math.Sqrt(Math.Max(0, 1.0 - (vals[0] * vals[0] + vals[1] * vals[1] + vals[2] * vals[2])));
            if (((raw >> 38) & 1) != 0) w = -w;
            int shift = (int)((raw >> 36) & 3);
            float[] result = shift switch
            {
                0 => new[] { w, vals[0], vals[1], vals[2] },
                1 => new[] { vals[0], w, vals[1], vals[2] },
                2 => new[] { vals[0], vals[1], w, vals[2] },
                _ => new[] { vals[0], vals[1], vals[2], w }
            };
            return NormalizeQuat(result);
        }

        private static float[] Read48Quat(byte[] data, ref int off)
        {
            const float fractal = 0.000043161f;
            const int mask = (1 << 15) - 1;
            const int half = mask >> 1;
            ushort xRaw = BitConverter.ToUInt16(data, off);
            ushort yRaw = BitConverter.ToUInt16(data, off + 2);
            ushort zRaw = BitConverter.ToUInt16(data, off + 4);
            off += 6;
            int shift = ((yRaw >> 14) & 2) | ((xRaw >> 15) & 1);
            bool rSign = (zRaw >> 15) != 0;
            float[] vals =
            {
                ((xRaw & mask) - half) * fractal,
                ((yRaw & mask) - half) * fractal,
                ((zRaw & mask) - half) * fractal
            };
            float w = (float)Math.Sqrt(Math.Max(0, 1.0 - (vals[0] * vals[0] + vals[1] * vals[1] + vals[2] * vals[2])));
            if (rSign) w = -w;
            float[] result = shift switch
            {
                0 => new[] { w, vals[0], vals[1], vals[2] },
                1 => new[] { vals[0], w, vals[1], vals[2] },
                2 => new[] { vals[0], vals[1], w, vals[2] },
                _ => new[] { vals[0], vals[1], vals[2], w }
            };
            return NormalizeQuat(result);
        }

        private static float[] ReadUncompressedQuat(byte[] data, ref int off)
        {
            float[] q = { F32(data, off), F32(data, off + 4), F32(data, off + 8), F32(data, off + 12) };
            off += 16;
            return NormalizeQuat(q);
        }

        private static float[] NormalizeQuat(float[] q)
        {
            float mag = (float)Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (mag < 1e-10f) return new[] { 0f, 0f, 0f, 1f };
            return new[] { q[0] / mag, q[1] / mag, q[2] / mag, q[3] / mag };
        }

        private static int ArrayCount(Packfile pack, int arrRel)
        {
            int abs = pack.DataAbs + arrRel;
            if (abs + 12 > pack.Data.Length) return 0;
            return (int)U32(pack.Data, abs + 8);
        }

        private static int Fixup(Packfile pack, int srcRel) =>
            pack.Fixups.TryGetValue(srcRel, out int dst) ? dst : -1;

        private static List<uint> ReadArrayU32(Packfile pack, int objRel, int fieldOff)
        {
            var list = new List<uint>();
            int arrRel = objRel + fieldOff;
            int count = ArrayCount(pack, arrRel);
            int content = Fixup(pack, arrRel);
            if (content < 0 || count <= 0) return list;
            int abs = pack.DataAbs + content;
            for (int i = 0; i < count && abs + i * 4 + 4 <= pack.Data.Length; i++)
                list.Add(U32(pack.Data, abs + i * 4));
            return list;
        }

        private static byte[] ReadArrayU8(Packfile pack, int objRel, int fieldOff)
        {
            int arrRel = objRel + fieldOff;
            int count = ArrayCount(pack, arrRel);
            int content = Fixup(pack, arrRel);
            if (content < 0 || count <= 0) return Array.Empty<byte>();
            int abs = pack.DataAbs + content;
            if (abs < 0 || abs + count > pack.Data.Length) return Array.Empty<byte>();
            var bytes = new byte[count];
            Buffer.BlockCopy(pack.Data, abs, bytes, 0, count);
            return bytes;
        }

        private static List<Qs> ReadArrayQs(Packfile pack, int objRel, int fieldOff)
        {
            var list = new List<Qs>();
            int arrRel = objRel + fieldOff;
            int count = ArrayCount(pack, arrRel);
            int content = Fixup(pack, arrRel);
            if (content < 0 || count <= 0) return list;
            const int stride = 0x30;
            for (int i = 0; i < count; i++)
            {
                int p = pack.DataAbs + content + i * stride;
                if (p + stride > pack.Data.Length) break;
                list.Add(new Qs
                {
                    Tx = F32(pack.Data, p), Ty = F32(pack.Data, p + 4), Tz = F32(pack.Data, p + 8),
                    Rx = F32(pack.Data, p + 16), Ry = F32(pack.Data, p + 20), Rz = F32(pack.Data, p + 24), Rw = F32(pack.Data, p + 28),
                    Sx = F32(pack.Data, p + 32), Sy = F32(pack.Data, p + 36), Sz = F32(pack.Data, p + 40),
                    HasT = true, HasR = true, HasS = true
                });
            }
            return list;
        }

        private static int Align(int offset, int alignment)
        {
            int r = offset % alignment;
            return r == 0 ? offset : offset + (alignment - r);
        }

        private static uint U32(byte[] data, int off) => BitConverter.ToUInt32(data, off);
        private static float F32(byte[] data, int off) => BitConverter.ToSingle(data, off);

        private static string ReadAscii(byte[] data, int off, int max)
        {
            if (off < 0 || off >= data.Length) return "";
            int end = off;
            int limit = Math.Min(data.Length, off + max);
            while (end < limit && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, off, end - off);
        }
    }
}
