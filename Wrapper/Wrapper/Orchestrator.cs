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
        private static int s_totalFiles = 0;
        private static int s_processedFiles = 0;

        // configurable limits
        private static int MaxDegreeOfParallelism => Math.Max(1, Environment.ProcessorCount);
        private static TimeSpan ParserTimeout => TimeSpan.FromSeconds(60);

        public static async Task<List<ProcessingResult>> ProcessRepositoryAsync(string repoPath)
        {
            var results = new List<ProcessingResult>();

            if (!Directory.Exists(repoPath))
                throw new DirectoryNotFoundException(repoPath);

            // gather relevant files and skip common build / vcs folders
            var allFiles = Directory.GetFiles(repoPath, "*.*", SearchOption.AllDirectories);
            var files = allFiles.Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".v" && ext != ".lean") return false;
                var lf = f.Replace('/', '\\').ToLowerInvariant();
                if (lf.Contains("\\bin\\") || lf.Contains("\\obj\\") || lf.Contains("\\.git\\") || lf.Contains("\\node_modules\\") || lf.Contains("\\.vs\\"))
                    return false;
                return true;
            }).ToArray();

            s_totalFiles = files.Length;
            s_processedFiles = 0;

            Console.WriteLine($"Found {s_totalFiles} source files (.v/.lean). Starting processing with max parallelism {MaxDegreeOfParallelism}...");

            var semaphore = new SemaphoreSlim(MaxDegreeOfParallelism);
            var writeLock = new object();
            string outPath = Path.Combine(repoPath, "output.json");

            // open stream and write array start for incremental JSON
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs);
            sw.WriteLine("[");

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

            var firstWritten = false;

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
                    var json = JsonSerializer.Serialize(res, jsonOptions);
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
                else
                {
                    res.Success = false;
                    res.Error = "Unsupported extension";
                    return res;
                }

                res.Ast = ast;
                res.Success = true;
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
