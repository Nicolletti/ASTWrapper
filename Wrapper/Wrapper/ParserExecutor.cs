using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;

namespace Wrapper
{
    public static class ParserExecutor
    {
        // Usa diretamente os parsers locais: LeanAST (AstBuilder) e RocqAST (RocqAstBuilder)
        public static Task<UnifiedASTNode> ExecuteRocqParserAsync(string filePath)
        {
            // RocqAstBuilder.Build(filePath) deve retornar uma coleção de comandos/objetos com
            // propriedades: Type, Command, Metadata e Children
            var cmds = RocqAstBuilder.Build(filePath);

            var root = new UnifiedASTNode
            {
                NodeType = NodeType.Other,
                Name = Path.GetFileName(filePath),
                Language = "Rocq",
                Metadata = new Dictionary<string, object> { ["source"] = filePath }
            };

            foreach (var c in cmds)
            {
                var cmdText = c.Command as string ?? string.Empty;
                var child = new UnifiedASTNode
                {
                    NodeType = MapStringToNodeType(c.Type as string),
                    Name = cmdText.Length > 80 ? cmdText.Substring(0, 80) : cmdText,
                    Language = "Rocq",
                    Metadata = c.Metadata as IDictionary<string, object> ?? new Dictionary<string, object>()
                };

                if (c.Children != null)
                {
                    foreach (var ch in c.Children)
                        child.Children.Add(new UnifiedASTNode { NodeType = NodeType.Other, Name = ch as string, Language = "Rocq" });
                }

                root.Children.Add(child);
            }

            return Task.FromResult(root);
        }

        // Parse a JSON file on disk that already contains the AST (e.g. agda --print-ast-json output)
        public static Task<(UnifiedASTNode Ast, string RawJson)> ParseAgdaJsonFileAsync(string filePath)
        {
            var text = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(text);
            var rootElem = doc.RootElement;
            if (rootElem.ValueKind == JsonValueKind.Object && rootElem.TryGetProperty("ast", out var astProp))
                rootElem = astProp;

            UnifiedASTNode root = new UnifiedASTNode
            {
                NodeType = NodeType.Other,
                Name = Path.GetFileName(filePath),
                Language = "Agda",
                Metadata = new Dictionary<string, object> { ["source"] = filePath }
            };

            UnifiedASTNode Convert(JsonElement el)
            {
                string kind = null;
                string name = null;

                    if (el.ValueKind == JsonValueKind.String)
                    {
                        name = el.GetString();
                        return new UnifiedASTNode { NodeType = NodeType.Other, Name = name, Language = "Agda", Metadata = ParseJsonStringToObject(el.GetRawText()) };
                    }

                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
                        kind = k.GetString();
                    if (el.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                        name = v.GetString();
                    else if (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        name = n.GetString();

                    var node = new UnifiedASTNode
                    {
                        NodeType = MapStringToNodeType(kind ?? name ?? string.Empty),
                        Name = string.IsNullOrEmpty(name) ? (kind ?? Path.GetFileName(filePath)) : (name.Length > 200 ? name.Substring(0, 200) : name),
                        Language = "Agda",
                        Metadata = ParseJsonStringToObject(el.GetRawText())
                    };

                    if (el.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var childEl in ch.EnumerateArray()) node.Children.Add(Convert(childEl));
                    }
                    else if (el.TryGetProperty("declarations", out var decls) && decls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var childEl in decls.EnumerateArray()) node.Children.Add(Convert(childEl));
                    }

                    return node;
                }

                return new UnifiedASTNode { NodeType = NodeType.Other, Name = el.ToString(), Language = "Agda", Metadata = ParseJsonStringToObject(el.GetRawText()) };
            }

            if (rootElem.ValueKind == JsonValueKind.Object && rootElem.TryGetProperty("children", out var topChildren) && topChildren.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in topChildren.EnumerateArray()) root.Children.Add(Convert(child));
            }
            else if (rootElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in rootElem.EnumerateArray()) root.Children.Add(Convert(child));
            }
            else
            {
                root.Children.Add(Convert(rootElem));
            }

            return Task.FromResult(((UnifiedASTNode)root, text));
        }

        public static async Task<(UnifiedASTNode Ast, string RawJson)> ExecuteAgdaParserAsync(string filePath, int fallbackAttempt = 0, string includePath = null)
        {
            // allow configuring include paths via AGDA_INCLUDE_PATH environment variable when not provided
            if (string.IsNullOrEmpty(includePath))
            {
                try { includePath = Environment.GetEnvironmentVariable("AGDA_INCLUDE_PATH"); } catch { includePath = null; }
            }
            // Call external agda tool with --print-ast-json and capture stdout
            var agdaExe = Environment.GetEnvironmentVariable("AGDA_EXECUTABLE");
            if (string.IsNullOrWhiteSpace(agdaExe)) agdaExe = "agda";

            var args = $"--ignore-interfaces --print-ast-json \"{filePath}\"";
            if (!string.IsNullOrEmpty(includePath))
            {
                args += $" --include-path \"{includePath}\"";
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = agdaExe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // set working directory to the file's folder so Agda resolves module name -> file path correctly
            try
            {
                var wd = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(wd)) startInfo.WorkingDirectory = wd;
            }
            catch
            {
                // ignore and proceed with default working directory
            }

            using var proc = System.Diagnostics.Process.Start(startInfo);
            if (proc == null)
                throw new InvalidOperationException("Failed to start agda process");

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                // Try automatic fallback for ModuleNameDoesntMatchFileName once
                if (fallbackAttempt == 0 && !string.IsNullOrEmpty(stderr) && stderr.IndexOf("ModuleNameDoesntMatchFileName", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // attempt fallback; if it returns non-null, return its result
                    var fb = await TryHandleModuleNameMismatchFallbackAsync(filePath, stderr, stdout);
                    if (fb != null)
                        return fb.Value;
                }

                // include both stderr and stdout in the exception message (truncate if too long)
                string Trunc(string s, int max = 2000) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "...[truncated]");
                var msg = $"Agda failed (code {proc.ExitCode}). Executable: {agdaExe}\n--- stderr ---\n{Trunc(stderr)}\n--- stdout ---\n{Trunc(stdout)}";
                throw new InvalidOperationException(msg);
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(stdout);

                JsonElement rootElem = doc.RootElement;

                if (rootElem.ValueKind == System.Text.Json.JsonValueKind.Object && rootElem.TryGetProperty("ast", out var astProp))
                {
                    rootElem = astProp;
                }



                UnifiedASTNode root = new UnifiedASTNode
                {
                    NodeType = NodeType.Other,
                    Name = Path.GetFileName(filePath),
                    Language = "Agda",
                    Metadata = new Dictionary<string, object> { ["source"] = filePath }
                };

                UnifiedASTNode Convert(JsonElement el)
                {
                    string kind = null;
                    string name = null;

                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        name = el.GetString();
                        return new UnifiedASTNode { NodeType = NodeType.Other, Name = name, Language = "Agda", Metadata = ParseJsonStringToObject(el.GetRawText()) };
                    }

                    if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (el.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String)
                            kind = k.GetString();
                        if (el.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                            name = v.GetString();
                        else if (el.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                            name = n.GetString();

                        var node = new UnifiedASTNode
                        {
                            NodeType = MapStringToNodeType(kind ?? name ?? string.Empty),
                            Name = string.IsNullOrEmpty(name) ? (kind ?? Path.GetFileName(filePath)) : (name.Length > 200 ? name.Substring(0, 200) : name),
                            Language = "Agda",
                            Metadata = ParseJsonStringToObject(el.GetRawText())
                        };

                        if (el.TryGetProperty("children", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var childEl in ch.EnumerateArray())
                            {
                                node.Children.Add(Convert(childEl));
                            }
                        }
                        else
                        {
                            if (el.TryGetProperty("declarations", out var decls) && decls.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var childEl in decls.EnumerateArray()) node.Children.Add(Convert(childEl));
                            }
                        }

                        return node;
                    }

                    return new UnifiedASTNode { NodeType = NodeType.Other, Name = el.ToString(), Language = "Agda", Metadata = ParseJsonStringToObject(el.GetRawText()) };
                }

                if (rootElem.ValueKind == System.Text.Json.JsonValueKind.Object && rootElem.TryGetProperty("children", out var topChildren) && topChildren.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var child in topChildren.EnumerateArray())
                        root.Children.Add(Convert(child));
                }
                else if (rootElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var child in rootElem.EnumerateArray())
                        root.Children.Add(Convert(child));
                }
                else
                {
                    root.Children.Add(Convert(rootElem));
                }

                return (root, stdout);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException("Failed to parse Agda JSON output: " + ex.Message);
            }
        }

        // When Agda complains ModuleNameDoesntMatchFileName try a fallback:
        // - parse the suggested path from stderr
        // - if the suggested file exists, re-run agda on it
        // - otherwise create a temporary folder mirroring the suggested path, copy the file there and run agda
        private static async Task<(UnifiedASTNode Ast, string RawJson)?> TryHandleModuleNameMismatchFallbackAsync(string originalFilePath, string stderr, string stdout)
        {
            try
            {
                var s = stderr ?? string.Empty;
                // prepare a debug log file for this fallback attempt
                var debugLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wrapper_agda_fallback_log_" + Guid.NewGuid().ToString("N") + ".log");
                void AppendLog(string line)
                {
                    try { System.IO.File.AppendAllText(debugLog, DateTime.UtcNow.ToString("o") + " - " + line + System.Environment.NewLine); } catch { }
                }
                AppendLog("FALLBACK START");
                AppendLog("OriginalFile: " + originalFilePath);
                AppendLog("Captured stderr:\n" + (stderr ?? ""));
                AppendLog("Captured stdout:\n" + (stdout ?? ""));
                // first, try to find an absolute suggested .AGDA path in stderr
                var pathRx = new System.Text.RegularExpressions.Regex(@"([A-Za-z]:\\[^\r\n]+?\\[^\\\s]+\.AGDA)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var pm = pathRx.Match(s);
                if (pm.Success)
                {
                    var suggestedPath = pm.Groups[1].Value.Trim();
                    AppendLog("Suggested absolute path found: " + suggestedPath);
                    try
                    {
                        var suggestedFull = System.IO.Path.GetFullPath(suggestedPath);
                        var suggestedDir = System.IO.Path.GetDirectoryName(suggestedFull) ?? System.IO.Path.GetDirectoryName(originalFilePath);
                        var createdDirs = new List<string>();
                        var createdFile = false;
                        var createdDirList = new List<string>();
                        // create directories if they don't exist
                        if (!System.IO.Directory.Exists(suggestedDir))
                        {
                            System.IO.Directory.CreateDirectory(suggestedDir);
                            createdDirList.Add(suggestedDir);
                        }
                        var targetFile = System.IO.Path.Combine(suggestedDir, System.IO.Path.GetFileName(suggestedFull));
                        System.IO.File.Copy(originalFilePath, targetFile, overwrite: true);
                        createdFile = true;

                        try
                        {
                            Console.WriteLine($"Wrapper: attempting Agda fallback by copying {originalFilePath} -> {targetFile}");
                            var result = await ExecuteAgdaParserAsync(targetFile, 1, System.IO.Path.GetDirectoryName(targetFile));
                            return result;
                        }
                        finally
                        {
                            try { if (createdFile) System.IO.File.Delete(targetFile); } catch { }
                            try { foreach (var d in createdDirList) System.IO.Directory.Delete(d, recursive: true); } catch { }
                        }
                    }
                    catch
                    {
                        // fall through and try module-name mapping
                    }
                }

                // try extract module name from backticks: `Module.Name.Here`
                var mnameRx = new System.Text.RegularExpressions.Regex("`([^`]+)`");
                var mm = mnameRx.Match(s);
                string moduleName = null;
                if (mm.Success) moduleName = mm.Groups[1].Value.Trim();

                if (string.IsNullOrEmpty(moduleName))
                {
                    // fallback: look for the phrase "top\nlevel module does not match" and try to read the dot name before it
                    var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var idx = line.IndexOf("of the top", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            // try to find a dotted name in the line
                            var dotRx = new System.Text.RegularExpressions.Regex(@"[A-Za-z0-9_\.]+\.[A-Za-z0-9_\.]+");
                            var d = dotRx.Match(line);
                            if (d.Success) { moduleName = d.Value; break; }
                        }
                    }
                }

                if (string.IsNullOrEmpty(moduleName))
                {
                    AppendLog("Could not determine module name from stderr");
                    AppendLog("FALLBACK END");
                    return null;
                }

                // map module name to path
                var relPath = moduleName.Replace('.', System.IO.Path.DirectorySeparatorChar) + ".agda";

                // choose a candidate root to copy: walk ancestors and prefer one that already contains the first module segment
                var firstSegment = moduleName.Split(new[] {'.'}, StringSplitOptions.RemoveEmptyEntries)[0];
                var origDir = System.IO.Path.GetDirectoryName(originalFilePath) ?? string.Empty;
                string candidateRoot = null;
                var cur = origDir;
                while (!string.IsNullOrEmpty(cur))
                {
                    try
                    {
                        if (System.IO.Directory.Exists(System.IO.Path.Combine(cur, firstSegment))) { candidateRoot = cur; break; }
                    }
                    catch { }
                    var parent = System.IO.Directory.GetParent(cur);
                    cur = parent?.FullName;
                }
                if (string.IsNullOrEmpty(candidateRoot)) candidateRoot = origDir;

                // Create temp tree and copy the candidate root into it
                var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wrapper_agda_fallback_" + Guid.NewGuid().ToString("N"));
                try
                {
                    void CopyDir(string sourceDir, string destDir)
                    {
                        var di = new System.IO.DirectoryInfo(sourceDir);
                        if (!di.Exists) return;
                        System.IO.Directory.CreateDirectory(destDir);
                        foreach (var file in di.GetFiles())
                        {
                            var targetFile = System.IO.Path.Combine(destDir, file.Name);
                            try { file.CopyTo(targetFile, overwrite: true); } catch { }
                        }
                        foreach (var sub in di.GetDirectories())
                        {
                            CopyDir(sub.FullName, System.IO.Path.Combine(destDir, sub.Name));
                        }
                    }

                    CopyDir(candidateRoot, tempRoot);

                    // ensure target path exists and copy the original file into module path so Agda finds it
                    var targetFile2 = System.IO.Path.Combine(tempRoot, relPath);
                    var targetDir2 = System.IO.Path.GetDirectoryName(targetFile2) ?? tempRoot;
                    System.IO.Directory.CreateDirectory(targetDir2);
                    System.IO.File.Copy(originalFilePath, targetFile2, overwrite: true);

                    Console.WriteLine($"Wrapper: attempting Agda fallback by copying tree {candidateRoot} -> {tempRoot} and placing {originalFilePath} at {targetFile2}");
                    var result = await ExecuteAgdaParserAsync(targetFile2, 1, tempRoot);
                    return result;
                }
                finally
                {
                    try { System.IO.Directory.Delete(tempRoot, recursive: true); } catch { }
                }
            }
            catch
            {
                return null;
            }
        }

        // Call agda and return raw stdout (the JSON). Throws on non-zero exit like before but returns both stdout/stderr via exception message
        public static async Task<string> GetRawAstJsonFromAgda(string filePath)
        {
            var agdaExe = Environment.GetEnvironmentVariable("AGDA_EXECUTABLE");
            if (string.IsNullOrWhiteSpace(agdaExe)) agdaExe = "agda";

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = agdaExe,
                Arguments = $"--ignore-interfaces --print-ast-json \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                var wd = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(wd)) startInfo.WorkingDirectory = wd;
            }
            catch { }

            using var proc = System.Diagnostics.Process.Start(startInfo);
            if (proc == null) throw new InvalidOperationException("Failed to start agda process");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                string Trunc(string s, int max = 2000) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "...[truncated]");
                throw new InvalidOperationException($"Agda failed (code {proc.ExitCode}). Executable: {agdaExe}\n--- stderr ---\n{Trunc(stderr)}\n--- stdout ---\n{Trunc(stdout)}");
            }

            return stdout;
        }

        // Run agda and always return exit code, stdout and stderr (does not throw on non-zero)
        public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAgdaCaptureOutputAsync(string filePath, string includePath = null)
        {
            var agdaExe = Environment.GetEnvironmentVariable("AGDA_EXECUTABLE");
            if (string.IsNullOrWhiteSpace(agdaExe)) agdaExe = "agda";

            var args = $"--ignore-interfaces --print-ast-json \"{filePath}\"";
            if (!string.IsNullOrEmpty(includePath))
            {
                var parts = includePath.Split(System.IO.Path.PathSeparator);
                foreach (var p in parts)
                {
                    var t = p?.Trim();
                    if (string.IsNullOrEmpty(t)) continue;
                    args += $" --include-path \"{t}\"";
                }
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = agdaExe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                var wd = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(wd)) startInfo.WorkingDirectory = wd;
            }
            catch { }

            using var proc = System.Diagnostics.Process.Start(startInfo);
            if (proc == null) return (-1, string.Empty, "Failed to start agda process");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, stderr);
        }

        public static Task<UnifiedASTNode> ExecuteLeanParserAsync(string filePath)
        {
            // AstBuilder.Build(filePath) retorna a raiz do AST do Lean
            var leanRoot = AstBuilder.Build(filePath);

            UnifiedASTNode Convert(dynamic node)
            {
                var u = new UnifiedASTNode
                {
                    NodeType = MapStringToNodeType((string)node.Kind),
                    Name = node.Value ?? Path.GetFileName(filePath),
                    Language = "Lean",
                    Metadata = node.ToDict() as IDictionary<string, object> ?? new Dictionary<string, object>()
                };

                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                        u.Children.Add(Convert(child));
                }

                return u;
            }

            var root = Convert(leanRoot);
            root.Language = "Lean";
            return Task.FromResult(root);
        }

        // Convert a JSON fragment string into CLR objects (dictionary, lists, primitives)
        private static object ParseJsonStringToObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return ConvertElement(doc.RootElement);
            }
            catch
            {
                return json;
            }

            static object ConvertElement(JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.Object:
                        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in el.EnumerateObject()) dict[p.Name] = ConvertElement(p.Value);
                        return dict;
                    case JsonValueKind.Array:
                        var list = new List<object>();
                        foreach (var i in el.EnumerateArray()) list.Add(ConvertElement(i));
                        return list;
                    case JsonValueKind.String:
                        return el.GetString();
                    case JsonValueKind.Number:
                        if (el.TryGetInt64(out var l)) return l;
                        if (el.TryGetDouble(out var d)) return d;
                        return el.GetRawText();
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Null:
                        return null;
                    default:
                        return el.GetRawText();
                }
            }
        }

        private static NodeType MapStringToNodeType(string s)
        {
            if (string.IsNullOrEmpty(s)) return NodeType.Other;
            s = s.ToLowerInvariant();
            if (s.Contains("theorem")) return NodeType.Theorem;
            if (s.Contains("lemma")) return NodeType.Lemma;
            if (s.Contains("def") || s.Contains("definition")) return NodeType.Definition;
            if (s.Contains("fixpoint") || s.Contains("fix")) return NodeType.Fixpoint;
            if (s.Contains("inductive")) return NodeType.Inductive;
            return NodeType.Other;
        }
    }
}
