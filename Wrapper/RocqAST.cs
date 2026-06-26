using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wrapper
{
    public class RocqCommandAst
    {
        public string Command { get; set; } = "";
        public string Type { get; set; } = "";
        public List<string> Children { get; set; } = new List<string>();
        public object? Metadata { get; set; }
    }

    public class RocqParsedChild
    {
        public string? type { get; set; }
    }

    public class RocqParsedResult
    {
        public string? type { get; set; }
        public List<RocqParsedChild>? children { get; set; }
    }

    public static class RocqAstBuilder
    {
        // Build ASTs for each top-level command in the .v file
        public static List<RocqCommandAst> Build(string file)
        {
            if (!File.Exists(file)) throw new FileNotFoundException(file);

            // fast local parsing: split into commands and build lightweight ASTs
            // This avoids expensive round-trips to serAPI and is many times faster.
            var cmds = Split(File.ReadAllText(file));

            var result = new List<RocqCommandAst>(cmds.Count);

            for (int i = 0; i < cmds.Count; i++)
            {
                // Note: ParseAst currently uses only the command string, so we can skip serAPI.
                var parsed = ParseAst(cmds[i], null);
                var node = new RocqCommandAst
                {
                    Command = cmds[i],
                    Type = parsed.type ?? "Unknown",
                    Children = parsed.children != null ? parsed.children.ConvertAll(c => c.type ?? "") : new List<string>(),
                    Metadata = new { fast = true }
                };
                result.Add(node);
            }

            return result;
        }

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.Error.WriteLine("Usage: RocqAST <file.v>");
                    return 2;
                }

                var file = args[0];
                var list = Build(file);
                Console.WriteLine(JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.ToString());
                return 1;
            }
        }

        // start SerAPI
        static Process StartSerapi()
        {
            var p = new Process();

            p.StartInfo.FileName = "wsl.exe";
            p.StartInfo.Arguments =
                "opam exec -- sertop --implicit --printer=serapi";

            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;

            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;

            p.Start();

            // wait SerAPI start
            System.Threading.Thread.Sleep(1500);

            return p;
        }

        // send command safely
        static string TryQuery(ref Process p, string cmd)
        {
            // restart if crashed
            if (p == null || p.HasExited)
                p = StartSerapi();

            try
            {
                return Query(p, cmd);
            }
            catch
            {
                // restart and try again
                p = StartSerapi();
                return Query(p, cmd);
            }
        }

        // talk with SerAPI
        static string Query(Process p, string cmd)
        {
            if (p.HasExited)
                return "SERAPI_DEAD";

            string safe = cmd.Replace("\"", "\\\"");

            // send code
            p.StandardInput.WriteLine($"(Add () \"{safe}\")");
            p.StandardInput.Flush();

            string add = Read(p);

            var m = Regex.Match(add, @"Added\s+(\d+)");
            if (!m.Success)
                return add;

            string sid = m.Groups[1].Value;

            // ask for AST
            p.StandardInput.WriteLine($"(Query ((sid {sid})) (Ast))");
            p.StandardInput.Flush();

            return Read(p);
        }

        // read SerAPI output
        static string Read(Process p)
        {
            var sb = new StringBuilder();

            while (true)
            {
                var line = p.StandardOutput.ReadLine();
                if (line == null) break;

                sb.AppendLine(line);

                if (line.Contains("Completed") || line.Contains("CoqExn"))
                    break;
            }

            return sb.ToString();
        }

        // close process
        static void SafeQuit(Process p)
        {
            try
            {
                if (p != null && !p.HasExited)
                {
                    p.StandardInput.WriteLine("(Quit)");
                    p.StandardInput.Flush();
                }
            }
            catch { }
        }

        // split Coq code into commands
        static List<string> Split(string code)
        {
            var cmds = new List<string>();
            var cur = new StringBuilder();

            bool inString = false;
            int comment = 0;

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];

                // start comment
                if (!inString && i + 1 < code.Length && code[i] == '(' && code[i + 1] == '*')
                    comment++;

                // end comment
                if (!inString && i + 1 < code.Length && code[i] == '*' && code[i + 1] == ')')
                    comment--;

                // string toggle
                if (c == '"') inString = !inString;

                cur.Append(c);

                // end of command
                if (c == '.' && !inString && comment == 0)
                {
                    var cmd = cur.ToString().Trim();
                    if (cmd.Length > 0)
                        cmds.Add(cmd);

                    cur.Clear();
                }
            }

            return cmds;
        }

        // very small heuristic parser that tries to create a simple AST object
        static RocqParsedResult ParseAst(string cmd, string serapiOutput)
        {
            // try to detect a top-level keyword like Definition, Theorem, Fixpoint, Inductive
            var m = Regex.Match(cmd, "^(Definition|Theorem|Fixpoint|Inductive|Compute)\\b");
            string rootType = m.Success ? m.Groups[1].Value : "Unknown";

            // find tokens that look like constructors / node names (start with uppercase)
            var childMatches = Regex.Matches(cmd, "\\b([A-Z][a-zA-Z0-9_]*)\\b");
            var children = new List<RocqParsedChild>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match cm in childMatches)
            {
                var name = cm.Groups[1].Value;
                if (name == rootType) continue;
                if (seen.Add(name))
                    children.Add(new RocqParsedChild { type = name });
            }

            return new RocqParsedResult { type = rootType, children = children };
        }
    }
}