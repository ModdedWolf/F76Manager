using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F76ManagerApp.Managers;

public static class JsoncText
{
    public sealed record Addition(IReadOnlyList<object> Path, string? Key, JsonNode? Value, bool Replace = false);

    private static readonly JsonSerializerOptions ValueWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private enum TokenType { String, Comment, Punct, Literal }

    private readonly record struct Token(TokenType Type, int Start, int End);

    private enum NodeKind { Object, Array, Value }

    private sealed class Node
    {
        public NodeKind Kind;
        public int Start;
        public int End;
        public int CloseStart = -1;
        public List<Entry> Children = new();
    }

    private sealed class Entry
    {
        public string? Key;
        public int KeyStart = -1;
        public int KeyEnd = -1;
        public int Start;
        public int End;
        public int Comma = -1;
        public Node Value = null!;
    }

    private const string Punct = "{}[],:";

    public static bool ContainsComments(string? text) =>
        !string.IsNullOrEmpty(text) && Scan(text).Any(t => t.Type == TokenType.Comment);

    public static string? ApplyAdditions(string text, IEnumerable<Addition> additions)
    {
        string current = text ?? "";
        foreach (var add in additions)
        {
            var root = ParseTree(current);
            if (root == null) return null;
            var container = FindNode(root, add.Path);
            if (container == null || container.Kind == NodeKind.Value) return null;
            if (container.Kind == NodeKind.Object && add.Key == null) return null;
            if (add.Replace)
            {
                var entry = container.Children.LastOrDefault(e => e.Key == add.Key);
                if (entry == null) return null;
                string eol = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                string serialized = SerializeValue(add.Value, IndentAt(current, entry.Value.Start), DetectIndentUnit(current), eol);
                current = current.Substring(0, entry.Value.Start) + serialized + current.Substring(entry.Value.End);
                continue;
            }
            current = Append(current, container, add.Key, add.Value);
        }
        return current;
    }

    private static List<Token> Scan(string src)
    {
        var tokens = new List<Token>();
        int n = src.Length;
        int i = 0;
        while (i < n)
        {
            char c = src[i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\uFEFF')
            {
                i++;
                continue;
            }
            int start = i;
            if (c == '"')
            {
                i++;
                while (i < n && src[i] != '"' && src[i] != '\n') i += src[i] == '\\' ? 2 : 1;
                if (i < n && src[i] == '"') i++;
                tokens.Add(new Token(TokenType.String, start, Math.Min(i, n)));
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n' && src[i] != '\r') i++;
                tokens.Add(new Token(TokenType.Comment, start, i));
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int close = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                tokens.Add(new Token(TokenType.Comment, start, i));
                continue;
            }
            if (Punct.IndexOf(c) >= 0)
            {
                i++;
                tokens.Add(new Token(TokenType.Punct, start, i));
                continue;
            }
            while (i < n && Punct.IndexOf(src[i]) < 0 && " \t\n\r\"/".IndexOf(src[i]) < 0) i++;
            if (i == start) i++;
            tokens.Add(new Token(TokenType.Literal, start, i));
        }
        return tokens;
    }

    private static Node? ParseTree(string src)
    {
        var all = Scan(src);
        var sig = all.Where(t => t.Type != TokenType.Comment).ToList();
        int k = 0;

        char Ch(Token t) => src[t.Start];

        Node ParseValue()
        {
            if (k >= sig.Count) throw new FormatException("Unexpected end of input");
            var t = sig[k];
            if (t.Type == TokenType.Punct && Ch(t) == '{') return ParseContainer(NodeKind.Object, '}');
            if (t.Type == TokenType.Punct && Ch(t) == '[') return ParseContainer(NodeKind.Array, ']');
            if (t.Type == TokenType.Punct) throw new FormatException($"Unexpected '{Ch(t)}' at {t.Start}");
            k++;
            return new Node { Kind = NodeKind.Value, Start = t.Start, End = t.End };
        }

        Node ParseContainer(NodeKind kind, char close)
        {
            var open = sig[k++];
            var node = new Node { Kind = kind, Start = open.Start };
            while (true)
            {
                if (k >= sig.Count) throw new FormatException("Unterminated container");
                var t = sig[k];
                if (t.Type == TokenType.Punct && Ch(t) == close)
                {
                    node.CloseStart = t.Start;
                    node.End = t.End;
                    k++;
                    return node;
                }
                var entry = new Entry { Start = t.Start };
                if (kind == NodeKind.Object)
                {
                    if (t.Type != TokenType.String) throw new FormatException($"Expected property name at {t.Start}");
                    entry.KeyStart = t.Start;
                    entry.KeyEnd = t.End;
                    entry.Key = JsonSerializer.Deserialize<string>(src.AsSpan(t.Start, t.End - t.Start));
                    k++;
                    if (k >= sig.Count || Ch(sig[k]) != ':') throw new FormatException("Expected ':'");
                    k++;
                }
                entry.Value = ParseValue();
                entry.End = entry.Value.End;
                node.Children.Add(entry);
                if (k < sig.Count && sig[k].Type == TokenType.Punct && Ch(sig[k]) == ',')
                {
                    entry.Comma = sig[k].Start;
                    k++;
                }
                else if (k >= sig.Count || Ch(sig[k]) != close)
                {
                    throw new FormatException($"Expected ',' or '{close}'");
                }
            }
        }

        try
        {
            var root = ParseValue();
            return k == sig.Count ? root : null;
        }
        catch
        {
            return null;
        }
    }

    private static Node? FindNode(Node root, IReadOnlyList<object> path)
    {
        Node node = root;
        foreach (var seg in path)
        {
            if (node.Kind == NodeKind.Value) return null;
            Entry? match = null;
            if (node.Kind == NodeKind.Array)
            {
                if (seg is int idx && idx >= 0 && idx < node.Children.Count) match = node.Children[idx];
            }
            else if (seg is string key)
            {
                for (int j = node.Children.Count - 1; j >= 0; j--)
                {
                    if (node.Children[j].Key == key) { match = node.Children[j]; break; }
                }
            }
            if (match == null) return null;
            node = match.Value;
        }
        return node;
    }

    private static string Append(string src, Node container, string? key, JsonNode? value)
    {
        string eol = src.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string unit = DetectIndentUnit(src);
        var kids = container.Children;
        bool isObject = container.Kind == NodeKind.Object;

        string sep = ": ";
        if (isObject && kids.Count > 0)
        {
            var k0 = kids[0];
            string between = src.Substring(k0.KeyEnd, k0.Value.Start - k0.KeyEnd);
            int nl = between.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) between = between.Substring(0, nl);
            if (between.Contains(':')) sep = between;
        }

        bool lineMode = kids.Count == 0 || StartsLine(src, kids[0].Start);
        string childIndent = kids.Count > 0 && lineMode
            ? IndentAt(src, kids[0].Start)
            : IndentAt(src, container.Start) + unit;
        string item = (isObject ? JsonSerializer.Serialize(key, ValueWriteOptions) + sep : "")
                      + SerializeValue(value, childIndent, unit, eol);

        var edits = new List<(int Pos, string Text)>();
        if (kids.Count == 0)
        {
            string inner = src.Substring(container.Start + 1, container.CloseStart - container.Start - 1);
            if (string.IsNullOrWhiteSpace(inner))
            {
                return src.Substring(0, container.Start + 1)
                       + eol + childIndent + item + eol + IndentAt(src, container.Start)
                       + src.Substring(container.CloseStart);
            }
            return src.Insert(container.Start + 1, eol + childIndent + item);
        }

        var last = kids[^1];
        bool trailingStyle = last.Comma >= 0;
        int anchor = last.Comma >= 0 ? last.Comma + 1 : last.End;
        string tail = trailingStyle ? "," : "";

        if (lineMode)
        {
            int pos = TrailingCommentEnd(src, anchor);
            edits.Add((pos, eol + childIndent + item + tail));
        }
        else
        {
            edits.Add((anchor, " " + item + tail));
        }
        if (last.Comma < 0) edits.Add((last.End, ","));

        var sb = new StringBuilder(src);
        foreach (var (pos, text) in edits.OrderByDescending(e => e.Pos))
            sb.Insert(pos, text);
        return sb.ToString();
    }

    private static string SerializeValue(JsonNode? value, string baseIndent, string unit, string eol)
    {
        string json = value == null ? "null" : value.ToJsonString(ValueWriteOptions);
        var lines = json.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int spaces = 0;
            while (spaces < line.Length && line[spaces] == ' ') spaces++;
            sb.Append(eol).Append(baseIndent);
            for (int d = 0; d < spaces / 2; d++) sb.Append(unit);
            sb.Append(line, spaces, line.Length - spaces);
        }
        return sb.ToString();
    }

    private static int LineStartOf(string src, int pos)
    {
        int i = pos;
        while (i > 0 && src[i - 1] != '\n' && src[i - 1] != '\r') i--;
        return i;
    }

    private static int LineEndOf(string src, int pos)
    {
        int i = pos;
        while (i < src.Length && src[i] != '\n' && src[i] != '\r') i++;
        return i;
    }

    private static string IndentAt(string src, int pos)
    {
        int ls = LineStartOf(src, pos);
        int i = ls;
        while (i < src.Length && (src[i] == ' ' || src[i] == '\t')) i++;
        return src.Substring(ls, i - ls);
    }

    private static bool StartsLine(string src, int pos)
    {
        for (int i = LineStartOf(src, pos); i < pos; i++)
        {
            if (src[i] != ' ' && src[i] != '\t') return false;
        }
        return true;
    }

    private static bool IsBlank(string src, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (src[i] != ' ' && src[i] != '\t') return false;
        }
        return true;
    }

    private static int TrailingCommentEnd(string src, int pos)
    {
        int le = LineEndOf(src, pos);
        int end = pos;
        foreach (var t in Scan(src))
        {
            if (t.Start < end) continue;
            if (t.Type != TokenType.Comment || t.Start >= le || !IsBlank(src, end, t.Start) || t.End > le) break;
            end = t.End;
        }
        return end;
    }

    private static string DetectIndentUnit(string src)
    {
        string? best = null;
        foreach (var line in src.Replace("\r\n", "\n").Split('\n'))
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i == 0 || i >= line.Length || line[i] == '*') continue;
            if (line[0] == '\t') return "\t";
            if (best == null || i < best.Length) best = line.Substring(0, i);
        }
        return best ?? "  ";
    }
}
