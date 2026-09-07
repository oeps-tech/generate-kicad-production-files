using System.Text;

namespace Oeps.KicadProductionFiles.Core.Kicad;

/// <summary>Reads the root title block and edits only its revision token, preserving all other text.</summary>
public sealed class SchematicRevisionDocument
{
    private readonly string _text;
    private readonly ListNode _root;
    private readonly ListNode? _title;
    private readonly Token? _revision;
    public string? Revision => _revision?.Value;

    public SchematicRevisionDocument(string text)
    {
        _text = text;
        var parser = new Parser(text);
        _root = parser.ReadList(0, capture: true);
        if (_root.Name != "kicad_sch" || _root.Values.Count != 0 || parser.Next().Kind != 'e')
            throw new InvalidDataException("The main schematic must contain one kicad_sch document.");
        var titles = _root.Children.Where(node => node.Name == "title_block").ToArray();
        if (titles.Length > 1) throw new InvalidDataException("The main schematic contains duplicate title_block sections.");
        _title = titles.SingleOrDefault();
        if (_title is null) return;
        if (_title.Values.Count != 0) throw new InvalidDataException("The main schematic title_block is malformed.");
        var revisions = _title.Children.Where(node => node.Name == "rev").ToArray();
        if (revisions.Length > 1) throw new InvalidDataException("The main schematic title_block contains duplicate rev entries.");
        var revision = revisions.SingleOrDefault();
        if (revision is null) return;
        if (revision.Children.Count != 0 || revision.Values.Count != 1 || revision.Values[0].Kind != 's')
            throw new InvalidDataException("The main schematic title_block rev must contain one quoted string.");
        _revision = revision.Values[0];
    }

    public string WithRevision(string revision)
    {
        var expected = RevisionValue.Normalize(revision);
        if (Revision is not null)
        {
            try { if (RevisionValue.Normalize(Revision) == expected) return _text; }
            catch (InvalidDataException) { /* Empty saved revisions can be repaired. */ }
        }
        var quoted = "\"" + expected.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        if (_revision is Token token) return _text[..token.Start] + quoted + _text[token.End..];
        // Insert missing metadata near the root header, before the symbol contents.
        var position = _title?.HeaderEnd ?? (_root.Children.FirstOrDefault(node => node.Name == "lib_symbols")?.Start ?? _root.CloseStart);
        var newline = _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var addition = _title is null
            ? $"{newline}\t(title_block{newline}\t\t(rev {quoted}){newline}\t){newline}"
            : $"{newline}\t\t(rev {quoted})";
        return _text.Insert(position, addition);
    }

    private sealed record Token(char Kind, int Start, int End, string Value);
    private sealed record ListNode(string Name, int Start, int HeaderEnd, int CloseStart, List<Token> Values, List<ListNode> Children);

    // Tokenize strings and balanced lists rather than searching for '(rev' inside comments or library symbols.
    private sealed class Parser(string text)
    {
        private int _position;
        public ListNode ReadList(int depth, bool capture, int start = -1)
        {
            if (depth > 128) throw new InvalidDataException("The main schematic nesting is too deep.");
            if (start < 0)
            {
                var opening = Next();
                if (opening.Kind != '(') throw new InvalidDataException("Expected a schematic list.");
                start = opening.Start;
            }
            var name = Next();
            if (name.Kind != 'a') throw new InvalidDataException("A schematic list has no name.");
            var node = new ListNode(name.Value, start, name.End, -1, [], []);
            while (true)
            {
                var token = Next();
                if (token.Kind == ')') return node with { CloseStart = token.Start };
                if (token.Kind == 'e') throw new InvalidDataException("The main schematic has an unclosed list.");
                if (token.Kind == '(')
                {
                    var child = ReadList(depth + 1, capture && (depth == 0 || name.Value == "title_block"), token.Start);
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
                var value = new StringBuilder();
                while (_position < text.Length)
                {
                    character = text[_position++];
                    if (character == '"') return new('s', start, _position, value.ToString());
                    if (character == '\\')
                    {
                        if (_position == text.Length) break;
                        character = text[_position++];
                        character = character switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => character };
                    }
                    value.Append(character);
                }
                throw new InvalidDataException("The main schematic has an unclosed quoted string.");
            }
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not '(' and not ')' and not '"') _position++;
            return new('a', start, _position, text[start.._position]);
        }
    }
}
