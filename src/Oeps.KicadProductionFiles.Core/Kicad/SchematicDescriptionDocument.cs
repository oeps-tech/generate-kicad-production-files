using System.Text;

namespace Oeps.KicadProductionFiles.Core.Kicad;

/// <summary>Reads saved sheet/symbol instances and changes only placed symbols' OEPS Description properties.</summary>
internal sealed class SchematicDescriptionDocument
{
    internal sealed record Token(char Kind, int Start, int End, string Value);
    internal sealed record Node(string Name, int Start, int End, List<Token> Values, List<Node> Children);
    internal sealed record Symbol(Node Node, string Uuid, int Unit, IReadOnlyDictionary<string, string> Fields,
        IReadOnlyList<(string Path, string Reference)> Instances, bool InBom);
    internal sealed record Sheet(string Uuid, string File);
    private readonly string _text;
    public string Uuid { get; }
    public IReadOnlyList<Symbol> Symbols { get; }
    public IReadOnlyList<Sheet> Sheets { get; }

    public SchematicDescriptionDocument(string text)
    {
        _text = text;
        var parser = new Parser(text);
        var root = parser.List(0);
        if (root.Name != "kicad_sch" || parser.Next().Kind != 'e') throw new InvalidDataException("Expected one kicad_sch document.");
        Uuid = Scalar(root, "uuid");
        var symbols = new List<Symbol>();
        foreach (var node in root.Children.Where(node => node.Name == "symbol"))
        {
            var fields = Properties(node).ToDictionary(property => property.Values[0].Value, property => property.Values[1].Value, StringComparer.Ordinal);
            var instances = node.Children.Where(child => child.Name == "instances")
                .SelectMany(child => child.Children.Where(child => child.Name == "project"))
                .SelectMany(child => child.Children.Where(child => child.Name == "path"))
                .Select(path => (Path: Value(path), Reference: Scalar(path, "reference"))).ToArray();
            if (!int.TryParse(Scalar(node, "unit"), out var unit) || unit < 1) throw new InvalidDataException("Invalid symbol unit.");
            symbols.Add(new(node, Scalar(node, "uuid"), unit, fields, instances, Scalar(node, "in_bom") == "yes"));
        }
        if (symbols.GroupBy(symbol => symbol.Uuid).Any(group => group.Count() > 1)) throw new InvalidDataException("Duplicate placed-symbol UUID.");
        Symbols = symbols;
        Sheets = root.Children.Where(node => node.Name == "sheet").Select(node =>
        {
            var file = Properties(node).Where(property => property.Values[0].Value is "Sheetfile" or "Sheet file").ToArray();
            if (file.Length != 1) throw new InvalidDataException("A hierarchical sheet must have one Sheetfile property.");
            return new Sheet(Scalar(node, "uuid"), file[0].Values[1].Value);
        }).ToArray();
        if (Sheets.GroupBy(sheet => sheet.Uuid).Any(group => group.Count() > 1)) throw new InvalidDataException("Duplicate sheet UUID.");
    }

    public string WithDescriptions(IReadOnlyDictionary<string, string> descriptions)
    {
        var edits = new List<(int Start, int Length, string Text)>();
        foreach (var symbol in Symbols.Where(symbol => descriptions.ContainsKey(symbol.Uuid)))
        {
            var description = descriptions[symbol.Uuid];
            var property = Properties(symbol.Node).SingleOrDefault(node => node.Values[0].Value == "OEPS Description");
            if (property is not null)
            {
                var token = property.Values[1];
                if (token.Value.Trim() != description) edits.Add((token.Start, token.End - token.Start, Quote(description)));
            }
            else
            {
                var position = symbol.Node.Children.SingleOrDefault(node => node.Name == "at");
                if (position is null || position.Values.Count < 2) throw new InvalidDataException("Cannot position a missing description on a symbol without coordinates.");
                var x = position.Values[0].Value;
                var y = position.Values[1].Value;
                if (!decimal.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
                    || !decimal.TryParse(y, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    throw new InvalidDataException("Invalid symbol coordinates.");
                var newline = _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                edits.Add((symbol.Node.End - 1, 0,
                    $"{newline}\t\t(property \"OEPS Description\" {Quote(description)} (at {x} {y} 0) (effects (font (size 1.27 1.27)) (hide yes))){newline}\t"));
            }
        }
        var output = new StringBuilder(_text);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) output.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        var modified = output.ToString();
        var verified = new SchematicDescriptionDocument(modified);
        foreach (var (uuid, expected) in descriptions)
            if (verified.Symbols.Single(symbol => symbol.Uuid == uuid).Fields.GetValueOrDefault("OEPS Description", "").Trim() != expected)
                throw new InvalidDataException("The prepared description could not be verified.");
        return modified;
    }

    private static Node[] Properties(Node node)
    {
        var properties = node.Children.Where(child => child.Name == "property").ToArray();
        if (properties.Any(property => property.Values.Count != 2 || property.Values.Any(value => value.Kind != 's'))
            || properties.GroupBy(property => property.Values[0].Value).Any(group => group.Count() > 1))
            throw new InvalidDataException("Malformed or duplicate schematic property; correct it in KiCad first.");
        return properties;
    }
    private static string Scalar(Node node, string key)
    {
        var children = node.Children.Where(child => child.Name == key).ToArray();
        if (children.Length != 1) throw new InvalidDataException($"Missing or duplicate schematic {key}.");
        return Value(children[0]);
    }
    private static string Value(Node node) => node.Values.Count == 1 && !string.IsNullOrWhiteSpace(node.Values[0].Value)
        ? node.Values[0].Value : throw new InvalidDataException($"Malformed schematic {node.Name}.");
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private sealed class Parser(string text)
    {
        private int _position;
        public Node List(int depth, Token? opening = null)
        {
            if (depth > 128) throw new InvalidDataException("Schematic nesting is too deep.");
            var start = opening ?? Next();
            if (start.Kind != '(') throw new InvalidDataException("Expected a schematic list.");
            var name = Next();
            if (name.Kind != 'a') throw new InvalidDataException("Expected a schematic list name.");
            var node = new Node(name.Value, start.Start, -1, [], []);
            while (true)
            {
                var token = Next();
                if (token.Kind == ')') return node with { End = token.End };
                if (token.Kind == 'e') throw new InvalidDataException("Unclosed schematic list.");
                if (token.Kind == '(') node.Children.Add(List(depth + 1, token));
                else node.Values.Add(token);
            }
        }
        public Token Next()
        {
            while (_position < text.Length && (char.IsWhiteSpace(text[_position]) || (_position == 0 && text[_position] == '\uFEFF'))) _position++;
            var start = _position;
            if (start == text.Length) return new('e', start, start, "");
            var c = text[_position++];
            if (c is '(' or ')') return new(c, start, _position, "");
            if (c == '"')
            {
                var value = new StringBuilder();
                while (_position < text.Length)
                {
                    c = text[_position++];
                    if (c == '"') return new('s', start, _position, value.ToString());
                    if (c == '\\')
                    {
                        if (_position == text.Length) break;
                        c = text[_position++] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var other => other };
                    }
                    value.Append(c);
                }
                throw new InvalidDataException("Unclosed schematic string.");
            }
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not '(' and not ')' and not '"') _position++;
            return new('a', start, _position, text[start.._position]);
        }
    }
}
