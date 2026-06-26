using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


public class AstNode
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public int Line { get; set; }
    public Dictionary<string, object?> Attrs { get; } = new();
    public List<AstNode> Children { get; } = new();

    public AstNode(string kind, string value = "", int line = 0)
    {
        Kind = kind;
        Value = value;
        Line = line;
    }

    public AstNode Add(AstNode child) { Children.Add(child); return child; }

    public int Count() => 1 + Children.Sum(c => c.Count());

    public Dictionary<string, object?> ToDict()
    {
        var d = new Dictionary<string, object?> { ["kind"] = Kind };
        if (Line > 0) d["line"] = Line;
        if (!string.IsNullOrEmpty(Value)) d["value"] = Value;
        foreach (var (k, v) in Attrs)
        {
            if (v is null) continue;
            if (v is string s && s.Length == 0) continue;
            if (v is List<string> lst && lst.Count == 0) continue;
            d[k] = v;
        }
        if (Children.Count > 0)
            d["children"] = Children.Select(c => c.ToDict()).ToList();
        return d;
    }
}


public static class TextUtils
{
    public static (string clean, List<(int start, int end, string text)> comments)
        StripComments(string src)
    {
        var comments = new List<(int, int, string)>();
        var result = new StringBuilder(src.Length);
        int i = 0, line = 1, n = src.Length;

        while (i < n)
        {
            if (i + 1 < n && src[i] == '-' && src[i + 1] == '-')
            {
                int j = src.IndexOf('\n', i);
                if (j == -1) j = n;
                comments.Add((line, line, src[i..j]));
                result.Append('\n');
                line++;
                i = j + 1;
                continue;
            }
            if (i + 1 < n && src[i] == '/' && src[i + 1] == '-')
            {
                int depth = 1, j = i + 2, startLine = line;
                while (j < n && depth > 0)
                {
                    if (j + 1 < n && src[j] == '/' && src[j + 1] == '-') { depth++; j += 2; }
                    else if (j + 1 < n && src[j] == '-' && src[j + 1] == '/') { depth--; j += 2; }
                    else { if (src[j] == '\n') line++; j++; }
                }
                comments.Add((startLine, line, src[i..j]));
                result.Append(new string('\n', line - startLine));
                i = j;
                continue;
            }
            if (src[i] == '\n') line++;
            result.Append(src[i]);
            i++;
        }
        return (result.ToString(), comments);
    }

    public static int LineAt(string src, int pos)
    {
        int count = 1;
        for (int i = 0; i < pos && i < src.Length; i++)
            if (src[i] == '\n') count++;
        return count;
    }
}


public static class TacticExtractor
{
    private static readonly Regex TacticKw = new(
        @"\b(simp|rw|rfl|exact|apply|refine|intro|intros|cases|rcases|obtain|" +
        @"constructor|use|ext|funext|congr|convert|norm_num|ring|linarith|omega|" +
        @"push_neg|pull_neg|contrapose|by_contra|by_cases|decide|trivial|" +
        @"assumption|tauto|aesop|field_simp|positivity|gcongr|" +
        @"have|let|show|suffices|calc|induction|match|" +
        @"simpa|simp_all|simp_rw|lift|peel|" +
        @"fin_cases|interval_cases|abel|noncomm_ring|group|" +
        @"all_goals|any_goals|first|try|repeat|" +
        @"next|case|rename_i|grind|norm_cast|push_cast|" +
        @"map_tac|continuity|measurability|" +
        @"specialize|generalize|clear|revert)\b",
        RegexOptions.Compiled);

    public record TacticStep(string Tactic, int LineOffset, List<string> Keywords);

    public static List<TacticStep> Extract(string proofBody)
    {
        var result = new List<TacticStep>();
        var lines = proofBody.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var stripped = lines[i].Trim();
            if (string.IsNullOrEmpty(stripped)) continue;
            if (TacticKw.IsMatch(stripped))
            {
                var kws = TacticKw.Matches(stripped)
                                   .Select(m => m.Value)
                                   .Distinct()
                                   .ToList();
                result.Add(new TacticStep(
                    stripped[..Math.Min(120, stripped.Length)],
                    i + 1,
                    kws));
            }
        }
        return result;
    }
}


public static class ParamParser
{
    private static readonly Dictionary<char, string> BracketKind = new()
    {
        ['('] = "explicit",
        ['{'] = "implicit",
        ['['] = "instance",
        ['⦃'] = "strict_implicit",
    };

    private static readonly Dictionary<char, char> CloseOf = new()
    {
        [')'] = '(',
        ['}'] = '{',
        [']'] = '[',
        ['⦄'] = '⦃',
    };

    private static readonly HashSet<char> Opens = new() { '(', '{', '[', '⦃' };
    private static readonly HashSet<char> Closes = new() { ')', '}', ']', '⦄' };

    public record Param(string Kind, string Name, string? Type);

    public static List<Param> Parse(string sig)
    {
        var result = new List<Param>();
        int i = 0;
        while (i < sig.Length)
        {
            char ch = sig[i];
            if (Opens.Contains(ch))
            {
                var stack = new Stack<char>();
                stack.Push(ch);
                int j = i + 1;
                while (j < sig.Length && stack.Count > 0)
                {
                    char c = sig[j];
                    if (Opens.Contains(c)) stack.Push(c);
                    else if (Closes.Contains(c) && stack.Count > 0 && stack.Peek() == CloseOf[c])
                        stack.Pop();
                    j++;
                }
                string inner = sig[(i + 1)..(j - 1)].Trim();
                string kind = BracketKind.TryGetValue(ch, out var k) ? k : "explicit";
                int colon = FindFirstColon(inner);
                if (colon != -1)
                {
                    string namesRaw = inner[..colon].Trim();
                    string typeRaw = inner[(colon + 1)..].Trim();
                    var names = Regex.Split(namesRaw, @"\s+")
                                     .Where(n => n.Length > 0).ToList();
                    foreach (var name in names)
                        result.Add(new Param(kind, name, typeRaw));
                }
                else if (inner.Length > 0)
                    result.Add(new Param(kind, inner, null));
                i = j;
            }
            else i++;
        }
        return result;
    }

    public static int FindFirstColon(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '(' || ch == '{' || ch == '[') depth++;
            else if (ch == ')' || ch == '}' || ch == ']') depth--;
            else if (ch == ':' && depth == 0 && (i + 1 >= s.Length || s[i + 1] != '='))
                return i;
        }
        return -1;
    }
}


public static class AstBuilder
{
    private static readonly Regex ReImport = new(@"^(?:public\s+)?import\s+(.+)$", RegexOptions.Multiline);
    private static readonly Regex ReOpen = new(@"^open\s+(.+)$", RegexOptions.Multiline);
    private static readonly Regex ReNamespace = new(@"^namespace\s+(\w+(?:\.\w+)*)", RegexOptions.Multiline);
    private static readonly Regex ReSection = new(@"^(?:@\[expose\]\s+public\s+)?section(?:\s+(\w+))?", RegexOptions.Multiline);
    private static readonly Regex ReVariable = new(@"^variable\s+(.+?)$", RegexOptions.Multiline);

    private static readonly Regex DeclReNet = new(
        @"^((?:@\[.*?\]\s*\n?)*)((?:noncomputable\s+|protected\s+|private\s+|public\s+)*)(theorem|lemma|def|abbrev|instance|class|structure|inductive|grind_pattern|alias)\s+([^\s({[\n:]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TopLevel = new(
        @"^(?:@\[.*\]\s*)*(?:noncomputable\s+|protected\s+|private\s+|public\s+)*" +
        @"(?:theorem|lemma|def|abbrev|instance|class|structure|inductive|" +
        @"namespace|section|end|variable|open|export|grind_pattern|alias)\b",
        RegexOptions.Multiline);


    public static AstNode Build(string srcPath)
    {
        string srcOriginal = File.ReadAllText(srcPath, Encoding.UTF8);
        var (srcClean, rawComments) = TextUtils.StripComments(srcOriginal);
        int numLines = srcOriginal.Count(c => c == '\n') + 1;

        var root = new AstNode("FileNode", srcPath);
        root.Attrs["num_lines"] = numLines;

        var header = root.Add(new AstNode("ModuleHeader", line: 1));
        foreach (Match m in Regex.Matches(srcOriginal, @"/-!(.*?)-/", RegexOptions.Singleline))
        {
            string txt = m.Groups[1].Value.Trim();
            var doc = header.Add(new AstNode("ModuleDoc",
                txt.Length > 200 ? txt[..200] + "…" : txt,
                TextUtils.LineAt(srcOriginal, m.Index)));
            doc.Attrs["full_length"] = txt.Length;
        }

        var importsNode = root.Add(new AstNode("Imports"));
        foreach (Match m in ReImport.Matches(srcClean))
        {
            var imp = new AstNode("Import", m.Groups[1].Value.Trim(),
                                  TextUtils.LineAt(srcClean, m.Index));
            importsNode.Add(imp);
        }

        var opensNode = root.Add(new AstNode("Opens"));
        foreach (Match m in ReOpen.Matches(srcClean))
            opensNode.Add(new AstNode("Open", m.Groups[1].Value.Trim(),
                                      TextUtils.LineAt(srcClean, m.Index)));

        var docsNode = root.Add(new AstNode("Comments"));
        docsNode.Attrs["total"] = rawComments.Count;
        foreach (var (startL, endL, text) in rawComments)
        {
            bool isDoc = text.StartsWith("/--");
            bool isBlock = text.StartsWith("/-") && !isDoc;
            string kind = isDoc ? "DocComment" : (isBlock ? "BlockComment" : "LineComment");
            var cn = new AstNode(kind, text.Trim()[..Math.Min(160, text.Trim().Length)]);
            cn.Attrs["start_line"] = startL;
            cn.Attrs["end_line"] = endL;
            docsNode.Add(cn);
        }

        var scopesNode = root.Add(new AstNode("Scopes"));
        int nsDepth = 0;
        foreach (Match m in ReNamespace.Matches(srcClean))
        {
            nsDepth++;
            var ns = new AstNode("NamespaceOpen", m.Groups[1].Value,
                                  TextUtils.LineAt(srcClean, m.Index));
            ns.Attrs["depth"] = nsDepth;
            scopesNode.Add(ns);
        }
        foreach (Match m in ReSection.Matches(srcClean))
        {
            string name = m.Groups[1].Success ? m.Groups[1].Value : "(anonymous)";
            scopesNode.Add(new AstNode("SectionOpen", name,
                                        TextUtils.LineAt(srcClean, m.Index)));
        }

        var varsNode = root.Add(new AstNode("Variables"));
        foreach (Match m in ReVariable.Matches(srcClean))
        {
            string raw = m.Groups[1].Value.Trim();
            var vnode = new AstNode("Variable",
                raw.Length > 120 ? raw[..120] : raw,
                TextUtils.LineAt(srcClean, m.Index));
            foreach (var p in ParamParser.Parse(raw))
            {
                var pn = new AstNode("Param", p.Name);
                pn.Attrs["kind"] = p.Kind;
                pn.Attrs["type"] = p.Type ?? "";
                vnode.Add(pn);
            }
            varsNode.Add(vnode);
        }

        var declsNode = root.Add(new AstNode("Declarations"));

        foreach (Match m in DeclReNet.Matches(srcOriginal))
        {
            string mods = m.Groups[2].Value.Trim();
            string kw = m.Groups[3].Value.Trim();
            string name = m.Groups[4].Value.Trim();
            int startIdx = m.Index;
            int endIdx = FindBlockEnd(srcOriginal, startIdx);
            string block = srcOriginal[startIdx..endIdx];
            int lineNum = TextUtils.LineAt(srcOriginal, startIdx);

            int lineStart = srcOriginal.LastIndexOf('\n', Math.Max(0, startIdx - 1));
            string prePart = srcOriginal[Math.Max(0, lineStart - 300)..startIdx];
            var attrsList = Regex.Matches(prePart, @"@\[(.*?)\]").Select(a => a.Groups[1].Value).ToList();
            string docstring = ExtractDocstring(prePart);

            string sigRaw = Regex.Split(block, @":=|\bwhere\b")[0];
            var @params = ParamParser.Parse(sigRaw);
            string retType = ExtractReturnType(sigRaw);

            string? proofBody = ExtractProofBody(block);
            var tactics = proofBody != null ? TacticExtractor.Extract(proofBody) : new();

            var whereFields = new List<string>();
            var whereM = Regex.Match(block, @"\bwhere\b(.*)", RegexOptions.Singleline);
            if (whereM.Success)
            {
                foreach (var wl in whereM.Groups[1].Value.Split('\n'))
                {
                    string ws = wl.Trim();
                    if (ws.Length > 0 && !ws.StartsWith("|"))
                        whereFields.Add(ws.Length > 120 ? ws[..120] : ws);
                }
            }

            var dnode = new AstNode(DeclKind(kw), name, lineNum);
            dnode.Attrs["end_line"] = TextUtils.LineAt(srcOriginal, endIdx);
            dnode.Attrs["keyword"] = kw;
            if (mods.Length > 0) dnode.Attrs["modifiers"] = mods;
            if (attrsList.Count > 0) dnode.Attrs["attributes"] = attrsList;
            if (retType.Length > 0) dnode.Attrs["return_type"] = retType;

            if (!string.IsNullOrEmpty(docstring))
                dnode.Add(new AstNode("DocString",
                    docstring.Trim('/').Trim('-').Trim()));

            var sigNode = dnode.Add(new AstNode("Signature"));
            foreach (var p in @params)
            {
                var pn = new AstNode("Param", p.Name);
                pn.Attrs["kind"] = p.Kind;
                pn.Attrs["type"] = (p.Type ?? "").Length > 120 ? p.Type![..120] : (p.Type ?? "");
                sigNode.Add(pn);
            }

            if (retType.Length > 0)
                dnode.Add(new AstNode("ReturnType",
                    retType.Length > 200 ? retType[..200] : retType));

            if (whereFields.Count > 0)
            {
                var whereNode = dnode.Add(new AstNode("WhereBlock"));
                whereNode.Attrs["fields"] = whereFields.Count;
                foreach (var wf in whereFields.Take(20))
                    whereNode.Add(new AstNode("Field", wf));
            }

            if (tactics.Count > 0)
            {
                var proofNode = dnode.Add(new AstNode("Proof"));
                proofNode.Attrs["tactic_count"] = tactics.Count;
                foreach (var t in tactics)
                {
                    var tnode = proofNode.Add(new AstNode("TacticStep", t.Tactic));
                    tnode.Attrs["line_offset"] = t.LineOffset;
                    foreach (var tkw in t.Keywords)
                        tnode.Add(new AstNode("TacticKeyword", tkw));
                }
            }

            declsNode.Add(dnode);
        }

        return root;
    }


    private static int FindBlockEnd(string src, int start)
    {
        var m = TopLevel.Match(src, start + 1);
        return m.Success ? m.Index : src.Length;
    }

    private static string ExtractDocstring(string textBefore)
    {
        var matches = Regex.Matches(textBefore, @"/--.*?-/", RegexOptions.Singleline);
        return matches.Count > 0 ? matches[^1].Value.Trim() : "";
    }

    private static string? ExtractProofBody(string block)
    {
        var m = Regex.Match(block, @":=\s*by\b");
        if (m.Success) return block[(m.Index + m.Length)..].Trim();
        m = Regex.Match(block, @"\bby\b");
        if (m.Success) return block[(m.Index + m.Length)..].Trim();
        return null;
    }

    private static string ExtractReturnType(string sig)
    {
        int depth = 0, colonPos = -1;
        for (int i = 0; i < sig.Length; i++)
        {
            char ch = sig[i];
            if (ch == '(' || ch == '{' || ch == '[') depth++;
            else if (ch == ')' || ch == '}' || ch == ']') depth--;
            else if (ch == ':' && depth == 0 && (i + 1 >= sig.Length || sig[i + 1] != '='))
                colonPos = i;
        }
        if (colonPos == -1) return "";
        string rest = sig[(colonPos + 1)..];
        rest = Regex.Split(rest, @":=|\bwhere\b|\bby\b")[0];
        return rest.Trim();
    }

    private static string DeclKind(string kw) => kw.Trim() switch
    {
        "theorem" => "Theorem",
        "lemma" => "Lemma",
        "def" => "Definition",
        "abbrev" => "Abbreviation",
        "instance" => "Instance",
        "class" => "Class",
        "structure" => "Structure",
        "inductive" => "Inductive",
        "grind_pattern" => "GrindPattern",
        "alias" => "Alias",
        _ => "Declaration",
    };
}


public static class AstStats
{
    public static Dictionary<string, object> Compute(AstNode root)
    {
        var freq = new Dictionary<string, int>();
        Walk(root, freq);

        var decls = root.Children.FirstOrDefault(c => c.Kind == "Declarations");
        var all = decls?.Children ?? new List<AstNode>();

        int thms = all.Count(d => d.Kind is "Theorem" or "Lemma");
        int defs = all.Count(d => d.Kind is "Definition" or "Abbreviation");
        int structs = all.Count(d => d.Kind is "Structure" or "Class" or "Inductive");
        int tactics = all.Sum(d => d.Attrs.TryGetValue("tactic_count", out var v) ? (int)v! : 0);
        int imports = root.Children.Where(c => c.Kind == "Imports").Sum(c => c.Children.Count);

        return new Dictionary<string, object>
        {
            ["total_nodes"] = root.Count(),
            ["kind_frequencies"] = freq.OrderByDescending(kv => kv.Value)
                                         .Take(20)
                                         .ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            ["theorems_lemmas"] = thms,
            ["definitions"] = defs,
            ["structures_classes"] = structs,
            ["total_tactic_steps"] = tactics,
            ["imports"] = imports,
        };
    }

    private static void Walk(AstNode n, Dictionary<string, int> freq)
    {
        freq[n.Kind] = freq.GetValueOrDefault(n.Kind) + 1;
        foreach (var c in n.Children) Walk(c, freq);
    }
}


public static class TreeRenderer
{
    private static readonly Dictionary<string, (ConsoleColor color, string icon)> Styles = new()
    {
        ["FileNode"] = (ConsoleColor.Cyan, "📄"),
        ["ModuleHeader"] = (ConsoleColor.Blue, "🗂"),
        ["ModuleDoc"] = (ConsoleColor.DarkBlue, "  /!-"),
        ["Imports"] = (ConsoleColor.Blue, "📦"),
        ["Import"] = (ConsoleColor.DarkBlue, "  ↳"),
        ["Opens"] = (ConsoleColor.Blue, "🔓"),
        ["Open"] = (ConsoleColor.DarkBlue, "  open"),
        ["Comments"] = (ConsoleColor.DarkGray, "💬"),
        ["DocComment"] = (ConsoleColor.Green, "  /--"),
        ["BlockComment"] = (ConsoleColor.DarkGray, "  /-"),
        ["LineComment"] = (ConsoleColor.DarkGray, "  --"),
        ["Scopes"] = (ConsoleColor.Magenta, "🏷"),
        ["NamespaceOpen"] = (ConsoleColor.Magenta, "  namespace"),
        ["SectionOpen"] = (ConsoleColor.Magenta, "  section"),
        ["Variables"] = (ConsoleColor.Yellow, "📐"),
        ["Variable"] = (ConsoleColor.DarkYellow, "  variable"),
        ["Param"] = (ConsoleColor.DarkCyan, "    ·"),
        ["Declarations"] = (ConsoleColor.Green, "📜"),
        ["Theorem"] = (ConsoleColor.White, "⊢ theorem"),
        ["Lemma"] = (ConsoleColor.White, "⊢ lemma"),
        ["Definition"] = (ConsoleColor.Cyan, "≡ def"),
        ["Abbreviation"] = (ConsoleColor.Cyan, "≡ abbrev"),
        ["Instance"] = (ConsoleColor.Yellow, "⚙ instance"),
        ["Class"] = (ConsoleColor.Magenta, "◈ class"),
        ["Structure"] = (ConsoleColor.Magenta, "◈ structure"),
        ["Inductive"] = (ConsoleColor.Magenta, "◈ inductive"),
        ["Proof"] = (ConsoleColor.Yellow, "  ∎ by"),
        ["TacticStep"] = (ConsoleColor.DarkYellow, "    ›"),
        ["TacticKeyword"] = (ConsoleColor.DarkYellow, "      τ"),
        ["Signature"] = (ConsoleColor.DarkCyan, "  (sig)"),
        ["ReturnType"] = (ConsoleColor.DarkGreen, "  : T"),
        ["WhereBlock"] = (ConsoleColor.DarkYellow, "  where"),
        ["Field"] = (ConsoleColor.DarkGray, "    ·"),
        ["DocString"] = (ConsoleColor.Green, "  /--"),
    };

    public static void Render(AstNode node, string prefix = "", bool isLast = true,
                               int maxDepth = 99, int depth = 0)
    {
        var (color, icon) = Styles.TryGetValue(node.Kind, out var s)
            ? s : (ConsoleColor.White, "○");

        string connector = depth == 0 ? "" : (isLast ? "└─ " : "├─ ");
        Console.Write(prefix + connector);

        Console.ForegroundColor = color;
        Console.Write($"{icon} {node.Kind}");
        Console.ResetColor();

        if (!string.IsNullOrEmpty(node.Value))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  " + node.Value[..Math.Min(80, node.Value.Length)]);
            Console.ResetColor();
        }

        foreach (var (k, v) in node.Attrs)
        {
            if (v is null || (v is string sv && sv.Length == 0)) continue;
            if (k is "attributes" or "modifiers") continue; // verbose demais
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  {k}=");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            string val = v is List<string> lst ? string.Join(",", lst.Take(3)) : v.ToString()!;
            Console.Write(val.Length > 50 ? val[..50] : val);
            Console.ResetColor();
        }
        Console.WriteLine();

        if (depth >= maxDepth) return;

        string childPrefix = prefix + (depth == 0 ? "" : (isLast ? "   " : "│  "));
        for (int i = 0; i < node.Children.Count; i++)
            Render(node.Children[i], childPrefix, i == node.Children.Count - 1,
                   maxDepth, depth + 1);
    }

    public static void PrintStats(Dictionary<string, object> s)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n══════════════════ Statistics ══════════════════");
        Console.ResetColor();

        PrintCard("Nós totais", s["total_nodes"], ConsoleColor.Cyan);
        PrintCard("Theorems/Lemmas", s["theorems_lemmas"], ConsoleColor.Green);
        PrintCard("Definitions", s["definitions"], ConsoleColor.Yellow);
        PrintCard("Tactic Steps", s["total_tactic_steps"], ConsoleColor.Magenta);
        PrintCard("Imports", s["imports"], ConsoleColor.Blue);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n  Frequência de nós:");
        Console.ResetColor();
        if (s["kind_frequencies"] is Dictionary<string, object> freq)
        {
            foreach (var (k, v) in freq)
            {
                Console.Write($"    {k,-30}");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(v);
                Console.ResetColor();
            }
        }
    }

    private static void PrintCard(string label, object value, ConsoleColor color)
    {
        Console.Write($"  {label,-25} ");
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ResetColor();
        Console.WriteLine();
    }
}


public static class FolderProcessor
{
    public record FileResult(
        string File,
        string RepoPath,
        string LeanModule,
        int NumLines,
        int TotalNodes,
        Dictionary<string, object> Stats,
        Dictionary<string, object?> Ast,
        string? Error
    );

    public static string FindRepoRoot(string startDir)
    {
        string? dir = Path.GetFullPath(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "lakefile.lean")) ||
                File.Exists(Path.Combine(dir, "lakefile.toml")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.GetFullPath(startDir);
    }

    public static string ToRepoPath(string absoluteFile, string repoRoot)
        => Path.GetRelativePath(repoRoot, absoluteFile).Replace('\\', '/');

    public static string ToLeanModule(string repoPath)
    {
        string s = repoPath.EndsWith(".lean") ? repoPath[..^5] : repoPath;
        return s.Replace('/', '.').Replace('\\', '.');
    }

    public static List<FileResult> ProcessFolder(string folderPath, int maxParallel = 0)
    {
        var files = Directory.GetFiles(folderPath, "*.lean", SearchOption.AllDirectories)
                             .OrderBy(f => f)
                             .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine("[lean_ast_local] Nenhum ficheiro .lean encontrado.");
            return new();
        }

        string repoRoot = FindRepoRoot(folderPath);
        Console.WriteLine($"[lean_ast_local] Raiz do repo : {repoRoot}");

        int workers = maxParallel > 0 ? maxParallel : Environment.ProcessorCount;
        Console.WriteLine($"[lean_ast_local] {files.Count} ficheiros .lean encontrados — {workers} workers");

        var results = new FileResult[files.Count];
        int done = 0;
        var lockObj = new object();

        var opts = new ParallelOptions { MaxDegreeOfParallelism = workers };
        Parallel.For(0, files.Count, opts, i =>
        {
            string path = files[i];
            string absPath = Path.GetFullPath(path);
            string repoPath = ToRepoPath(absPath, repoRoot);
            string leanModule = ToLeanModule(repoPath);

            FileResult result;
            try
            {
                var root = AstBuilder.Build(path);
                var stats = AstStats.Compute(root);
                result = new FileResult(
                    File: absPath,
                    RepoPath: repoPath,
                    LeanModule: leanModule,
                    NumLines: root.Attrs.TryGetValue("num_lines", out var nl) ? (int)nl! : 0,
                    TotalNodes: root.Count(),
                    Stats: stats,
                    Ast: root.ToDict(),
                    Error: null
                );
            }
            catch (Exception ex)
            {
                result = new FileResult(
                    File: absPath,
                    RepoPath: repoPath,
                    LeanModule: leanModule,
                    NumLines: 0,
                    TotalNodes: 0,
                    Stats: new(),
                    Ast: new(),
                    Error: ex.Message
                );
            }

            results[i] = result;

            int n = Interlocked.Increment(ref done);
            if (n % 50 == 0 || n == files.Count)
            {
                lock (lockObj)
                {
                    Console.Write($"\r  [{n}/{files.Count}]  {Path.GetFileName(path),-50}");
                }
            }
        });

        Console.WriteLine(); // newline após a linha de progresso
        return results.ToList();
    }

    public static Dictionary<string, object> AggregateStats(List<FileResult> results)
    {
        int totalFiles = results.Count;
        int failedFiles = results.Count(r => r.Error != null);
        int totalNodes = results.Sum(r => r.TotalNodes);
        int totalLines = results.Sum(r => r.NumLines);
        int totalThms = results.Sum(r => r.Stats.TryGetValue("theorems_lemmas", out var v) ? (int)v : 0);
        int totalDefs = results.Sum(r => r.Stats.TryGetValue("definitions", out var v) ? (int)v : 0);
        int totalTactics = results.Sum(r => r.Stats.TryGetValue("total_tactic_steps", out var v) ? (int)v : 0);
        int totalImports = results.Sum(r => r.Stats.TryGetValue("imports", out var v) ? (int)v : 0);

        var kindFreq = new Dictionary<string, int>();
        foreach (var r in results)
        {
            if (r.Stats.TryGetValue("kind_frequencies", out var kf) &&
                kf is Dictionary<string, object> freq)
            {
                foreach (var (k, v) in freq)
                    kindFreq[k] = kindFreq.GetValueOrDefault(k) + (int)v;
            }
        }

        return new Dictionary<string, object>
        {
            ["total_files"] = totalFiles,
            ["failed_files"] = failedFiles,
            ["total_nodes"] = totalNodes,
            ["total_lines"] = totalLines,
            ["theorems_lemmas"] = totalThms,
            ["definitions"] = totalDefs,
            ["total_tactic_steps"] = totalTactics,
            ["total_imports"] = totalImports,
            ["kind_frequencies"] = kindFreq.OrderByDescending(kv => kv.Value)
                                              .ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        };
    }
}


public static class Program
{
    public static int Main(string[] args)
    {
        string? target = null;   // ficheiro ou pasta
        string? output = null;
        bool showTree = false;
        bool statsOnly = false;
        bool noJson = false;
        int depth = 99;
        int maxParallel = 0;      // 0 = auto (nº de CPUs)

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output": case "-o": output = args[++i]; break;
                case "--show-tree": showTree = true; break;
                case "--stats": statsOnly = true; break;
                case "--no-json": noJson = true; break;
                case "--depth": case "-d": depth = int.Parse(args[++i]); break;
                case "--parallel": case "-p": maxParallel = int.Parse(args[++i]); break;
                default:
                    if (!args[i].StartsWith("-")) target = args[i];
                    break;
            }
        }

        if (target is null)
        {
            Console.Error.WriteLine("Usage: LeanAstLocal <file.lean | folder> [options]");
            Console.Error.WriteLine("  --output FILE      Output JSON file");
            Console.Error.WriteLine("  --parallel N       Nº de workers paralelos (padrão: nº CPUs)");
            Console.Error.WriteLine("  --show-tree        Renderizar árvore (só ficheiro único)");
            Console.Error.WriteLine("  --depth N          Profundidade máxima da árvore");
            Console.Error.WriteLine("  --stats            Show statistics");
            Console.Error.WriteLine("  --no-json          Do not write JSON");
            return 1;
        }

        if (Directory.Exists(target))
            return RunFolder(target, output, statsOnly, noJson, maxParallel);

        if (File.Exists(target))
            return RunSingleFile(target, output, showTree, statsOnly, noJson, depth);

        Console.Error.WriteLine($"[ERRO] Not found: {target}");
        return 1;
    }


    private static int RunSingleFile(string leanFile, string? output,
        bool showTree, bool statsOnly, bool noJson, int depth)
    {
        long sizeKb = new FileInfo(leanFile).Length / 1024;
        Console.WriteLine($"[lean_ast_local] A analisar: {leanFile}  ({sizeKb} KB)");

        AstNode root;
        try { root = AstBuilder.Build(leanFile); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERRO] Falha ao construir AST: {ex.Message}");
            return 2;
        }
        Console.WriteLine($"[lean_ast_local] AST construída — {root.Count()} nós.");

        var s = AstStats.Compute(root);
        if (statsOnly || showTree) TreeRenderer.PrintStats(s);

        if (showTree)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n══════════════════ AST (profundidade ≤ {depth}) ══════════════════");
            Console.ResetColor();
            TreeRenderer.Render(root, maxDepth: depth);
        }

        if (!noJson)
        {
            string outPath = output ?? Path.ChangeExtension(leanFile, ".ast.json");
            WriteJson(outPath, new Dictionary<string, object?>
            {
                ["file"] = Path.GetFullPath(leanFile),
                ["num_lines"] = root.Attrs.TryGetValue("num_lines", out var nl) ? nl : 0,
                ["total_nodes"] = root.Count(),
                ["stats"] = s,
                ["ast"] = root.ToDict(),
            });
            long outKb = new FileInfo(outPath).Length / 1024;
            Console.WriteLine($"[lean_ast_local] JSON gravado em: {outPath}  ({outKb} KB, {root.Count()} nós)");
        }
        return 0;
    }


    private static int RunFolder(string folder, string? output,
        bool statsOnly, bool noJson, int maxParallel)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"[lean_ast_local] Pasta: {Path.GetFullPath(folder)}");

        var results = FolderProcessor.ProcessFolder(folder, maxParallel);
        sw.Stop();

        var agg = FolderProcessor.AggregateStats(results);

        var failed = results.Where(r => r.Error != null).ToList();
        if (failed.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  {failed.Count} ficheiro(s) com erro:");
            foreach (var f in failed.Take(10))
                Console.WriteLine($"    {f.File}: {f.Error}");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n══════════════════ Summary ══════════════════");
        Console.ResetColor();
        Console.WriteLine($"  Ficheiros processados : {agg["total_files"]}  (falhas: {agg["failed_files"]})");
        Console.WriteLine($"  Linhas totais         : {agg["total_lines"]:N0}");
        Console.WriteLine($"  Nós AST totais        : {agg["total_nodes"]:N0}");
        Console.WriteLine($"  Teoremas / Lemas      : {agg["theorems_lemmas"]:N0}");
        Console.WriteLine($"  Definitions            : {agg["definitions"]:N0}");
        Console.WriteLine($"  Tactic Steps      : {agg["total_tactic_steps"]:N0}");
        Console.WriteLine($"  Tempo total           : {sw.Elapsed.TotalSeconds:F1}s");

        if (!noJson)
        {
            string outPath = output ?? Path.Combine(folder, "mathlib_ast.json");

            var data = new Dictionary<string, object?>
            {
                ["source_folder"] = Path.GetFullPath(folder),
                ["generated_at"] = DateTime.UtcNow.ToString("o"),
                ["elapsed_seconds"] = sw.Elapsed.TotalSeconds,
                ["aggregate_stats"] = agg,
                ["files"] = results.Select(r => new Dictionary<string, object?>
                {
                    ["file"] = r.File,
                    ["repo_path"] = r.RepoPath,
                    ["lean_module"] = r.LeanModule,
                    ["num_lines"] = r.NumLines,
                    ["total_nodes"] = r.TotalNodes,
                    ["error"] = r.Error,
                    ["stats"] = r.Stats,
                    ["ast"] = r.Error == null ? r.Ast : null,
                }).ToList(),
            };

            Console.Write("[lean_ast_local] A gravar JSON... ");
            WriteJson(outPath, data);
            long outMb = new FileInfo(outPath).Length / (1024 * 1024);
            Console.WriteLine($"pronto — {outPath}  ({outMb} MB)");
        }
        return 0;
    }


    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void WriteJson(string path, object data)
        => File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOpts), Encoding.UTF8);
}