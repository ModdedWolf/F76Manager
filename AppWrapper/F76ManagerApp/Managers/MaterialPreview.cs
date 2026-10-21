using System;
using System.Collections.Generic;
using System.IO;

namespace F76ManagerApp.Managers
{
    public static class MaterialPreview
    {
        public sealed class TextureSlot
        {
            public string Role { get; init; } = "texture";
            public string Path { get; init; } = "";
        }

        public sealed class Info
        {
            public string Signature { get; init; } = "";
            public uint Version { get; init; }
            public string Template { get; init; }
            public List<TextureSlot> Textures { get; init; } = new List<TextureSlot>();
        }

        public static bool TryParse(byte[] data, out Info info)
        {
            info = null;
            if (data == null || data.Length < 8) return false;
            string signature = System.Text.Encoding.ASCII.GetString(data, 0, 4);
            if (signature != "BGSM" && signature != "BGEM") return false;
            uint version = BitConverter.ToUInt32(data, 4);

            string template = null;
            var textures = new List<TextureSlot>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string text in ScanStrings(data, 8, data.Length))
            {
                string lower = text.ToLowerInvariant();
                if (lower.EndsWith(".bgsm", StringComparison.Ordinal) || lower.EndsWith(".bgem", StringComparison.Ordinal))
                {
                    if (template == null) template = text;
                }
                else if (lower.EndsWith(".dds", StringComparison.Ordinal) && seen.Add(lower) && textures.Count < 12)
                {
                    textures.Add(new TextureSlot { Role = RoleFor(text), Path = text });
                }
            }

            info = new Info
            {
                Signature = signature,
                Version = version,
                Template = template,
                Textures = textures
            };
            return true;
        }

        public static List<string> ScanStrings(byte[] data, int start, int end)
        {
            var found = new List<string>();
            if (data == null || start < 0) return found;
            if (end > data.Length) end = data.Length;
            for (int i = start; i + 4 < end; i++)
            {
                int length = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24);
                if (length < 2 || length > 512) continue;
                if (i + 4 + length > end) continue;
                if (data[i + 4 + length - 1] != 0) continue;

                bool printable = true;
                for (int c = 0; c < length - 1; c++)
                {
                    byte b = data[i + 4 + c];
                    if (b < 32 || b > 126) { printable = false; break; }
                }
                if (!printable) continue;

                found.Add(System.Text.Encoding.ASCII.GetString(data, i + 4, length - 1).Replace('\\', '/'));
                i += 3 + length;
            }
            return found;
        }

        public static string RoleFor(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant();
            if (name.EndsWith("_d")) return "diffuse";
            if (name.EndsWith("_n")) return "normal";
            if (name.EndsWith("_s") || name.EndsWith("_r")) return "smooth";
            if (name.EndsWith("_g")) return "glow";
            if (name.EndsWith("_l")) return "lighting";
            if (name.EndsWith("_e")) return "env";
            return "texture";
        }

        public static string FindDiffusePath(byte[] materialBytes)
        {
            if (!TryParse(materialBytes, out Info info)) return null;
            foreach (var slot in info.Textures)
            {
                if (slot.Role == "diffuse") return slot.Path;
            }
            return info.Textures.Count > 0 ? info.Textures[0].Path : null;
        }
    }
}
