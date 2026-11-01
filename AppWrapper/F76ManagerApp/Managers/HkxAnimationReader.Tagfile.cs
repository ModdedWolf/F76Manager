using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace F76ManagerApp.Managers
{
    public static partial class HkxAnimationReader
    {
        private static bool IsTagfile(byte[] file)
        {
            if (file == null || file.Length < 8) return false;
            if (file[0] == (byte)'T' && file[1] == (byte)'A' && file[2] == (byte)'G' && file[3] == (byte)'0')
                return true;
            return file.Length >= 12
                && file[4] == (byte)'T' && file[5] == (byte)'A' && file[6] == (byte)'G' && file[7] == (byte)'0';
        }

        private static Result ReadTagfile(byte[] file, string entryPath, string context, Action<string> logger, bool withMatrices = false)
        {
            bool hasBehavior = ContainsAscii(file, "hkbBehaviorGraph");
            bool hasSpline = ContainsAscii(file, "hkaSplineCompressedAnimation");
            bool hasInterleaved = ContainsAscii(file, "hkaInterleavedUncompressedAnimation");
            bool hasCharacter = ContainsAscii(file, "hkbCharacterData")
                || ContainsAscii(file, "hkbCharacterStringData");
            bool hasProject = ContainsAscii(file, "hkbProjectData")
                || ContainsAscii(file, "hkbProjectStringData");
            if (hasBehavior && !hasSpline && !hasInterleaved)
                return Fail(Failure.BehaviorGraph, "Behavior graph.");
            if (hasCharacter && !hasSpline && !hasInterleaved && !hasBehavior)
                return Fail(Failure.CharacterSetup, "Character setup.");
            if (hasProject && !hasSpline && !hasInterleaved && !hasBehavior && !hasCharacter)
                return Fail(Failure.ProjectData, "Project data.");

            if (!TryParseTagfile(file, out TagDoc doc, out string parseError))
            {
                if (hasBehavior) return Fail(Failure.BehaviorGraph, "Behavior graph.");
                if (hasCharacter) return Fail(Failure.CharacterSetup, "Character setup.");
                if (hasProject) return Fail(Failure.ProjectData, "Project data.");
                logger?.Invoke($"[BUNDLE] HKX tagfile parse failed: {parseError}");
                return Fail(Failure.UnsupportedVersion, "TAG0");
            }

            if (doc.HasBehavior && !doc.HasSpline && !doc.HasInterleaved)
                return Fail(Failure.BehaviorGraph, "Behavior graph.");

            SkeletonData skeleton = TryExtractTagfileSkeleton(doc);
            if (!TryExtractTagfileClip(doc, out ClipData clip))
            {
                if (doc.HasBehavior) return Fail(Failure.BehaviorGraph, "Behavior graph.");
                if (skeleton != null && skeleton.Names.Count > 0)
                    return BuildRestPosePreview(skeleton, withMatrices);
                if (doc.HasCharacter) return Fail(Failure.CharacterSetup, "Character setup.");
                if (doc.HasProject) return Fail(Failure.ProjectData, "Project data.");
                return Fail(Failure.NotClip, "Not a playable animation.");
            }

            if (skeleton == null || skeleton.Names.Count == 0)
                skeleton = ResolveExternalSkeleton(entryPath, context, logger);

            return BuildPreview(clip, skeleton, withMatrices);
        }

        private static SkeletonData TryReadTagfileSkeleton(byte[] file, Action<string> logger)
        {
            if (!TryParseTagfile(file, out TagDoc doc, out string error))
            {
                logger?.Invoke($"[BUNDLE] Tagfile skeleton parse failed: {error}");
                return null;
            }
            return TryExtractTagfileSkeleton(doc);
        }

        private static bool ContainsAscii(byte[] data, string text)
        {
            if (string.IsNullOrEmpty(text) || data == null || data.Length < text.Length) return false;
            byte[] needle = Encoding.ASCII.GetBytes(text);
            for (int i = 0; i <= data.Length - needle.Length; i++)
            {
                int j = 0;
                for (; j < needle.Length; j++)
                    if (data[i + j] != needle[j]) break;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private sealed class TagDoc
        {
            public TagObject Root;
            public bool HasBehavior;
            public bool HasSpline;
            public bool HasInterleaved;
            public bool HasCharacter;
            public bool HasProject;
        }

        private sealed class TagType
        {
            public string Name = "";
            public TagType Parent;
            public int Flags = 1;
            public int SubTypeFlags;
            public TagType Pointer;
            public int Version;
            public int ByteSize;
            public int Alignment;
            public int AbstractValue;
            public readonly List<TagMember> Members = new List<TagMember>();
            public readonly List<TagTemplate> Templates = new List<TagTemplate>();

            public TagType SuperType
            {
                get
                {
                    TagType t = this;
                    while (t != null && (t.Flags & TagFlag.SubType) == 0 && t.Parent != null)
                        t = t.Parent;
                    return t ?? this;
                }
            }

            public int SubType => SubTypeFlags & TagSubType.TypeMask;

            public IEnumerable<TagMember> AllMembers
            {
                get
                {
                    if (Parent != null)
                    {
                        foreach (TagMember m in Parent.AllMembers)
                            yield return m;
                    }
                    foreach (TagMember m in Members)
                        yield return m;
                }
            }

            public int TupleSize => SubTypeFlags >> 8;
        }

        private sealed class TagMember
        {
            public string Name = "";
            public int Flags;
            public int ByteOffset;
            public TagType Type;
        }

        private sealed class TagTemplate
        {
            public string Name = "";
            public int Value;
            public TagType TypeValue;
            public bool IsType => Name.Length > 0 && Name[0] == 't';
        }

        private sealed class TagObject
        {
            public object Value;
            public TagType Type;
        }

        private sealed class TagItem
        {
            public TagType Type;
            public int Offset;
            public int Count;
            public bool IsPtr;
            public List<TagObject> Value;
        }

        private static class TagSubType
        {
            public const int Void = 0x0;
            public const int Invalid = 0x1;
            public const int Bool = 0x2;
            public const int String = 0x3;
            public const int Int = 0x4;
            public const int Float = 0x5;
            public const int Pointer = 0x6;
            public const int Class = 0x7;
            public const int Array = 0x8;
            public const int Tuple = 0x28;
            public const int TypeMask = 0xff;
            public const int IsSigned = 0x200;
            public const int Int8 = 0x2000;
            public const int Int16 = 0x4000;
            public const int Int32 = 0x8000;
            public const int Int64 = 0x10000;
        }

        private static class TagFlag
        {
            public const int SubType = 0x1;
            public const int Pointer = 0x2;
            public const int Version = 0x4;
            public const int ByteSize = 0x8;
            public const int AbstractValue = 0x10;
            public const int Members = 0x20;
            public const int Interfaces = 0x40;
            public const int Unknown = 0x80;
        }

        private sealed class TagParser
        {
            private readonly byte[] _data;
            private int _pos;
            private int _dataOffset;
            private readonly List<TagType> _types = new List<TagType>();
            private readonly List<TagItem> _items = new List<TagItem>();

            public TagParser(byte[] data) => _data = data ?? Array.Empty<byte>();

            public TagObject ParseRoot()
            {
                ReadRootSection();
                return GetObject(0);
            }

            private void ReadRootSection()
            {
                using (SectionGuard root = OpenSection("TAG0", "TCM0"))
                {
                    if (root.Signature != "TAG0")
                        throw new InvalidDataException("Not a TAG0 object file.");

                    using (SectionGuard sdkv = OpenSection("SDKV"))
                    {
                        string version = Encoding.ASCII.GetString(_data, sdkv.PayloadStart, Math.Min(8, sdkv.PayloadSize));
                        if (version != "20150100" && version != "20160100" && version != "20160200")
                            throw new InvalidDataException("Unsupported SDKV " + version);
                    }

                    using (SectionGuard data = OpenSection("DATA"))
                    {
                        _dataOffset = data.PayloadStart;
                    }

                    ReadTypeSection();
                    ReadIndexSection();
                }
            }

            private void ReadTypeSection()
            {
                using (SectionGuard typeSec = OpenSection("TYPE", "TCRF"))
                {
                    if (typeSec.Signature == "TCRF")
                        throw new InvalidDataException("Tagfile requires an external type compendium.");

                    using (OpenSection("TPTR")) { }

                    string[] typeStrings;
                    using (SectionGuard tstr = OpenSection("TSTR"))
                    {
                        typeStrings = SplitNullStrings(_data, tstr.PayloadStart, tstr.PayloadSize);
                    }

                    using (SectionGuard tnam = OpenSection("TNAM", "TNA1"))
                    {
                        int typeCount = ReadPacked();
                        _types.Clear();
                        for (int i = 0; i < typeCount; i++)
                            _types.Add(i == 0 ? null : new TagType());

                        for (int i = 1; i < typeCount; i++)
                        {
                            TagType typ = _types[i];
                            typ.Name = typeStrings[ReadPacked()];
                            int templateCount = ReadPacked();
                            for (int t = 0; t < templateCount; t++)
                            {
                                var tmpl = new TagTemplate
                                {
                                    Name = typeStrings[ReadPacked()],
                                    Value = ReadPacked()
                                };
                                if (tmpl.IsType && tmpl.Value >= 0 && tmpl.Value < _types.Count)
                                    tmpl.TypeValue = _types[tmpl.Value];
                                typ.Templates.Add(tmpl);
                            }
                        }
                    }

                    string[] fieldStrings;
                    using (SectionGuard fstr = OpenSection("FSTR"))
                    {
                        fieldStrings = SplitNullStrings(_data, fstr.PayloadStart, fstr.PayloadSize);
                    }

                    using (SectionGuard tbod = OpenSection("TBOD", "TBDY"))
                    {
                        while (_pos < tbod.End)
                        {
                            int typeIndex = ReadPacked();
                            if (typeIndex == 0) continue;
                            TagType typ = _types[typeIndex];
                            typ.Parent = _types[ReadPacked()];
                            typ.Flags = ReadPacked();

                            if ((typ.Flags & TagFlag.SubType) != 0)
                                typ.SubTypeFlags = ReadPacked();

                            if ((typ.Flags & TagFlag.Pointer) != 0 && (typ.SubTypeFlags & 0xF) >= 6)
                                typ.Pointer = _types[ReadPacked()];

                            if ((typ.Flags & TagFlag.Version) != 0)
                                typ.Version = ReadPacked();

                            if ((typ.Flags & TagFlag.ByteSize) != 0)
                            {
                                typ.ByteSize = ReadPacked();
                                typ.Alignment = ReadPacked();
                            }

                            if ((typ.Flags & TagFlag.AbstractValue) != 0)
                                typ.AbstractValue = ReadPacked();

                            if ((typ.Flags & TagFlag.Members) != 0)
                            {
                                int memberCount = ReadPacked();
                                for (int m = 0; m < memberCount; m++)
                                {
                                    typ.Members.Add(new TagMember
                                    {
                                        Name = fieldStrings[ReadPacked()],
                                        Flags = ReadPacked(),
                                        ByteOffset = ReadPacked(),
                                        Type = _types[ReadPacked()]
                                    });
                                }
                            }

                            if ((typ.Flags & TagFlag.Interfaces) != 0)
                            {
                                int n = ReadPacked();
                                for (int i = 0; i < n; i++)
                                {
                                    ReadPacked();
                                    ReadPacked();
                                }
                            }

                            if ((typ.Flags & TagFlag.Unknown) != 0)
                                throw new InvalidDataException("Unsupported type flag 0x80.");
                        }
                    }

                    using (SectionGuard thsh = OpenSection("THSH"))
                    {
                        int n = ReadPacked();
                        for (int i = 0; i < n; i++)
                        {
                            ReadPacked();
                            _pos += 4;
                        }
                    }

                    using (OpenSection("TPAD")) { }
                }
            }

            private void ReadIndexSection()
            {
                using (SectionGuard indx = OpenSection("INDX"))
                {
                    using (SectionGuard item = OpenSection("ITEM"))
                    {
                        while (_pos + 12 <= item.End)
                        {
                            uint flag = ReadU32Le();
                            var ti = new TagItem
                            {
                                Type = _types[(int)(flag & 0xFFFFFF)],
                                IsPtr = (flag & 0x10000000) != 0,
                                Offset = _dataOffset + (int)ReadU32Le(),
                                Count = (int)ReadU32Le()
                            };
                            _items.Add(ti);
                        }
                    }

                    using (OpenSection("PTCH")) { }
                }
            }

            private TagObject GetObject(int index)
            {
                TagItem item = _items[index + 1];
                if (item.Type == null) return null;
                EnsureItem(item);
                return item.Value.Count > 0 ? item.Value[0] : null;
            }

            private void EnsureItem(TagItem item)
            {
                if (item.Value != null) return;
                item.Value = new List<TagObject>(item.Count);
                int stride = item.Type.SuperType.ByteSize;
                for (int i = 0; i < item.Count; i++)
                    item.Value.Add(ReadObject(item.Type, item.Offset + i * stride));
            }

            private TagObject ReadObject(TagType typOrg, int offset)
            {
                TagType typ = typOrg.SuperType;
                object value = null;

                switch (typ.SubType)
                {
                    case TagSubType.Bool:
                        value = ReadFormatNumber(typ.SubTypeFlags, offset) != 0;
                        break;
                    case TagSubType.String:
                    {
                        List<TagObject> chars = ReadItemPtr(offset);
                        var sb = new StringBuilder();
                        for (int i = 0; i < chars.Count - 1; i++)
                            sb.Append((char)Convert.ToInt32(chars[i].Value));
                        value = sb.ToString();
                        break;
                    }
                    case TagSubType.Int:
                        value = ReadFormatNumber(typ.SubTypeFlags, offset);
                        break;
                    case TagSubType.Float:
                        value = BitConverter.ToSingle(_data, offset);
                        break;
                    case TagSubType.Pointer:
                    {
                        List<TagObject> pts = ReadItemPtr(offset);
                        value = pts.Count == 1 ? pts[0] : null;
                        break;
                    }
                    case TagSubType.Class:
                    {
                        var dict = new Dictionary<string, TagObject>(StringComparer.Ordinal);
                        foreach (TagMember member in typ.AllMembers)
                            dict[member.Name] = ReadObject(member.Type, offset + member.ByteOffset);
                        value = dict;
                        break;
                    }
                    case TagSubType.Array:
                        value = ReadItemPtr(offset);
                        break;
                    case TagSubType.Tuple:
                    {
                        var tuple = new List<TagObject>(typ.TupleSize);
                        int elemSize = typ.Pointer.SuperType.ByteSize;
                        for (int i = 0; i < typ.TupleSize; i++)
                            tuple.Add(ReadObject(typ.Pointer, offset + i * elemSize));
                        value = tuple;
                        break;
                    }
                }

                return new TagObject { Type = typOrg, Value = value };
            }

            private List<TagObject> ReadItemPtr(int offset)
            {
                int index = (int)BitConverter.ToUInt32(_data, offset);
                if (index == 0) return new List<TagObject>();
                TagItem item = _items[index];
                EnsureItem(item);
                return item.Value;
            }

            private long ReadFormatNumber(int flags, int offset)
            {
                bool signed = (flags & TagSubType.IsSigned) != 0;
                if ((flags & TagSubType.Int8) != 0)
                {
                    byte b = _data[offset];
                    return signed ? (long)(sbyte)b : b;
                }
                if ((flags & TagSubType.Int16) != 0)
                {
                    short v = BitConverter.ToInt16(_data, offset);
                    return signed ? v : (ushort)v;
                }
                if ((flags & TagSubType.Int32) != 0)
                {
                    int v = BitConverter.ToInt32(_data, offset);
                    return signed ? v : (uint)v;
                }
                if ((flags & TagSubType.Int64) != 0)
                    return BitConverter.ToInt64(_data, offset);
                return 0;
            }

            private int ReadPacked()
            {
                if (_pos + 5 <= _data.Length
                    && _data[_pos] == 0xE8
                    && _data[_pos + 1] == 0x7F
                    && _data[_pos + 2] == 0xFF
                    && _data[_pos + 3] == 0xFF
                    && _data[_pos + 4] == 0xFF)
                {
                    _pos += 5;
                    return unchecked((int)0xFFFFFFFFu);
                }

                int value = _data[_pos++];
                if ((value & 0x80) == 0) return value;
                if ((value & 0x40) == 0)
                    return ((value << 8) | _data[_pos++]) & 0x3FFF;
                if ((value & 0x20) == 0)
                {
                    int v = ((value << 16) | (_data[_pos] << 8) | _data[_pos + 1]) & 0x1FFFFF;
                    _pos += 2;
                    return v;
                }
                {
                    int v = ((value << 24) | (_data[_pos] << 16) | (_data[_pos + 1] << 8) | _data[_pos + 2]) & 0x1FFFFFFF;
                    _pos += 3;
                    return v;
                }
            }

            private uint ReadU32Le()
            {
                uint v = BitConverter.ToUInt32(_data, _pos);
                _pos += 4;
                return v;
            }

            private SectionGuard OpenSection(params string[] signatures)
            {
                if (_pos + 8 > _data.Length)
                    throw new InvalidDataException("Truncated tagfile section.");
                int sizeField = (_data[_pos] << 24) | (_data[_pos + 1] << 16) | (_data[_pos + 2] << 8) | _data[_pos + 3];
                int size = sizeField & 0x3FFFFFFF;
                string sig = Encoding.ASCII.GetString(_data, _pos + 4, 4);
                bool ok = false;
                foreach (string s in signatures)
                    if (sig == s) { ok = true; break; }
                if (!ok)
                    throw new InvalidDataException($"Expected {string.Join("/", signatures)}, got {sig}.");
                int start = _pos;
                int payload = _pos + 8;
                int end = start + size;
                _pos = payload;
                return new SectionGuard(this, sig, payload, end, size - 8);
            }

            private sealed class SectionGuard : IDisposable
            {
                private readonly TagParser _owner;
                public readonly string Signature;
                public readonly int PayloadStart;
                public readonly int End;
                public readonly int PayloadSize;

                public SectionGuard(TagParser owner, string signature, int payloadStart, int end, int payloadSize)
                {
                    _owner = owner;
                    Signature = signature;
                    PayloadStart = payloadStart;
                    End = end;
                    PayloadSize = payloadSize;
                }

                public void Dispose() => _owner._pos = End;
            }
        }

        private static string[] SplitNullStrings(byte[] data, int start, int size)
        {
            var list = new List<string>();
            int end = start + size;
            int i = start;
            while (i < end)
            {
                int s = i;
                while (i < end && data[i] != 0) i++;
                list.Add(Encoding.ASCII.GetString(data, s, i - s));
                i++;
            }
            return list.ToArray();
        }

        private static bool TryParseTagfile(byte[] file, out TagDoc doc, out string error)
        {
            doc = null;
            error = "";
            try
            {
                var parser = new TagParser(file);
                TagObject root = parser.ParseRoot();
                doc = new TagDoc { Root = root };
                ScanClasses(root, doc, new HashSet<TagObject>());
                return root != null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void ScanClasses(TagObject obj, TagDoc doc, HashSet<TagObject> seen)
        {
            obj = Resolve(obj);
            if (obj == null || !seen.Add(obj)) return;
            string name = obj.Type?.Name ?? "";
            if (name == "hkbBehaviorGraph") doc.HasBehavior = true;
            if (name == "hkaSplineCompressedAnimation") doc.HasSpline = true;
            if (name == "hkaInterleavedUncompressedAnimation") doc.HasInterleaved = true;
            if (name == "hkbCharacterData" || name == "hkbCharacterStringData") doc.HasCharacter = true;
            if (name == "hkbProjectData" || name == "hkbProjectStringData") doc.HasProject = true;

            if (obj.Value is Dictionary<string, TagObject> dict)
            {
                foreach (TagObject child in dict.Values)
                    ScanClasses(child, doc, seen);
            }
            else if (obj.Value is List<TagObject> list)
            {
                foreach (TagObject child in list)
                    ScanClasses(child, doc, seen);
            }
            else if (obj.Value is TagObject nested)
            {
                ScanClasses(nested, doc, seen);
            }
        }

        private static TagObject Resolve(TagObject obj)
        {
            while (obj?.Type != null)
            {
                TagType super = obj.Type.SuperType;
                if (super.SubType == TagSubType.Pointer)
                {
                    obj = obj.Value as TagObject;
                    continue;
                }
                string name = obj.Type.Name;
                if ((name == "hkRefVariant" || name == "hkRefPtr") && obj.Value is TagObject inner)
                {
                    obj = inner;
                    continue;
                }
                break;
            }
            return obj;
        }

        private static IEnumerable<TagObject> NamedVariants(TagDoc doc)
        {
            TagObject root = Resolve(doc.Root);
            if (root?.Value is not Dictionary<string, TagObject> dict) yield break;
            if (!dict.TryGetValue("namedVariants", out TagObject arrObj)) yield break;
            if (arrObj.Value is not List<TagObject> list) yield break;
            foreach (TagObject item in list)
                yield return Resolve(item);
        }

        private static TagObject FindAnimationContainer(TagDoc doc)
        {
            foreach (TagObject variant in NamedVariants(doc))
            {
                if (variant?.Value is not Dictionary<string, TagObject> nv) continue;
                TagObject resolved = null;
                if (nv.TryGetValue("variant", out TagObject v)) resolved = Resolve(v);
                if (resolved?.Type?.Name == "hkaAnimationContainer") return resolved;
                if (nv.TryGetValue("className", out TagObject cn) && cn.Value as string == "hkaAnimationContainer")
                    return resolved;
            }
            return null;
        }

        private static bool TryExtractTagfileClip(TagDoc doc, out ClipData clip)
        {
            clip = null;
            TagObject container = FindAnimationContainer(doc);
            if (container?.Value is not Dictionary<string, TagObject> cdict) return false;
            if (!cdict.TryGetValue("animations", out TagObject animsObj) || animsObj.Value is not List<TagObject> anims)
                return false;

            TagObject animObj = null;
            foreach (TagObject a in anims)
            {
                TagObject r = Resolve(a);
                string n = r?.Type?.Name ?? "";
                if (n == "hkaSplineCompressedAnimation" || n == "hkaInterleavedUncompressedAnimation")
                {
                    animObj = r;
                    break;
                }
            }
            if (animObj?.Value is not Dictionary<string, TagObject> anim) return false;

            string animType = animObj.Type.Name;
            if (animType == "hkaInterleavedUncompressedAnimation")
            {
                return false;
            }

            clip = new ClipData
            {
                Duration = GetFloat(anim, "duration"),
                NumTracks = GetInt(anim, "numberOfTransformTracks"),
                NumFrames = GetInt(anim, "numFrames"),
                NumBlocks = GetInt(anim, "numBlocks"),
                MaxFramesPerBlock = GetInt(anim, "maxFramesPerBlock"),
                MaskAndQuantSize = GetInt(anim, "maskAndQuantizationSize"),
                FrameDuration = GetFloat(anim, "frameDuration"),
                BlockOffsets = GetU32List(anim, "blockOffsets"),
                Data = GetU8Array(anim, "data")
            };
            if (clip.MaxFramesPerBlock <= 0) clip.MaxFramesPerBlock = 256;
            if (clip.NumTracks <= 0 || clip.NumFrames <= 0 || clip.NumBlocks <= 0) return false;
            if (clip.BlockOffsets.Count == 0 || clip.Data.Length == 0) return false;
            if (clip.MaskAndQuantSize <= 0) clip.MaskAndQuantSize = Align(4 * clip.NumTracks, 4);

            if (cdict.TryGetValue("bindings", out TagObject bindsObj) && bindsObj.Value is List<TagObject> binds && binds.Count > 0)
            {
                TagObject bind = Resolve(binds[0]);
                if (bind?.Value is Dictionary<string, TagObject> bdict
                    && bdict.TryGetValue("transformTrackToBoneIndices", out TagObject mapObj)
                    && mapObj.Value is List<TagObject> mapList)
                {
                    clip.TrackToBone = new List<short>(mapList.Count);
                    foreach (TagObject m in mapList)
                        clip.TrackToBone.Add(unchecked((short)Convert.ToInt32(m.Value)));
                }
            }

            if (anim.TryGetValue("annotationTracks", out TagObject annObj) && annObj.Value is List<TagObject> anns)
            {
                clip.TrackNames = new List<string>(anns.Count);
                foreach (TagObject ann in anns)
                {
                    TagObject a = Resolve(ann);
                    string name = "";
                    if (a?.Value is Dictionary<string, TagObject> adict
                        && adict.TryGetValue("trackName", out TagObject tn))
                        name = tn.Value as string ?? "";
                    clip.TrackNames.Add(name);
                }
            }

            return true;
        }

        private static SkeletonData TryExtractTagfileSkeleton(TagDoc doc)
        {
            TagObject container = FindAnimationContainer(doc);
            if (container?.Value is not Dictionary<string, TagObject> cdict) return null;
            if (!cdict.TryGetValue("skeletons", out TagObject skelsObj) || skelsObj.Value is not List<TagObject> skels || skels.Count == 0)
                return null;

            TagObject skelObj = Resolve(skels[0]);
            if (skelObj?.Value is not Dictionary<string, TagObject> skel) return null;

            var result = new SkeletonData
            {
                Name = GetString(skel, "name")
            };

            if (skel.TryGetValue("bones", out TagObject bonesObj) && bonesObj.Value is List<TagObject> bones)
            {
                foreach (TagObject boneObj in bones)
                {
                    TagObject b = Resolve(boneObj);
                    string name = "";
                    if (b?.Value is Dictionary<string, TagObject> bd && bd.TryGetValue("name", out TagObject bn))
                        name = bn.Value as string ?? "";
                    result.Names.Add(name);
                }
            }

            if (skel.TryGetValue("parentIndices", out TagObject parentsObj) && parentsObj.Value is List<TagObject> parents)
            {
                foreach (TagObject p in parents)
                    result.Parents.Add(Convert.ToInt32(p.Value));
            }

            if (skel.TryGetValue("referencePose", out TagObject poseObj) && poseObj.Value is List<TagObject> poses)
            {
                foreach (TagObject pose in poses)
                {
                    TagObject qsObj = Resolve(pose);
                    result.Reference.Add(ReadQs(qsObj));
                }
            }

            while (result.Parents.Count < result.Names.Count) result.Parents.Add(-1);
            while (result.Reference.Count < result.Names.Count)
                result.Reference.Add(new Qs { HasT = true, HasR = true, HasS = true });

            return result.Names.Count > 0 ? result : null;
        }

        private static Qs ReadQs(TagObject qsObj)
        {
            var qs = new Qs { HasT = true, HasR = true, HasS = true, Rw = 1f, Sx = 1f, Sy = 1f, Sz = 1f };
            if (qsObj?.Value is not Dictionary<string, TagObject> d) return qs;
            if (d.TryGetValue("translation", out TagObject t)) ApplyVec3(t, ref qs.Tx, ref qs.Ty, ref qs.Tz);
            if (d.TryGetValue("rotation", out TagObject r)) ApplyVec4(r, ref qs.Rx, ref qs.Ry, ref qs.Rz, ref qs.Rw);
            if (d.TryGetValue("scale", out TagObject s)) ApplyVec3(s, ref qs.Sx, ref qs.Sy, ref qs.Sz);
            return qs;
        }

        private static void ApplyVec3(TagObject obj, ref float x, ref float y, ref float z)
        {
            float[] v = ReadFloatTuple(obj);
            if (v == null || v.Length < 3) return;
            x = v[0]; y = v[1]; z = v[2];
        }

        private static void ApplyVec4(TagObject obj, ref float x, ref float y, ref float z, ref float w)
        {
            float[] v = ReadFloatTuple(obj);
            if (v == null || v.Length < 4) return;
            x = v[0]; y = v[1]; z = v[2]; w = v[3];
        }

        private static float[] ReadFloatTuple(TagObject obj)
        {
            obj = Resolve(obj);
            if (obj?.Value is List<TagObject> list)
            {
                var vals = new float[list.Count];
                for (int i = 0; i < list.Count; i++)
                    vals[i] = Convert.ToSingle(list[i].Value);
                return vals;
            }
            return null;
        }

        private static int GetInt(Dictionary<string, TagObject> dict, string key)
        {
            if (!dict.TryGetValue(key, out TagObject obj) || obj?.Value == null) return 0;
            return Convert.ToInt32(obj.Value);
        }

        private static float GetFloat(Dictionary<string, TagObject> dict, string key)
        {
            if (!dict.TryGetValue(key, out TagObject obj) || obj?.Value == null) return 0f;
            return Convert.ToSingle(obj.Value);
        }

        private static string GetString(Dictionary<string, TagObject> dict, string key)
        {
            if (!dict.TryGetValue(key, out TagObject obj)) return "";
            return obj.Value as string ?? "";
        }

        private static List<uint> GetU32List(Dictionary<string, TagObject> dict, string key)
        {
            var list = new List<uint>();
            if (!dict.TryGetValue(key, out TagObject obj) || obj.Value is not List<TagObject> items) return list;
            foreach (TagObject item in items)
                list.Add(unchecked((uint)Convert.ToInt64(item.Value)));
            return list;
        }

        private static byte[] GetU8Array(Dictionary<string, TagObject> dict, string key)
        {
            if (!dict.TryGetValue(key, out TagObject obj) || obj.Value is not List<TagObject> items)
                return Array.Empty<byte>();
            var bytes = new byte[items.Count];
            for (int i = 0; i < items.Count; i++)
                bytes[i] = unchecked((byte)Convert.ToInt32(items[i].Value));
            return bytes;
        }
    }
}
