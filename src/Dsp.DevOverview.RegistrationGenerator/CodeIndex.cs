using System.Text;
using System.Text.RegularExpressions;

namespace Dsp.DevOverview.RegistrationGenerator;

/// <summary>A Service Bus send found in code: <c>receiver.SendAsync(new Message …)</c> on a queue/topic client field or parameter.</summary>
public sealed record SendSite(string File, int Line, string ConfigKey, string SenderClass, string? Message, string? Via)
{
    /// <summary>"src/X/Y.cs:52" (+ " → src/X/Client.cs:43" when the client sends through its own ServiceBusSender).</summary>
    public string Source => Via is null ? $"{File}:{Line}" : $"{File}:{Line} → {Via}";
}

/// <summary>
/// ADDENDA-008 §9.2: a light, read-only index of a service repo's C# (no Roslyn, no build) used to attribute Service Bus
/// sends to the Function (or Api controllers) whose call graph reaches them.
/// <list type="bullet">
/// <item>Senders: classes deriving <c>QueueClientBase</c>/<c>TopicClientBase</c> or calling <c>CreateSender(settings.X)</c>;
/// <c>settings.X</c> resolves to a config key through <c>X = configuration.GetValue&lt;string&gt;("Key")</c>.</item>
/// <item>Call graph: from a root method, member calls on fields/ctor params/method params (<c>_x.M(</c>, <c>_x.Value.M(</c>)
/// resolve to the repo classes implementing the field's type; unqualified calls resolve along the class and its repo base
/// classes. A member call whose method is not declared in the repo (e.g. inherited from a NuGet base) walks every method of
/// that class. Calls into code outside the repo (Dahl.Core <c>HandleWithRetry</c>) are not followed: retry infra is not drawn.</item>
/// </list>
/// Comments and string contents are blanked before parsing, so commented-out sends never count.
/// </summary>
public sealed partial class CodeIndex
{
    public sealed class ClassInfo
    {
        public required string Name;
        public required string File;
        public string Namespace = "";
        public required string Code;          // comment/string-blanked text of the whole file
        public required int BodyStart;        // index of '{'
        public required int BodyEnd;          // index of matching '}'
        public bool IsInterface;
        public List<string> Bases = [];       // simple names (generic args dropped)
        public List<string> BaseArgs = [];    // argument text of the base(...) / primary base call
        public Dictionary<string, string> Members = new(StringComparer.Ordinal); // field/property/ctor-param name → type text
        public List<MethodInfo> Methods = [];
    }

    public sealed record MethodInfo(string Name, int Start, int End, IReadOnlyDictionary<string, string> Params);

    private sealed record SenderInfo(string Class, string ConfigKey, string? Via);

    private readonly Dictionary<string, List<ClassInfo>> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ClassInfo>> _implementations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SenderInfo> _senderTypes = new(StringComparer.Ordinal); // class or interface name → sender
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);         // settings property → config key
    private readonly Dictionary<string, (string Raw, int[] Lines)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _visible = new(StringComparer.Ordinal); // file → namespaces in scope
    private readonly HashSet<string> _globalUsings = new(StringComparer.Ordinal);
    private readonly Dictionary<ClassInfo, List<(int Index, SendSite Site)>> _sites = [];

    public string RepoRoot { get; }
    public IReadOnlyCollection<string> SenderConfigKeys => _senderTypes.Values.Select(s => s.ConfigKey).ToHashSet();
    public IEnumerable<SendSite> AllSites => _sites.Values.SelectMany(l => l.Select(x => x.Site));

    private CodeIndex(string repoRoot) => RepoRoot = repoRoot;

    /// <summary>Indexes every <c>*.cs</c> under <c>{repo}/src</c> (bin/obj and test projects skipped).</summary>
    public static CodeIndex Build(string repoRoot)
    {
        var index = new CodeIndex(repoRoot);
        var src = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(src)) return index;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains(".Test", StringComparison.Ordinal) || relative.Contains("Tests/", StringComparison.Ordinal)) continue;
            index.AddFile(relative, File.ReadAllText(file));
        }
        index.Link();
        return index;
    }

    /// <summary>For tests: index in-memory sources keyed by repo-relative path.</summary>
    public static CodeIndex FromSources(IReadOnlyDictionary<string, string> sources)
    {
        var index = new CodeIndex("");
        foreach (var (path, text) in sources.OrderBy(kv => kv.Key, StringComparer.Ordinal)) index.AddFile(path, text);
        index.Link();
        return index;
    }

    // ------------------------------------------------------------------ queries

    /// <summary>The class + method declaring <c>[Function("name")]</c> in <paramref name="file"/> (repo-relative), if found.</summary>
    public (ClassInfo Class, MethodInfo Method)? FunctionRoot(string file, string function)
    {
        if (!_files.TryGetValue(file, out var f)) return null;
        foreach (Match m in FunctionAttr().Matches(f.Raw))
        {
            if (m.Groups["fn"].Value != function) continue;
            var cls = ClassesIn(file).Where(c => c.BodyStart < m.Index && m.Index < c.BodyEnd).MaxBy(c => c.BodyStart);
            var method = cls?.Methods.Where(x => x.Start > m.Index).MinBy(x => x.Start);
            if (cls is not null && method is not null) return (cls, method);
        }
        return null;
    }

    /// <summary>MVC controllers under <c>src/{projectFolder}</c> (class name *Controller or a Controller/ControllerBase base).</summary>
    public IReadOnlyList<ClassInfo> Controllers(string projectFolder) =>
        _classes.Values.SelectMany(l => l)
            .Where(c => !c.IsInterface && c.File.StartsWith($"src/{projectFolder}/", StringComparison.Ordinal)
                        && (c.Name.EndsWith("Controller", StringComparison.Ordinal) || c.Bases.Any(b => b is "Controller" or "ControllerBase")))
            .OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();

    /// <summary>Send sites reachable from one Function method.</summary>
    public IReadOnlyList<SendSite> SitesFrom(ClassInfo cls, MethodInfo method) => Walk([(cls, method)]);

    /// <summary>Send sites reachable from every method of the given classes (Api controllers).</summary>
    public IReadOnlyList<SendSite> SitesFrom(IEnumerable<ClassInfo> classes) =>
        Walk(classes.SelectMany(c => c.Methods.Select(m => (c, m))).ToList());

    /// <summary>Sender types (by config key) whose clients are passed to a base constructor of <paramref name="cls"/> (Dahl.Core MessageHandler retry client).</summary>
    public IReadOnlyList<(string Type, string ConfigKey)> BaseCtorSenders(ClassInfo cls)
    {
        var result = new List<(string, string)>();
        foreach (var arg in cls.BaseArgs.SelectMany(SplitTopLevel))
        {
            var name = arg.Trim();
            if (cls.Members.TryGetValue(name, out var type))
                foreach (var t in TypeNames(type))
                    if (_senderTypes.TryGetValue(t, out var s)) result.Add((t, s.ConfigKey));
        }
        return result;
    }

    // ------------------------------------------------------------------ walk

    private List<SendSite> Walk(List<(ClassInfo Class, MethodInfo Method)> roots)
    {
        var seen = new HashSet<(ClassInfo, MethodInfo)>();
        var wholeClasses = new HashSet<ClassInfo>();
        var sites = new List<SendSite>();
        var queue = new Queue<(ClassInfo Context, ClassInfo Owner, MethodInfo Method)>(roots.Select(r => (r.Class, r.Class, r.Method)));
        while (queue.Count > 0)
        {
            var (ctx, owner, method) = queue.Dequeue();
            if (!seen.Add((ctx, method))) continue;
            foreach (var (index, site) in _sites.GetValueOrDefault(owner) ?? [])
                if (index >= method.Start && index < method.End) sites.Add(site);

            var body = owner.Code[method.Start..method.End];
            foreach (Match call in MemberCall().Matches(body))
            {
                var recv = call.Groups["recv"].Value;
                var name = call.Groups["m"].Value;
                var type = method.Params.GetValueOrDefault(recv) ?? Member(owner, recv);
                IEnumerable<ClassInfo> targets;
                if (type is not null) targets = TypeNames(type).Where(t => !_senderTypes.ContainsKey(t)).SelectMany(t => Concrete(owner.File, t));
                else if (Lookup(owner.File, recv) is { Count: > 0 } statics) targets = statics.Where(c => !c.IsInterface); // static call on a repo class
                else continue;
                foreach (var target in targets.Distinct())
                {
                    var declared = Resolve(target, name).ToList();
                    if (declared.Count > 0) foreach (var (o, m) in declared) queue.Enqueue((target, o, m));
                    else if (wholeClasses.Add(target)) // method lives outside the repo (NuGet base): every repo method may run
                        foreach (var c in Chain(target)) foreach (var m in c.Methods) queue.Enqueue((target, c, m));
                }
            }
            foreach (Match call in BareCall().Matches(body))
                foreach (var (o, m) in Resolve(ctx, call.Groups["m"].Value))
                    queue.Enqueue((ctx, o, m));
        }
        return sites.Distinct().OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ToList();
    }

    /// <summary>Methods named <paramref name="name"/> on <paramref name="cls"/> or the first repo base class declaring it (virtual dispatch from the concrete class).</summary>
    private IEnumerable<(ClassInfo, MethodInfo)> Resolve(ClassInfo cls, string name)
    {
        foreach (var c in Chain(cls))
        {
            var hits = c.Methods.Where(m => m.Name == name).ToList();
            if (hits.Count > 0) return hits.Select(m => (c, m));
        }
        return [];
    }

    private IEnumerable<ClassInfo> Chain(ClassInfo cls)
    {
        var seen = new HashSet<ClassInfo>();
        for (var c = cls; c is not null && seen.Add(c); c = c.Bases.SelectMany(b => Lookup(c.File, b)).FirstOrDefault(x => !x.IsInterface))
            yield return c;
    }

    private string? Member(ClassInfo cls, string name)
    {
        foreach (var c in Chain(cls))
            if (c.Members.TryGetValue(name, out var t)) return t;
        return null;
    }

    /// <summary>Repo classes for a simple type name as seen from <paramref name="file"/>: same/parent namespace or a using wins; else every match.</summary>
    private List<ClassInfo> Lookup(string file, string typeName)
    {
        var all = _classes.GetValueOrDefault(typeName);
        if (all is null) return [];
        if (all.Count == 1) return all;
        var visible = _visible.GetValueOrDefault(file) ?? [];
        var hits = all.Where(c => visible.Contains(c.Namespace) || _globalUsings.Contains(c.Namespace)).ToList();
        return hits.Count > 0 ? hits : all;
    }

    /// <summary>Concrete classes behind a type name seen from <paramref name="file"/>: the class itself, or the implementations of that interface.</summary>
    private IEnumerable<ClassInfo> Concrete(string file, string typeName)
    {
        foreach (var decl in Lookup(file, typeName))
        {
            if (!decl.IsInterface) { yield return decl; continue; }
            foreach (var impl in _implementations.GetValueOrDefault(typeName) ?? [])
                if (Lookup(impl.File, typeName).Contains(decl)) yield return impl;
        }
    }

    private static IEnumerable<string> TypeNames(string typeText) =>
        TypeIdentifier().Matches(typeText).Select(m => m.Value.Split('.')[^1]);

    // ------------------------------------------------------------------ indexing

    private IEnumerable<ClassInfo> ClassesIn(string file) => _classes.Values.SelectMany(l => l).Where(c => c.File == file);

    private void AddFile(string path, string text)
    {
        var raw = Blank(text, strings: false);
        var code = Blank(text, strings: true);
        var lines = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lines.Add(i + 1);
        _files[path] = (raw, lines.ToArray());
        var ns = NamespaceDecl().Match(code) is { Success: true } nsm ? nsm.Groups["ns"].Value : "";
        var visible = new HashSet<string>(StringComparer.Ordinal);
        for (var n = ns; n.Length > 0; n = n.Contains('.') ? n[..n.LastIndexOf('.')] : "") visible.Add(n);
        visible.Add("");
        foreach (Match u in UsingDirective().Matches(code))
            (u.Groups["global"].Success ? _globalUsings : visible).Add(u.Groups["ns"].Value);
        _visible[path] = visible;

        foreach (Match b in ConfigBinding().Matches(raw)) _bindings.TryAdd(b.Groups["prop"].Value, b.Groups["key"].Value);
        foreach (Match b in ConfigIndexer().Matches(raw)) _bindings.TryAdd(b.Groups["prop"].Value, b.Groups["key"].Value);

        foreach (Match m in TypeDecl().Matches(code))
        {
            var i = m.Index + m.Length;
            var primary = "";
            i = SkipWs(code, i);
            if (i < code.Length && code[i] == '<') i = SkipWs(code, Match(code, i, '<', '>') + 1);
            if (i < code.Length && code[i] == '(')
            {
                var end = Match(code, i, '(', ')');
                primary = code[(i + 1)..end];
                i = SkipWs(code, end + 1);
            }
            var bases = new List<string>();
            var baseArgs = new List<string>();
            if (i < code.Length && code[i] == ':')
            {
                var start = i + 1;
                var j = start;
                while (j < code.Length && code[j] != '{' && code[j] != ';' && !IsWord(code, j, "where"))
                {
                    if (code[j] is '(' or '<') j = Match(code, j, code[j], code[j] == '(' ? ')' : '>');
                    j++;
                }
                foreach (var part in SplitTopLevel(code[start..j]))
                {
                    var p = part.Trim();
                    var paren = p.IndexOf('(');
                    if (paren > 0) { baseArgs.Add(p[(paren + 1)..p.LastIndexOf(')')]); p = p[..paren]; }
                    var lt = p.IndexOf('<');
                    if (lt > 0) p = p[..lt];
                    if (p.Length > 0) bases.Add(p.Trim().Split('.')[^1]);
                }
                i = j;
            }
            while (i < code.Length && code[i] != '{' && code[i] != ';') i++;
            if (i >= code.Length || code[i] == ';') continue; // record without body
            var bodyEnd = Match(code, i, '{', '}');
            var cls = new ClassInfo
            {
                Name = m.Groups["name"].Value, File = path, Namespace = ns, Code = code, BodyStart = i, BodyEnd = bodyEnd,
                IsInterface = m.Groups["kind"].Value == "interface", Bases = bases, BaseArgs = baseArgs,
            };
            foreach (var (name, type) in Parameters(primary)) cls.Members[name] = type;
            (_classes.TryGetValue(cls.Name, out var list) ? list : _classes[cls.Name] = []).Add(cls);
        }

        foreach (var cls in ClassesIn(path).ToList()) IndexBody(cls);
    }

    private void IndexBody(ClassInfo cls)
    {
        var code = cls.Code;
        var nested = ClassesIn(cls.File).Where(c => c != cls && c.BodyStart > cls.BodyStart && c.BodyEnd < cls.BodyEnd).ToList();
        bool InNested(int idx) => nested.Any(n => idx > n.BodyStart && idx < n.BodyEnd);

        foreach (Match f in FieldDecl().Matches(code, cls.BodyStart))
        {
            if (f.Index >= cls.BodyEnd) break;
            if (InNested(f.Index) || Depth(code, cls.BodyStart, f.Index) != 1) continue;
            cls.Members.TryAdd(f.Groups["name"].Value, f.Groups["type"].Value);
        }
        foreach (Match d in MethodDecl().Matches(code, cls.BodyStart))
        {
            if (d.Index >= cls.BodyEnd) break;
            if (InNested(d.Index) || Depth(code, cls.BodyStart, d.Index) != 1) continue;
            var name = d.Groups["name"].Value;
            if (name is "if" or "while" or "for" or "foreach" or "switch" or "using" or "lock" or "catch" or "return" or "nameof" or "new" or "base" or "this") continue;
            if (!FollowsTypeOrModifier(code, d.Index)) continue; // [Function("x")], [ServiceBusTrigger(...)], => Call(...)
            var open = d.Index + d.Length - 1;
            var close = Match(code, open, '(', ')');
            var parms = Parameters(code[(open + 1)..close]).ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
            var j = SkipWs(code, close + 1);
            if (name == cls.Name) // constructor: params are members (assigned to fields), base(...) args are base args
            {
                foreach (var (n, t) in parms) cls.Members.TryAdd(n, t);
                if (j < code.Length && code[j] == ':')
                {
                    var p = code.IndexOf('(', j);
                    if (p > 0) cls.BaseArgs.Add(code[(p + 1)..Match(code, p, '(', ')')]);
                }
            }
            while (j < code.Length && code[j] != '{' && code[j] != ';' && !(code[j] == '=' && j + 1 < code.Length && code[j + 1] == '>')) j++;
            if (j >= code.Length || code[j] == ';') continue; // abstract/interface member or field initializer
            int end;
            if (code[j] == '{') end = Match(code, j, '{', '}');
            else { end = j; while (end < code.Length && code[end] != ';') { if (code[end] is '(' or '{') end = Match(code, end, code[end], code[end] == '(' ? ')' : '}'); end++; } }
            cls.Methods.Add(new MethodInfo(name, d.Index, Math.Min(end + 1, code.Length), parms));
        }
    }

    private void Link()
    {
        foreach (var cls in _classes.Values.SelectMany(l => l).Where(c => !c.IsInterface))
            foreach (var b in cls.Bases)
                (_implementations.TryGetValue(b, out var list) ? list : _implementations[b] = []).Add(cls);

        // Senders: QueueClientBase/TopicClientBase subclasses and CreateSender(settings.X) wrappers.
        foreach (var cls in _classes.Values.SelectMany(l => l).Where(c => !c.IsInterface))
        {
            string? key = null;
            string? via = null;
            if (cls.Bases.Any(b => b is "QueueClientBase" or "TopicClientBase"))
                key = cls.BaseArgs.SelectMany(a => SettingRefs().Matches(a)).Select(m => _bindings.GetValueOrDefault(m.Groups["prop"].Value))
                    .FirstOrDefault(k => k is not null && !k.Contains("Connection", StringComparison.Ordinal) && !k.Contains("Namespace", StringComparison.Ordinal) && !k.Contains("Instance", StringComparison.Ordinal));
            else
            {
                var raw = _files[cls.File].Raw[cls.BodyStart..cls.BodyEnd];
                if (CreateSender().Match(raw) is { Success: true } cs)
                {
                    var prop = cs.Groups["prop"].Value;
                    key = _bindings.GetValueOrDefault(prop) ?? (prop.StartsWith("ServiceBus", StringComparison.Ordinal) ? prop : null);
                    if (SdkSend().Match(cls.Code, cls.BodyStart) is { Success: true } inner && inner.Index < cls.BodyEnd)
                        via = $"{cls.File}:{LineOf(cls.File, inner.Index)}";
                }
            }
            if (key is null) continue;
            var sender = new SenderInfo(cls.Name, key, via);
            _senderTypes.TryAdd(cls.Name, sender);
            foreach (var b in cls.Bases.Where(b => b is not ("QueueClientBase" or "TopicClientBase"))) _senderTypes.TryAdd(b, sender);
        }

        // Send sites: client-typed receivers calling Send*/Schedule* (inside the sender classes themselves = internal, skipped).
        foreach (var cls in _classes.Values.SelectMany(l => l).Where(c => !c.IsInterface && !_senderTypes.ContainsKey(c.Name)))
        {
            foreach (Match s in SendCall().Matches(cls.Code[..cls.BodyEnd], cls.BodyStart))
            {
                var recv = s.Groups["recv"].Value;
                var method = cls.Methods.FirstOrDefault(m => s.Index >= m.Start && s.Index < m.End);
                var type = method?.Params.GetValueOrDefault(recv) ?? Member(cls, recv);
                if (type is null) continue;
                var sender = TypeNames(type).Select(t => _senderTypes.GetValueOrDefault(t)).FirstOrDefault(x => x is not null);
                if (sender is null) continue;
                var open = s.Index + s.Length - 1;
                var args = _files[cls.File].Raw[(open + 1)..Match(cls.Code, open, '(', ')')];
                var message = NewMessage().Match(args) is { Success: true } nm ? nm.Groups["t"].Value : null;
                var site = new SendSite(cls.File, LineOf(cls.File, s.Index), sender.ConfigKey, sender.Class, message, sender.Via);
                (_sites.TryGetValue(cls, out var list) ? list : _sites[cls] = []).Add((s.Index, site));
            }
        }
    }

    private int LineOf(string file, int index)
    {
        var lines = _files[file].Lines;
        var i = Array.BinarySearch(lines, index);
        return (i >= 0 ? i : ~i - 1) + 1;
    }

    // ------------------------------------------------------------------ text helpers

    /// <summary>Comments → spaces (newlines kept); with <paramref name="strings"/> also string/char contents (quotes kept).</summary>
    internal static string Blank(string s, bool strings)
    {
        var sb = new StringBuilder(s);
        var i = 0;
        void Fill(int from, int to) { for (var k = from; k < to && k < sb.Length; k++) if (sb[k] != '\n') sb[k] = ' '; }
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { var e = s.IndexOf('\n', i); e = e < 0 ? s.Length : e; Fill(i, e); i = e; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 ? s.Length : e + 2; Fill(i, e); i = e; continue; }
            if (c == '\'')
            {
                var e = i + 1;
                while (e < s.Length && s[e] != '\'' && s[e] != '\n') e += s[e] == '\\' ? 2 : 1;
                if (strings) Fill(i + 1, e);
                i = e + 1;
                continue;
            }
            if (c == '"' || (c is '@' or '$' && i + 1 < s.Length && (s[i + 1] == '"' || (s[i + 1] is '@' or '$' && i + 2 < s.Length && s[i + 2] == '"'))))
            {
                var q = s.IndexOf('"', i);
                var verbatim = s[i..q].Contains('@');
                if (q + 2 < s.Length && s[q + 1] == '"' && s[q + 2] == '"') // raw string literal
                {
                    var e = s.IndexOf("\"\"\"", q + 3, StringComparison.Ordinal);
                    e = e < 0 ? s.Length : e;
                    if (strings) Fill(q + 3, e);
                    i = e + 3;
                    continue;
                }
                var j = q + 1;
                while (j < s.Length)
                {
                    if (verbatim && s[j] == '"' && j + 1 < s.Length && s[j + 1] == '"') { j += 2; continue; }
                    if (!verbatim && s[j] == '\\') { j += 2; continue; }
                    if (s[j] == '"' || (!verbatim && s[j] == '\n')) break;
                    j++;
                }
                if (strings) Fill(q + 1, j);
                i = j + 1;
                continue;
            }
            i++;
        }
        return sb.ToString();
    }

    private static int Match(string s, int open, char o, char c)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == o) depth++;
            else if (s[i] == c && --depth == 0) return i;
        }
        return s.Length - 1;
    }

    private static int Depth(string s, int from, int to)
    {
        var d = 0;
        for (var i = from; i < to; i++) { if (s[i] == '{') d++; else if (s[i] == '}') d--; }
        return d;
    }

    /// <summary>A declaration's name follows its return type or a modifier ("Task Run(", "public Foo("), never '[', '(', ',', '.', '=' or "=>".</summary>
    private static bool FollowsTypeOrModifier(string s, int i)
    {
        var p = i - 1;
        while (p >= 0 && char.IsWhiteSpace(s[p])) p--;
        if (p < 0) return false;
        if (s[p] == '>' && p > 0 && s[p - 1] == '=') return false;
        return char.IsLetterOrDigit(s[p]) || s[p] is '_' or '>' or '?' or ']';
    }

    private static int SkipWs(string s, int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; return i; }

    private static bool IsWord(string s, int i, string word) =>
        string.CompareOrdinal(s, i, word, 0, word.Length) == 0 && (i == 0 || !char.IsLetterOrDigit(s[i - 1]))
        && (i + word.Length >= s.Length || !char.IsLetterOrDigit(s[i + word.Length]));

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        int depth = 0, start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '(' or '<' or '[' or '{') depth++;
            else if (s[i] is ')' or '>' or ']' or '}') depth--;
            else if (s[i] == ',' && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        if (start < s.Length && s[start..].Trim().Length > 0) yield return s[start..];
    }

    /// <summary>"[Attr] Lazy&lt;IFoo&gt; foo = null" → ("foo", "Lazy&lt;IFoo&gt;").</summary>
    private static IEnumerable<(string Name, string Type)> Parameters(string list)
    {
        foreach (var part in SplitTopLevel(list))
        {
            var p = AttributeList().Replace(part, " ");
            var eq = p.IndexOf('=');
            if (eq >= 0) p = p[..eq];
            var tokens = p.Trim().Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t is not ("this" or "params" or "in" or "out" or "ref" or "scoped" or "readonly")).ToList();
            if (tokens.Count < 2) continue;
            yield return (tokens[^1], string.Join(" ", tokens[..^1]));
        }
    }

    [GeneratedRegex(@"\b(?<kind>class|record|struct|interface)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex TypeDecl();

    [GeneratedRegex(@"(?:(?:private|protected|internal|public|static|readonly|required|virtual|override|new)\s+)+(?<type>[A-Za-z_][\w.]*(?:<[^;=(){}]*>)?\??)\s+(?<name>[A-Za-z_]\w*)\s*(?:;|=(?!>)|\{\s*(?:get|set|init))")]
    private static partial Regex FieldDecl();

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*(?:<[^;=(){}]*>)?\s*\(")]
    private static partial Regex MethodDecl();

    [GeneratedRegex(@"(?<![\w.])(?:this\.)?(?<recv>[A-Za-z_]\w*)\s*(?:\.\s*Value)?\s*\??\.\s*(?<m>[A-Za-z_]\w*)\s*(?:<[^;=(){}]*>)?\s*\(")]
    private static partial Regex MemberCall();

    [GeneratedRegex(@"(?<![\w.])(?<m>[A-Za-z_]\w*)\s*(?:<[^;=(){}]*>)?\s*\(")]
    private static partial Regex BareCall();

    [GeneratedRegex(@"(?<![\w.])(?:this\.)?(?<recv>[A-Za-z_]\w*)\s*(?:\.\s*Value)?\s*\??\.\s*(?:SendAsync|SendBatchAsync|SendMessageAsync|SendMessagesAsync|SendMessage|ScheduleMessageAsync|ScheduleMessagesAsync|ScheduleAsync)\s*(?:<[^;=(){}]*>)?\s*\(")]
    private static partial Regex SendCall();

    [GeneratedRegex(@"\.\s*(?:SendMessageAsync|SendMessagesAsync|ScheduleMessageAsync)\s*\(")]
    private static partial Regex SdkSend();

    [GeneratedRegex(@"\bnew\s+(?:[A-Za-z_]\w*\.)*(?<t>[A-Z]\w*)\s*[({<]")]
    private static partial Regex NewMessage();

    [GeneratedRegex(@"\b(?<prop>\w+)\s*=\s*\w+\.GetValue<string>\(\s*""(?<key>[^""]+)""\s*\)")]
    private static partial Regex ConfigBinding();

    [GeneratedRegex(@"\b(?<prop>\w+)\s*=\s*\w+\[\s*""(?<key>[^""]+)""\s*\]")]
    private static partial Regex ConfigIndexer();

    [GeneratedRegex(@"\b\w+\.(?<prop>[A-Za-z_]\w*)")]
    private static partial Regex SettingRefs();

    [GeneratedRegex(@"\.CreateSender\(\s*\w+\.(?<prop>[A-Za-z_]\w*)\s*\)")]
    private static partial Regex CreateSender();

    [GeneratedRegex(@"\[\s*(?:Function|FunctionName)\s*\(\s*(?:""(?<fn>[^""]+)""|nameof\((?<fn>\w+)\))\s*\)\s*\]")]
    private static partial Regex FunctionAttr();

    [GeneratedRegex(@"^\s*namespace\s+(?<ns>[\w.]+)", RegexOptions.Multiline)]
    private static partial Regex NamespaceDecl();

    [GeneratedRegex(@"^\s*(?<global>global\s+)?using\s+(?!static\b)(?!\w+\s*=)(?<ns>[\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"[A-Za-z_][\w.]*")]
    private static partial Regex TypeIdentifier();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex AttributeList();
}
