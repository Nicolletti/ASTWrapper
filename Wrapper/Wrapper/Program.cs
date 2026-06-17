using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Wrapper
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.Write("Repository path: ");
            var path = args.Length > 0 ? args[0] : Console.ReadLine();

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                Console.WriteLine("Repository not found.");
                return 1;
            }

            try
            {
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
