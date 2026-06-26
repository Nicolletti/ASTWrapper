using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Wrapper
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            // simple CLI: supports multiple --include-path <path> or --include-path=<path>
            var includePaths = new List<string>();
            string path = null;
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--include-path=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = a.Substring("--include-path=".Length).Trim('"');
                    if (!string.IsNullOrEmpty(val)) includePaths.Add(val);
                }
                else if (string.Equals(a, "--include-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    includePaths.Add(args[i + 1]);
                    i++;
                }
                else if (!a.StartsWith("-"))
                {
                    if (path == null) path = a;
                }
            }

            if (string.IsNullOrEmpty(path))
            {
                Console.Write("Repository path: ");
                path = Console.ReadLine();
            }

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                Console.WriteLine("Repository not found.");
                return 1;
            }

            try
            {
                if (includePaths.Count > 0)
                {
                    // join include paths using platform path separator
                    var join = string.Join(System.IO.Path.PathSeparator.ToString(), includePaths);
                    Environment.SetEnvironmentVariable("AGDA_INCLUDE_PATH", join);
                }

                var results = await Orchestrator.ProcessRepositoryAsync(path);

                Console.WriteLine($"Processed: {results.Count}");
                var success = results.Count(r => r.Success);
                Console.WriteLine($"Success: {success}, Failures: {results.Count - success}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return 2;
            }
        }
    }
}
