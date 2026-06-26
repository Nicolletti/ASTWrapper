using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;

namespace Wrapper
{
    public static class Orchestrator
    {
        // repository root currently being processed (used by ParserExecutor for include-paths)
        public static string RepoRoot { get; private set; }
        private static int s_totalFiles = 0;
        private static int s_processedFiles = 0;

        // configurable limits
        // - WRAPPER_MAX_PARALLELISM: optional env var to cap concurrency (int)
        // - Default concurrency: min(Environment.ProcessorCount, 4) to avoid thrashing Agda
        private static int MaxDegreeOfParallelism
        {
            get
            {
                try
                {
                    var env = Environment.GetEnvironmentVariable("WRAPPER_MAX_PARALLELISM");
                    if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var v) && v > 0)
                    {
                        return Math.Min(v, Math.Max(1, Environment.ProcessorCount));
                    }
                }
                catch { }
                return Math.Max(1, Math.Min(4, Environment.ProcessorCount));
            }
        }

        // Parser timeout in seconds; can be overridden by WRAPPER_PARSER_TIMEOUT_SECONDS env var
        private static TimeSpan ParserTimeout
        {
            get
            {
                try
                {
                    var env = Environment.GetEnvironmentVariable("WRAPPER_PARSER_TIMEOUT_SECONDS");
                    if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var s) && s > 0)
                    {
                        return TimeSpan.FromSeconds(s);
                    }
                }
                catch { }
                return TimeSpan.FromSeconds(180); // default to 180s to allow slower parsing
            }
        }

        public static async Task<List<ProcessingResult>> ProcessRepositoryAsync(string repoPath)
        {
            RepoRoot = repoPath;
            var results = new List<ProcessingResult>();

            if (!Directory.Exists(repoPath))
                throw new DirectoryNotFoundException(repoPath);

            // gather relevant files and skip common build / vcs folders
            var allFiles = Directory.GetFiles(repoPath, "*.*", SearchOption.AllDirectories);
            var files = allFiles.Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                var lf = f.Replace('/', '\\').ToLowerInvariant();
                // only process source files: .v, .lean, .agda
                if (ext != ".v" && ext != ".lean" && ext != ".agda")
                    return false;

                if (lf.Contains("\\bin\\") || lf.Contains("\\obj\\") || lf.Contains("\\.git\\") || lf.Contains("\\node_modules\\") || lf.Contains("\\.vs\\"))
                    return false;
                return true;
            }).ToArray();

            s_totalFiles = files.Length;
            s_processedFiles = 0;

            Console.WriteLine($"Found {s_totalFiles} source files (.v/.lean/.agda). Starting processing with max parallelism {MaxDegreeOfParallelism}...");

            var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);
            var writeLock = new object();
            string outPath = Path.Combine(repoPath, "output.json");

            // open stream and write array start for incremental JSON
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs);
            sw.WriteLine("[");

            var firstWritten = false;
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = null, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            var tasks = files.Select(async file =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // run parser with timeout
                    ProcessingResult res = null;
                    try
                    {
                        var task = ProcessFileAsync(file);
                        var completed = await Task.WhenAny(task, Task.Delay(ParserTimeout));
                        if (completed == task)
                        {
                            res = await task;
                        }
                        else
                        {
                            res = new ProcessingResult { FileName = file, Success = false, Error = "Parser timeout", ProcessingTime = (long)ParserTimeout.TotalMilliseconds };
                        }
                    }
                    catch (Exception ex)
                    {
                        res = new ProcessingResult { FileName = file, Success = false, Error = ex.ToString() };
                    }

                    // write result incrementally
                    var json = JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = null });
                    lock (writeLock)
                    {
                        if (firstWritten) sw.WriteLine(",");
                        sw.Write(json);
                        sw.Flush();
                        firstWritten = true;
                    }

                    lock (results)
                    {
                        results.Add(res);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);

            // close JSON array
            sw.WriteLine();
            sw.WriteLine("]");
            sw.Flush();

            return results.ToList();
        }

        private static async Task<ProcessingResult> ProcessFileAsync(string file)
        {
            var sw = Stopwatch.StartNew();
            var res = new ProcessingResult { FileName = file };

            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                UnifiedASTNode ast = null;

                if (ext == ".v")
                {
                    res.Language = "Rocq";
                    ast = await ParserExecutor.ExecuteRocqParserAsync(file);
                }
                else if (ext == ".lean")
                {
                    res.Language = "Lean";
                    ast = await ParserExecutor.ExecuteLeanParserAsync(file);
                }
                else if (ext == ".agda")
                {
                    res.Language = "Agda";
                    // Always attempt to capture Agda stdout JSON even when Agda exits with error
                    var include = Orchestrator.RepoRoot;
                    var run = await ParserExecutor.RunAgdaCaptureOutputAsync(file, include);
                    if (!string.IsNullOrEmpty(run.Stdout))
                    {
                        try
                        {
                            // try to parse stdout as AST JSON; if it parses, use ParseAgdaJsonFileAsync logic
                            var tmp = Path.GetTempFileName();
                            File.WriteAllText(tmp, run.Stdout);
                            var pair = await ParserExecutor.ParseAgdaJsonFileAsync(tmp);
                            ast = pair.Ast;
                            res.RawAstJson = pair.RawJson;
                            try { File.Delete(tmp); } catch { }
                        }
                        catch
                        {
                            // ignore parse errors
                        }
                    }
                    if (run.ExitCode != 0)
                    {
                        res.Success = false;
                        res.Error = $"Agda exited with code {run.ExitCode}. Stderr: {run.Stderr}";
                        res.ProcessingTime = 0;
                        return res;
                    }
                }

                else
                {
                    res.Success = false;
                    res.Error = "Unsupported extension";
                    return res;
                }

                res.Ast = ast;
                res.Success = true;

                // If we have raw AST JSON from the parser, write a pretty-printed copy
                try
                {
                    if (!string.IsNullOrEmpty(res.RawAstJson))
                    {
                        using var jd = JsonDocument.Parse(res.RawAstJson);
                        var pretty = JsonSerializer.Serialize(jd.RootElement, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = null });
                        var dir = Path.GetDirectoryName(file) ?? ".";
                        var prettyPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".ast.pretty.json");
                        File.WriteAllText(prettyPath, pretty);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: failed to write pretty AST JSON for {file}: {ex.Message}");
                    if (string.IsNullOrEmpty(res.Error)) res.Error = "Failed to write pretty AST JSON: " + ex.Message;
                }
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Error = ex.ToString();
            }
            finally
            {
                sw.Stop();
                res.ProcessingTime = sw.ElapsedMilliseconds;

                int n = Interlocked.Increment(ref s_processedFiles);
                // simple progress message
                Console.WriteLine($"[{n}/{s_totalFiles}] {Path.GetFileName(file)} -> {res.Language ?? "?"}  Time: {res.ProcessingTime}ms  Success: {res.Success}");
            }

            return res;
        }
    }
}
