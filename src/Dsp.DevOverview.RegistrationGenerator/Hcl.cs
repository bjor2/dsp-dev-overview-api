namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>A non-literal HCL expression (function call, reference, conditional, ...) kept as source text.</summary>
public sealed record HclExpr(string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Just enough HCL to read the `locals { ... }` files the generator needs: attributes, blocks, objects, tuples,
/// quoted strings (interpolations kept verbatim), heredocs, numbers, bools and null. Anything else (function calls,
/// references, operators) becomes an <see cref="HclExpr"/> with its source text. Values map to
/// string | long | double | bool | null | HclExpr | Dictionary&lt;string, object?&gt; | List&lt;object?&gt;.
/// Not a Terraform evaluator: no variables are resolved here.
/// </summary>
public sealed class Hcl
{
    private readonly string _s;
    private int _i;

    private Hcl(string text) => _s = text;

    /// <summary>Parses a file body. Repeated blocks with the same name (several `locals { }`) are merged.</summary>
    public static Dictionary<string, object?> Parse(string text)
    {
        var p = new Hcl(text);
        var body = p.Body('\0');
        p.SkipWs();
        if (p._i < p._s.Length) throw p.Error("unexpected trailing content");
        return body;
    }

    public static Dictionary<string, object?> ParseFile(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (FormatException ex) { throw new FormatException($"{path}: {ex.Message}", ex); }
    }

    private FormatException Error(string message)
    {
        var line = 1 + _s.AsSpan(0, Math.Min(_i, _s.Length)).Count('\n');
        return new FormatException($"HCL line {line}: {message}");
    }

    private char Cur => _i < _s.Length ? _s[_i] : '\0';
    private char At(int offset) => _i + offset < _s.Length ? _s[_i + offset] : '\0';

    /// <summary>Skips whitespace and comments; with <paramref name="newlines"/> false it stops at the first newline.</summary>
    private void SkipWs(bool newlines = true)
    {
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c == '\n' && !newlines) return;
            if (char.IsWhiteSpace(c)) { _i++; continue; }
            if (c == '#' || (c == '/' && At(1) == '/'))
            {
                while (_i < _s.Length && _s[_i] != '\n') _i++;
                continue;
            }
            if (c == '/' && At(1) == '*')
            {
                var end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                _i = end < 0 ? _s.Length : end + 2;
                continue;
            }
            return;
        }
    }

    private Dictionary<string, object?> Body(char close)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            SkipWs();
            if (Cur == ',') { _i++; continue; }
            if (_i >= _s.Length)
            {
                if (close != '\0') throw Error($"missing '{close}'");
                return result;
            }
            if (Cur == close) return result;

            var key = Cur == '"' ? StringLiteral() : Identifier();
            SkipWs(newlines: false);
            if ((Cur == '=' && At(1) != '=') || Cur == ':')
            {
                _i++;
                result[key] = Value();
                continue;
            }
            // Block: name ["label" ...] { body }
            var labels = new List<string>();
            while (Cur == '"') { labels.Add(StringLiteral()); SkipWs(newlines: false); }
            if (Cur != '{') throw Error($"expected '=' or '{{' after '{key}'");
            _i++;
            var block = Body('}');
            _i++;
            foreach (var label in Enumerable.Reverse(labels)) block = new() { [label] = block };
            if (result.TryGetValue(key, out var existing) && existing is Dictionary<string, object?> merged)
                foreach (var (k, v) in block) merged[k] = v;
            else result[key] = block;
        }
    }

    private string Identifier()
    {
        var start = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '_' or '-' or '.')) _i++;
        if (_i == start) throw Error($"unexpected '{Cur}'");
        return _s[start.._i];
    }

    private object? Value()
    {
        SkipWs();
        var start = _i;
        object? literal;
        switch (Cur)
        {
            case '{':
                _i++;
                var obj = Body('}');
                _i++;
                return obj;
            case '[':
                return Tuple();
            case '"':
                literal = StringLiteral();
                break;
            case '<' when At(1) == '<':
                return Heredoc();
            default:
                if (char.IsDigit(Cur) || (Cur == '-' && char.IsDigit(At(1)))) literal = Number();
                else if (Word("true")) literal = true;
                else if (Word("false")) literal = false;
                else if (Word("null")) literal = null;
                else return RawExpression(start);
                break;
        }
        // A literal followed by an operator on the same line ("a" == b ? ...) is an expression.
        var after = _i;
        SkipWs(newlines: false);
        if (Cur is '\0' or '\n' or ',' or '}' or ']' or ')') { _i = after; return literal; }
        return RawExpression(start);
    }

    private bool Word(string word)
    {
        if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) return false;
        var next = At(word.Length);
        if (char.IsLetterOrDigit(next) || next is '_' or '.' or '-') return false;
        _i += word.Length;
        return true;
    }

    private object Number()
    {
        var start = _i;
        if (Cur == '-') _i++;
        while (char.IsDigit(Cur) || Cur is '.' or 'e' or 'E') _i++;
        var text = _s[start.._i];
        if (long.TryParse(text, out var l)) return l; // not a ?: — that would widen the long to double
        return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private List<object?> Tuple()
    {
        _i++; // [
        var list = new List<object?>();
        while (true)
        {
            SkipWs();
            if (Cur == ']') { _i++; return list; }
            if (Cur == ',') { _i++; continue; }
            if (_i >= _s.Length) throw Error("missing ']'");
            list.Add(Value());
        }
    }

    /// <summary>"..." with escapes decoded and ${...} / %{...} kept verbatim (they may contain quotes).</summary>
    private string StringLiteral()
    {
        _i++; // opening quote
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) throw Error("unterminated string");
            var c = _s[_i];
            if (c == '"') { _i++; return sb.ToString(); }
            if (c == '\\' && _i + 1 < _s.Length)
            {
                var e = _s[_i + 1];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                _i += 2;
                continue;
            }
            if (c is '$' or '%' && At(1) == '{')
            {
                var start = _i;
                _i += 2;
                SkipBalanced('}');
                sb.Append(_s, start, _i - start);
                continue;
            }
            sb.Append(c);
            _i++;
        }
    }

    /// <summary>Advances past the matching <paramref name="close"/>, honouring nested brackets and strings.</summary>
    private void SkipBalanced(char close)
    {
        var depth = 0;
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c == '"') { StringLiteral(); continue; }
            if (c is '{' or '(' or '[') depth++;
            else if (c is '}' or ')' or ']')
            {
                if (depth == 0 && c == close) { _i++; return; }
                depth--;
            }
            _i++;
        }
        throw Error($"missing '{close}'");
    }

    private string Heredoc()
    {
        _i += 2;
        if (Cur == '-') _i++;
        var marker = Identifier();
        var bodyStart = _s.IndexOf('\n', _i) + 1;
        var lines = new List<string>();
        var pos = bodyStart;
        while (pos > 0 && pos < _s.Length)
        {
            var end = _s.IndexOf('\n', pos);
            var line = end < 0 ? _s[pos..] : _s[pos..end];
            if (line.Trim() == marker) { _i = end < 0 ? _s.Length : end; return string.Join("\n", lines); }
            lines.Add(line);
            pos = end < 0 ? _s.Length : end + 1;
        }
        throw Error($"unterminated heredoc {marker}");
    }

    /// <summary>Reads an expression up to a newline / ',' / closing bracket at depth 0.</summary>
    private HclExpr RawExpression(int start)
    {
        _i = start;
        var depth = 0;
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c == '"') { StringLiteral(); continue; }
            if (c == '#' || (c == '/' && At(1) is '/' or '*')) { if (depth == 0) break; SkipWs(); continue; }
            if (c is '{' or '(' or '[') depth++;
            else if (c is '}' or ')' or ']')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (depth == 0 && c is '\n' or ',') break;
            _i++;
        }
        var text = _s[start.._i].Trim();
        if (text.Length == 0) throw Error("expected a value");
        return new HclExpr(text);
    }
}
