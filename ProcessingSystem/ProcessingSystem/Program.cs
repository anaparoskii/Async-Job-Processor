using ProcessingSystem.Helper;
using ProcessingSystem.Models;
using ProcessingSystem.Services;

namespace ProcessingSystem.ConsoleApp
{
    class Program
    {
        private static readonly object _consoleLock = new object();
        private static int _rejectedJobs = 0;
        private static int _submittedJobs = 0;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Processing System - Starting...");

            string configPath = "SystemConfig.xml";
            SystemConfig config;

            try
            {
                config = ConfigLoader.LoadConfig(configPath);
                Console.WriteLine($"Config loaded: {config.WorkerCount} workers, queue size {config.MaxQueueSize}, {config.Jobs?.Count ?? 0} initial jobs");
                for (int i = 0; i < config.Jobs?.Count; i++)
                {
                    Console.WriteLine(config.Jobs[i].Id + " " + config.Jobs[i].Type);
                }
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine($"Config file not found at '{configPath}'. Exiting.");
                return;
            }

            var system = new ProcessingSystem.Services.ProcessingSystem(config);
            var cts = new CancellationTokenSource();

            var producerTasks = Enumerable.Range(0, config.WorkerCount)
                .Select(id => Task.Run(() => ProduceJobsAsync(system, id, cts.Token)))
                .ToList();

            Console.WriteLine($"\n{config.WorkerCount} producers running. Press Q to quit, S for status, T for top jobs, G for job details.\n");

            while (true)
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(100);
                    continue;
                }

                var key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.Q) break;
                if (key == ConsoleKey.S) ShowStatus(system);
                if (key == ConsoleKey.T) ShowTopJobs(system);
                if (key == ConsoleKey.G) await ShowJobDetails(system);
            }

            Console.WriteLine("\nStopping producers...");
            cts.Cancel();

            try { await Task.WhenAll(producerTasks); }
            catch (OperationCanceledException) { }

            Console.WriteLine("Draining remaining jobs...");
            await Task.Delay(3000);

            await system.ShutdownAsync();

            Console.WriteLine($"\nDone. Submitted: {_submittedJobs}, Rejected: {_rejectedJobs}");
            Console.ReadKey();
        }

        private static async Task ProduceJobsAsync(ProcessingSystem.Services.ProcessingSystem system, int producerId, CancellationToken token)
        {
            var rng = new Random(producerId * 10000 + Environment.TickCount);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(rng.Next(500, 3000), token);

                    var job = CreateRandomJob(rng, producerId);
                    var handle = system.Submit(job);
                    string shortId = job.Id.ToString()[..8];

                    if (handle.Result == null)
                    {
                        Interlocked.Increment(ref _rejectedJobs);
                        lock (_consoleLock)
                            Console.WriteLine($"[P{producerId}] rejected {shortId} (queue full or duplicate)");
                        continue;
                    }

                    Interlocked.Increment(ref _submittedJobs);
                    lock (_consoleLock)
                        Console.WriteLine($"[P{producerId}] submitted {shortId} | {job.Type} | priority {job.Priority} | {TruncatePayload(job.Payload, 30)}");

                    _ = handle.Result.ContinueWith(t =>
                    {
                        lock (_consoleLock)
                        {
                            if (t.IsCompletedSuccessfully)
                                Console.WriteLine($"[P{producerId}] done {shortId} = {t.Result}");
                            else
                                Console.WriteLine($"[P{producerId}] failed {shortId}: {t.Exception?.InnerException?.Message}");
                        }
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    lock (_consoleLock)
                        Console.WriteLine($"[P{producerId}] error: {ex.Message}");
                }
            }

            lock (_consoleLock)
                Console.WriteLine($"[P{producerId}] stopped.");
        }

        private static Job CreateRandomJob(Random rng, int producerId)
        {
            var type = rng.Next(100) < 60 ? JobType.Prime : JobType.IO;

            string payload = type == JobType.Prime
                ? $"threads:{rng.Next(1, 9)},numbers:{rng.Next(10000, 500001)}"
                : $"delay:{rng.Next(100, 10000)}";

            return new Job
            {
                Id = Guid.NewGuid(),
                Type = type,
                Payload = payload,
                Priority = rng.Next(1, 11)
            };
        }

        private static string TruncatePayload(string payload, int maxLength)
        {
            return payload.Length <= maxLength ? payload : payload.Substring(0, maxLength - 3) + "...";
        }

        private static void ShowStatus(ProcessingSystem.Services.ProcessingSystem system)
        {
            lock (_consoleLock)
            {
                int total = _submittedJobs + _rejectedJobs;
                string rate = total > 0 ? $"{100.0 * _submittedJobs / total:F1}%" : "N/A";

                Console.WriteLine($"\nsubmitted: {_submittedJobs} | rejected: {_rejectedJobs} | acceptance: {rate}");

                var jobs = system.GetTopJobs(5).ToList();
                Console.WriteLine(jobs.Count == 0
                    ? "queue is empty"
                    : $"top {jobs.Count} in queue:");

                foreach (var job in jobs)
                    Console.WriteLine($"  {job.Id.ToString()[..8]} | {job.Type} | priority {job.Priority} | {TruncatePayload(job.Payload, 25)}");

                Console.WriteLine();
            }
        }

        private static void ShowTopJobs(ProcessingSystem.Services.ProcessingSystem system)
        {
            Console.Write("\nhow many jobs to show? ");
            string input = Console.ReadLine();  // read first, outside the lock

            if (!int.TryParse(input, out int n) || n <= 0)
            {
                Console.WriteLine("invalid number.");
                return;
            }

            lock (_consoleLock)  // only lock when actually writing results
            {
                var jobs = system.GetTopJobs(n).ToList();
                Console.WriteLine(jobs.Count == 0 ? "\nqueue is empty." : $"\ntop {jobs.Count} jobs:");

                for (int i = 0; i < jobs.Count; i++)
                    Console.WriteLine($"  {i + 1}. [{jobs[i].Priority,2}] {jobs[i].Id} | {jobs[i].Type} | {jobs[i].Payload}");

                Console.WriteLine();
            }
        }

        private static async Task ShowJobDetails(ProcessingSystem.Services.ProcessingSystem system)
        {
            Console.Write("\njob id: ");
            string input = Console.ReadLine();

            lock (_consoleLock)
            {
                if (!Guid.TryParse(input, out Guid id))
                {
                    Console.WriteLine($"'{input}' is not a valid GUID.");
                    return;
                }

                var job = system.GetJob(id);
                if (job == null)
                {
                    Console.WriteLine($"no job found with id '{input}'.");
                    return;
                }

                Console.WriteLine($"\n{job.Id} | {job.Type} | priority {job.Priority} | {job.Payload}\n");
            }
        }
    }
}