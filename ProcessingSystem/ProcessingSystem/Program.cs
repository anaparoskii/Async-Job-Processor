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
            Console.WriteLine("Industrial Processing System - Starting...");
            Console.WriteLine("========================================");

            try
            {
                string configPath = "config.xml";
                SystemConfig config;

                try
                {
                    config = ConfigLoader.LoadConfig(configPath);
                    Console.WriteLine($"Configuration loaded from: {configPath}");
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine($"Config file {configPath} not found. Creating default config...");
                    return;
                }

                Console.WriteLine($"Worker Count: {config.WorkerCount}");
                Console.WriteLine($"Max Queue Size: {config.MaxQueueSize}");
                Console.WriteLine($"Initial Jobs: {config.Jobs?.Count ?? 0}");
                Console.WriteLine("========================================");

                var processingSystem = new ProcessingSystem.Services.ProcessingSystem(config);

                int producerThreads = config.WorkerCount;
                var producerTasks = new List<Task>();
                var cts = new CancellationTokenSource();

                Console.WriteLine($"\nStarting {producerThreads} producer threads...");
                Console.WriteLine("Press 'q' to quit, 's' for status, 't' for top jobs\n");

                for (int i = 0; i < producerThreads; i++)
                {
                    int producerId = i;
                    producerTasks.Add(Task.Run(async () =>
                    {
                        await ProduceJobsAsync(processingSystem, producerId, cts.Token);
                    }));
                }

                bool running = true;
                while (running)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true).Key;
                        switch (key)
                        {
                            case ConsoleKey.Q:
                                running = false;
                                break;
                            case ConsoleKey.S:
                                ShowStatus(processingSystem);
                                break;
                            case ConsoleKey.T:
                                ShowTopJobs(processingSystem);
                                break;
                            case ConsoleKey.G:
                                await ShowJobDetails(processingSystem);
                                break;
                        }
                    }
                    await Task.Delay(100);
                }

                Console.WriteLine("\n\nInitiating graceful shutdown...");
                Console.WriteLine("Stopping producers...");
                cts.Cancel();

                try
                {
                    await Task.WhenAll(producerTasks);
                }
                catch (OperationCanceledException)
                {
                    // Očekivani izuzetak
                }

                Console.WriteLine("Waiting for remaining jobs to complete...");
                await Task.Delay(3000);

                Console.WriteLine("Shutting down processing system...");
                await processingSystem.ShutdownAsync();

                Console.WriteLine("\n=== Final Statistics ===");
                Console.WriteLine($"Total jobs submitted: {_submittedJobs}");
                Console.WriteLine($"Total jobs rejected: {_rejectedJobs}");
                Console.WriteLine("========================");
                Console.WriteLine("\nSystem shutdown complete. Press any key to exit.");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            }
        }

        private static async Task ProduceJobsAsync(ProcessingSystem.Services.ProcessingSystem system, int producerId, CancellationToken token)
        {
            var localRandom = new Random(producerId * 10000 + Environment.TickCount);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Nasumično čekanje između 500ms i 3 sekunde
                    int delay = localRandom.Next(500, 3000);
                    await Task.Delay(delay, token);

                    // Kreiranje nasumičnog posla
                    var job = CreateRandomJob(localRandom, producerId);

                    // Prikaz pre submit-a
                    lock (_consoleLock)
                    {
                        Console.WriteLine($"[P{producerId,2}] Submitting Job: {job.Id.ToString().Substring(0, 8)}... | Type: {job.Type,-5} | Priority: {job.Priority,2} | Payload: {TruncatePayload(job.Payload, 30)}");
                    }

                    // Submit posla
                    var handle = system.Submit(job);

                    if (handle.Result == null)
                    {
                        Interlocked.Increment(ref _rejectedJobs);
                        lock (_consoleLock)
                        {
                            Console.WriteLine($"[P{producerId,2}] ✗ REJECTED: {job.Id.ToString().Substring(0, 8)}... (Queue full or duplicate)");
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref _submittedJobs);
                        lock (_consoleLock)
                        {
                            Console.WriteLine($"[P{producerId,2}] ✓ ACCEPTED: {job.Id.ToString().Substring(0, 8)}...");
                        }

                        // Opciono: pratiti rezultat asinhrono
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                int result = await handle.Result;
                                lock (_consoleLock)
                                {
                                    Console.WriteLine($"[P{producerId,2}] ✔ COMPLETED: {job.Id.ToString().Substring(0, 8)}... = {result}");
                                }
                            }
                            catch (Exception ex)
                            {
                                lock (_consoleLock)
                                {
                                    Console.WriteLine($"[P{producerId,2}] ✘ FAILED: {job.Id.ToString().Substring(0, 8)}... ({ex.Message})");
                                }
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lock (_consoleLock)
                    {
                        Console.WriteLine($"[P{producerId,2}] ERROR: {ex.Message}");
                    }
                }
            }

            lock (_consoleLock)
            {
                Console.WriteLine($"[P{producerId,2}] Producer stopped.");
            }
        }

        private static Job CreateRandomJob(Random random, int producerId)
        {
            var jobId = Guid.NewGuid();

            var jobType = random.Next(100) < 60 ? JobType.Prime : JobType.IO;

            int priority = random.Next(1, 11);

            string payload;
            if (jobType == JobType.Prime)
            {
                int threads = random.Next(1, 9);
                int numbers = random.Next(10000, 500001);
                payload = $"threads:{threads},numbers:{numbers}";
            }
            else
            {
                int delay = random.Next(100, 2000);
                payload = $"delay:{delay}";
            }

            return new Job
            {
                Id = jobId,
                Type = jobType,
                Payload = payload,
                Priority = priority
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
                Console.WriteLine("\nSystem Status: ");
                Console.WriteLine($"Total Submitted: {_submittedJobs}");
                Console.WriteLine($"Total Rejected:  {_rejectedJobs}");
                Console.WriteLine($"Acceptance Rate: {(_submittedJobs + _rejectedJobs > 0 ? (100.0 * _submittedJobs / (_submittedJobs + _rejectedJobs)).ToString("F1") : "N/A")}%");

                var topJobs = system.GetTopJobs(5);
                Console.WriteLine($"\nTop {topJobs.Count()} jobs in queue (by priority):");
                if (topJobs.Any())
                {
                    foreach (var job in topJobs)
                    {
                        Console.WriteLine($"  [{job.Priority,2}] {job.Id.ToString().Substring(0, 8)}... | {job.Type,-5} | {TruncatePayload(job.Payload, 25)}");
                    }
                }
                else
                {
                    Console.WriteLine("  Queue is empty");
                }
                Console.WriteLine();
            }
        }

        private static void ShowTopJobs(ProcessingSystem.Services.ProcessingSystem system)
        {
            Console.Write("\nEnter number of top jobs to display: ");
            if (!int.TryParse(Console.ReadLine(), out int n) || n <= 0)
            {
                Console.WriteLine("Invalid number.\n");
                return;
            }

            lock (_consoleLock)
            {
                var topJobs = system.GetTopJobs(n);
                Console.WriteLine($"\nTop {n} jobs in queue (ordered by priority - lower number = higher priority):");
                Console.WriteLine(new string('-', 80));

                if (topJobs.Any())
                {
                    int rank = 1;
                    foreach (var job in topJobs)
                    {
                        Console.WriteLine($"{rank,2}. Priority: {job.Priority,2} | ID: {job.Id} | Type: {job.Type,-5} | Payload: {job.Payload}");
                        rank++;
                    }
                }
                else
                {
                    Console.WriteLine("No jobs in queue.");
                }
                Console.WriteLine();
            }
        }

        private static async Task ShowJobDetails(ProcessingSystem.Services.ProcessingSystem system)
        {
            Console.Write("\nEnter Job ID (GUID): ");
            string input = Console.ReadLine();
            lock (_consoleLock)
            {
                if (Guid.TryParse(input, out Guid id))
                {
                    var job = system.GetJob(id);
                    if (job != null)
                    {
                        Console.WriteLine("\nJob Details: ");
                        Console.WriteLine($"ID:       {job.Id}");
                        Console.WriteLine($"Type:     {job.Type}");
                        Console.WriteLine($"Priority: {job.Priority}");
                        Console.WriteLine($"Payload:  {job.Payload}");
                    }
                    else
                    {
                        Console.WriteLine($"\nJob with ID '{input}' not found.");
                    }
                }
                else
                {
                    Console.WriteLine($"\nInvalid GUID format: '{input}'");
                }
                Console.WriteLine();
            }
        }
    }
}