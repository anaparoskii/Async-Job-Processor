using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProcessingSystem.Models;

namespace ProcessingSystem.Services
{
    public class ProcessingSystem
    {
        private readonly SystemConfig _config;
        private List<Task> _workers = new List<Task>();
        private PriorityQueue<Job, int> _jobQueue = new PriorityQueue<Job, int>();
        private SemaphoreSlim _signal = new SemaphoreSlim(0);
        private bool _isComplete = false;
        private HashSet<Guid> _processedJobs = new HashSet<Guid>();
        private Dictionary<Guid, TaskCompletionSource<int>> _jobRegistry = new Dictionary<Guid, TaskCompletionSource<int>>();
        private readonly object _registryLock = new object();
        private static readonly Random _random = new Random();

        public event Func<Guid, int, Task>? JobCompleted;
        public event Func<Guid, Task>? JobFailed;

        private static readonly SemaphoreSlim _logLock = new SemaphoreSlim(1, 1);

        private Dictionary<Guid, Job> _allJobs = new Dictionary<Guid, Job>();
        private List<JobRecord> _completedRecords = new List<JobRecord>();
        private List<JobRecord> _failedRecords = new List<JobRecord>();
        private readonly object _recordsLock = new object();
        private int _reportIndex = 0;
        private Timer _reportTimer;

        public ProcessingSystem(SystemConfig config)
        {
            _config = config;

            JobCompleted += async (id, result) =>
            {
                await LogAsync($"[{DateTime.Now}] [COMPLETED] {id}, {result}");
            };

            JobFailed += async (id) =>
            {
                await LogAsync($"[{DateTime.Now}] [FAILED] {id}");
            };

            for (int i = 0; i < _config.WorkerCount; i++)
            {
                _workers.Add(Task.Run(async () =>
                {
                    while (true)
                    {
                        await _signal.WaitAsync();
                        Job? job = null;
                        lock (_jobQueue)
                        {
                            if (_isComplete && _jobQueue.Count == 0) break;
                            if (!_jobQueue.TryDequeue(out job, out _)) continue;
                        }
                        if (job != null)
                        {
                            try
                            {
                                int result = await ProcessJobWithRetry(job);
                                if (result != -1)
                                {
                                    lock (_registryLock)
                                    {
                                        if (_jobRegistry.TryGetValue(job.Id, out var tcs))
                                        {
                                            tcs.SetResult(result);
                                            _jobRegistry.Remove(job.Id);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) 
                            {
                                await LogAsync($"[{DateTime.Now}] [ERROR] Job {job.Id} crashed: {ex.Message}");

                                lock (_registryLock)
                                {
                                    if (_jobRegistry.TryGetValue(job.Id, out var tcs))
                                    {
                                        tcs.SetException(ex);
                                        _jobRegistry.Remove(job.Id);
                                    }
                                }
                            }
                        }
                    }
                }));
            }

            if (_config.Jobs != null)
            {
                foreach (var job in _config.Jobs) Submit(job);
            }
            _reportTimer = new Timer(async _ => await GenerateReport(), null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));
        }

        public JobHandle Submit(Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            if (job.Id == Guid.Empty)
            {
                job.Id = Guid.NewGuid();
            }

            var tcs = new TaskCompletionSource<int>();
            lock (_jobQueue)
            {
                if (_isComplete)
                    return new JobHandle { Id = job.Id, Result = null };
                if (_processedJobs.Contains(job.Id))
                    return new JobHandle { Id = job.Id, Result = null };
                if (_jobQueue.Count >= _config.MaxQueueSize)
                    return new JobHandle { Id = job.Id, Result = null };

                _processedJobs.Add(job.Id);
                _jobQueue.Enqueue(job, job.Priority);
                _allJobs[job.Id] = job;
            }
            lock (_registryLock)
            {
                _jobRegistry[job.Id] = tcs;
            }

            _signal.Release();
            return new JobHandle { Id = job.Id, Result = tcs.Task };
        }

        public async Task ShutdownAsync()
        {
            _reportTimer.Dispose();
            lock (_jobQueue)
            {
                _isComplete = true;
            }
            _signal.Release(_config.WorkerCount);
            await Task.WhenAll(_workers);
        }

        private int ProcessJob(Job job)
        {
            var payload = ParsePayload(job.Payload);
            if (job.Type == JobType.Prime) 
                return ProcessPrimeJob(payload);
            if (job.Type == JobType.IO) 
                return ProcessIOJob(payload);
            throw new ArgumentException("Invalid job type");
        }

        private async Task<int> ProcessJobWithRetry(Job job)
        {
            int attempts = 0;
            const int MAX_ATTEMPTS = 3;
            while (attempts < MAX_ATTEMPTS)
            {
                attempts++;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var jobTask = Task.Run(() => ProcessJob(job), cts.Token);
                    int result = await jobTask.WaitAsync(TimeSpan.FromSeconds(2));
                    stopwatch.Stop();
                    lock (_recordsLock)
                    {
                        _completedRecords.Add(new JobRecord
                        {
                            JobId = job.Id,
                            Type = job.Type,
                            ExecutionTimeSeconds = stopwatch.Elapsed.TotalSeconds
                        });
                    }
                    if (JobCompleted != null)
                        await JobCompleted(job.Id, result);
                    return result;
                } 
                catch (TimeoutException) 
                {
                    stopwatch.Stop();
                    lock (_recordsLock)
                    {
                        _failedRecords.Add(new JobRecord
                        {
                            JobId = job.Id,
                            Type = job.Type,
                            ExecutionTimeSeconds = stopwatch.Elapsed.TotalSeconds
                        });
                    }
                    if (JobFailed != null)
                        await JobFailed(job.Id);

                    if (attempts >= MAX_ATTEMPTS)
                    {
                        await LogAsync($"[{DateTime.Now}] [ABORT] {job.Id}");
                        lock (_registryLock)
                        {
                            if (_jobRegistry.TryGetValue(job.Id, out var tcs))
                            {
                                tcs.SetCanceled();
                                _jobRegistry.Remove(job.Id);
                            }
                        }
                        return -1;
                    }
                }
            }
            return -1;
        }

        private int ProcessPrimeJob(Dictionary<string, string> payload)
        {
            int threads = int.Parse(payload["threads"]);
            int numbers = int.Parse(payload["numbers"].Replace("_", ""));

            threads = Math.Clamp(threads, 1, 8);

            int count = 0;

            Parallel.For(2, numbers + 1,
                new ParallelOptions { MaxDegreeOfParallelism = threads },
                () => 0,
                (i, state, localCount) =>
                {
                    if (IsPrime(i)) localCount++;
                    return localCount;
                },
                localCount => Interlocked.Add(ref count, localCount)
            );

            return count;
        }

        private int ProcessIOJob(Dictionary<string, string> payload)
        {
            int delay = int.Parse(payload["delay"].Replace("_", ""));
            Thread.Sleep(delay);
            return _random.Next(0, 101);
        }

        private Dictionary<string, string> ParsePayload(string payload)
        {
            return payload.Split(',')
                .Select(p => p.Split(':'))
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        }

        private bool IsPrime(int n)
        {
            if (n < 2) return false;
            int limit = (int)Math.Sqrt(n); 
            for (int i = 2; i <= limit; i++)
            {
                if (n % i == 0) return false;
            }
            return true;
        }

        private async Task LogAsync(string message)
        {
            await _logLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync("log.txt", message + Environment.NewLine);
            }
            finally
            {
                _logLock.Release();
            }
        }

        public IEnumerable<Job> GetTopJobs(int n)
        {
            lock (_jobQueue)
            {
                return _jobQueue.UnorderedItems
                    .OrderBy(x => x.Priority)
                    .Take(n)
                    .Select(x => x.Element)
                    .ToList();
            }
        }

        public Job? GetJob(Guid id)
        {
            lock (_jobQueue)
            {
                _allJobs.TryGetValue(id, out var job);
                return job;
            }
        }

        private async Task GenerateReport()
        {
            List<JobRecord> completed;
            List<JobRecord> failed;

            lock (_recordsLock)
            {
                completed = _completedRecords.ToList();
                failed = _failedRecords.ToList();

                _completedRecords.Clear();
                _failedRecords.Clear();
            }

            var completedByType = completed
                .GroupBy(r => r.Type)
                .Select(g => new
                {
                    Type = g.Key,
                    Count = g.Count()
                });

            var avgTimeByType = completed
                .GroupBy(r => r.Type)
                .Select(g => new
                {
                    Type = g.Key,
                    AvgSeconds = g.Average(r => r.ExecutionTimeSeconds)
                });

            var failedByType = failed
                .GroupBy(r => r.Type)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    Type = g.Key,
                    Count = g.Count()
                });

            var doc = new System.Xml.Linq.XDocument(
                new System.Xml.Linq.XElement("Report",
                    new System.Xml.Linq.XAttribute("GeneratedAt", DateTime.Now),

                    new System.Xml.Linq.XElement("CompletedByType",
                        completedByType.Select(x =>
                            new System.Xml.Linq.XElement("Entry",
                                new System.Xml.Linq.XAttribute("Type", x.Type),
                                new System.Xml.Linq.XAttribute("Count", x.Count)))),

                    new System.Xml.Linq.XElement("AverageTimeByType",
                        avgTimeByType.Select(x =>
                            new System.Xml.Linq.XElement("Entry",
                                new System.Xml.Linq.XAttribute("Type", x.Type),
                                new System.Xml.Linq.XAttribute("AvgSeconds", Math.Round(x.AvgSeconds, 3))))),

                    new System.Xml.Linq.XElement("FailedByType",
                        failedByType.Select(x =>
                            new System.Xml.Linq.XElement("Entry",
                                new System.Xml.Linq.XAttribute("Type", x.Type),
                                new System.Xml.Linq.XAttribute("Count", x.Count))))
                )
            );

            string filename = $"report_{_reportIndex % 10}.xml";
            _reportIndex++;

            await Task.Run(() => doc.Save(filename));
        }
    }
}
