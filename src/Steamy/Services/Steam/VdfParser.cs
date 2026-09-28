using System.Globalization;
using System.Text;

namespace Steamy.Services;

/// <summary>
/// A node of a Valve KeyValues (VDF/ACF) document. Only text is read: nothing is executed and
/// no file is written.
/// </summary>
public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _children = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }
    public string? Value { get; internal set; }
    public bool HasChildren => _children.Count > 0;
    public IReadOnlyCollection<VdfNode> Children => _children.Values;

    public VdfNode? this[string key] => _children.TryGetValue(key, out var node) ? node : null;

    public IEnumerable<VdfNode> Descendants()
    {
        foreach (var child in _children.Values)
        {
            yield return child;
            foreach (var descendant in child.Descendants()) yield return descendant;
        }
    }

    public string? GetString(string key)
    {
        var node = this[key];
        if (node is null) return null;
        if (node.Value is not null) return node.Value;
        // ACF files sometimes wrap the payload of a key in a block with a single child.
        return node.Children.Count == 1 ? node.Children.First().Value : null;
    }

    public long GetLong(string key, long fallback = 0)
    {
        var raw = GetString(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    public int GetInt(string key, int fallback = 0)
    {
        var value = GetLong(key, fallback);
        return value is > int.MaxValue or < int.MinValue ? fallback : (int)value;
    }

    internal void Add(VdfNode child) => _children[child.Key] = child;
}

/// <summary>
/// Tolerant KeyValues parser. Malformed input never throws; unreadable parts are skipped so a
/// half-written ACF file cannot break the library scan.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string? text)
    {
        var root = new VdfNode("__root__");
        if (string.IsNullOrWhiteSpace(text)) return root;

        var stack = new Stack<VdfNode>();
        stack.Push(root);
        var tokens = Tokenize(text);
        var index = 0;

        while (index < tokens.Count)
        {
            var token = tokens[index];

            if (token == "}")
            {
                if (stack.Count > 1) stack.Pop();
                index++;
                continue;
            }

            if (token == "{")
            {
                index++;
                continue;
            }

            if (index + 1 >= tokens.Count)
            {
                stack.Peek().Add(new VdfNode(token, string.Empty));
                break;
            }

            var next = tokens[index + 1];
            if (next == "{")
            {
                var block = new VdfNode(token);
                stack.Peek().Add(block);
                stack.Push(block);
                index += 2;
                continue;
            }

            stack.Peek().Add(new VdfNode(token, next == "}" ? string.Empty : next));
            index += 2;
        }

        return root;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            // // and /* */ comments are part of the format but carry no data.
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] is not ('\n' or '\r')) index++;
                continue;
            }

            if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/')) index++;
                index = Math.Min(text.Length, index + 2);
                continue;
            }

            if (character is '{' or '}')
            {
                tokens.Add(character.ToString());
                index++;
                continue;
            }

            if (character == '"')
            {
                index++;
                var builder = new StringBuilder();
                while (index < text.Length && text[index] != '"')
                {
                    if (text[index] == '\\' && index + 1 < text.Length)
                    {
                        var escaped = text[index + 1];
                        builder.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                        index += 2;
                        continue;
                    }

                    builder.Append(text[index]);
                    index++;
                }

                if (index < text.Length) index++; // closing quote
                tokens.Add(builder.ToString());
                continue;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}' or '"')) index++;
            if (index > start) tokens.Add(text[start..index]);
            else index++;
        }

        return tokens;
    }
}
