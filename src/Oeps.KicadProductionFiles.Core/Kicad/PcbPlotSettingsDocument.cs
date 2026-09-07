using System.Text;

namespace Oeps.KicadProductionFiles.Core.Kicad;

/// <summary>Reads and edits setup/pcbplotparams while retaining the rest of the PCB text exactly.</summary>
public sealed class PcbPlotSettingsDocument
{
    private readonly string _text;
    private readonly Node _root;
    private readonly Node? _setup;
    private readonly Node? _plot;

    public PcbPlotSettingsDocument(string text)
    {
        _text = text;
        var parser = new Parser(text);
        _root = parser.ReadNode(0, true);
        if (_root.Name != "kicad_pcb" || _root.Values.Count != 0 || parser.Next().Kind != 'e')
            throw new InvalidDataException("The main PCB must contain one kicad_pcb document.");
        _setup = SingleChild(_root, "setup");
        _plot = _setup is null ? null : SingleChild(_setup, "pcbplotparams");
        if (_setup?.Values.Count > 0 || _plot?.Values.Count > 0)
            throw new InvalidDataException("The PCB setup/pcbplotparams section is malformed.");
    }

    public string? ReadValue(string key, bool quoted = false)
    {
        var node = _plot is null ? null : SingleChild(_plot, key);
        if (node is null) return null;
        if (node.Children.Count != 0 || node.Values.Count != 1 || node.Values[0].Kind != (quoted ? 's' : 'a'))
            throw new InvalidDataException($"{key} must contain one {(quoted ? "quoted string" : "unquoted value")}.");
        var value = node.Values[0].Value;
        if (!quoted) return value;
        var decoded = new StringBuilder();
        for (var index = 1; index < value.Length - 1; index++)
        {
            var character = value[index];
            if (character == '\\')
            {
                character = value[++index];
                character = character switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => character };
            }
            decoded.Append(character);
        }
        return decoded.ToString();
    }

    public string WithValues(IReadOnlyDictionary<string, string> values, IReadOnlySet<string>? quotedKeys = null)
    {
        var edits = new List<(int Start, int Length, string Text)>();
        var missing = new List<string>();
        foreach (var (key, suppliedValue) in values)
        {
            var quoted = quotedKeys?.Contains(key) == true;
            if (key.Length == 0 || key.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '"')
                || (!quoted && (suppliedValue.Length == 0 || suppliedValue.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '"'))))
                throw new ArgumentException("Plot-setting names and values must be single atoms.");
            var value = quoted ? "\"" + suppliedValue.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"" : suppliedValue;
            var node = _plot is null ? null : SingleChild(_plot, key);
            if (node is null) { missing.Add($"({key} {value})"); continue; }
            if (node.Children.Count != 0 || node.Values.Count > 1)
                throw new InvalidDataException($"Cannot repair ambiguous or structured plot setting {key}.");
            if (node.Values.Count == 0) edits.Add((node.HeaderEnd, 0, " " + value));
            else
            {
                var current = node.Values[0];
                if (current.Kind != (quoted ? 's' : 'a') || current.Value != value)
                    edits.Add((current.Start, current.End - current.Start, value));
            }
        }
        if (missing.Count > 0)
        {
            var newline = _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var settings = string.Join(newline + "\t\t\t", missing);
            if (_plot is not null)
                edits.Add((_plot.HeaderEnd, 0, newline + "\t\t\t" + settings));
            else if (_setup is not null)
                edits.Add((_setup.HeaderEnd, 0, $"{newline}\t\t(pcbplotparams{newline}\t\t\t{settings}{newline}\t\t)"));
            else
            {
                var position = _root.Children.FirstOrDefault(node => node.Name == "footprint")?.Start ?? _root.CloseStart;
                edits.Add((position, 0, $"{newline}\t(setup{newline}\t\t(pcbplotparams{newline}\t\t\t{settings}{newline}\t\t){newline}\t){newline}"));
            }
        }
        var output = new StringBuilder(_text);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            output.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        return output.ToString();
    }

    private static Node? SingleChild(Node parent, string name)
    {
        var matches = parent.Children.Where(node => node.Name == name).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"The PCB contains duplicate {name} settings.");
        return matches.SingleOrDefault();
    }

    private sealed record Token(char Kind, int Start, int End, string Value);
    private sealed record Node(string Name, int Start, int HeaderEnd, int CloseStart, List<Token> Values, List<Node> Children);

    private sealed class Parser(string text)
    {
        private int _position;
        public Node ReadNode(int depth, bool capture, int start = -1)
        {
            if (depth > 128) throw new InvalidDataException("The PCB nesting is too deep.");
            if (start < 0)
            {
                var opening = Next();
                if (opening.Kind != '(') throw new InvalidDataException("Expected a PCB list.");
                start = opening.Start;
            }
            var name = Next();
            if (name.Kind != 'a') throw new InvalidDataException("A PCB list has no name.");
            var node = new Node(name.Value, start, name.End, -1, [], []);
            while (true)
            {
                var token = Next();
                if (token.Kind == ')') return node with { CloseStart = token.Start };
                if (token.Kind == 'e') throw new InvalidDataException("The PCB has an unclosed list.");
                if (token.Kind == '(')
                {
                    var child = ReadNode(depth + 1, capture && (depth == 0 || name.Value is "setup" or "pcbplotparams"), token.Start);
                    if (capture) node.Children.Add(child);
                }
                else if (capture) node.Values.Add(token);
            }
        }

        public Token Next()
        {
            while (_position < text.Length && (char.IsWhiteSpace(text[_position]) || (_position == 0 && text[_position] == '\uFEFF'))) _position++;
            var start = _position;
            if (_position == text.Length) return new('e', start, start, "");
            var character = text[_position++];
            if (character is '(' or ')') return new(character, start, _position, "");
            if (character == '"')
            {
                while (_position < text.Length)
                {
                    character = text[_position++];
                    if (character == '"') return new('s', start, _position, text[start.._position]);
                    if (character == '\\' && _position < text.Length) _position++;
                }
                throw new InvalidDataException("The PCB has an unclosed quoted string.");
            }
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not '(' and not ')' and not '"') _position++;
            return new('a', start, _position, text[start.._position]);
        }
    }
}
