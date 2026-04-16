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

        public ProcessingSystem(SystemConfig config)
        {
            _config = config;

            for (int i = 0; i < _config.WorkerCount; i++)
            {
                _workers.Add(Task.Run(() =>
                {
                    while (true)
                    {
                        _signal.Wait();
                        Job? job = null;
                        lock (_jobQueue)
                        {
                            if (_isComplete && _jobQueue.Count == 0) break;
                            if (!_jobQueue.TryDequeue(out job, out _)) continue;
                        }
                        if (job != null)
                        {
                            int result = ProcessJob(job);
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
                }));
            }

            if (_config.Jobs != null)
            {
                foreach (var job in _config.Jobs) Submit(job);
            }
        }

        public JobHandle Submit(Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
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
            lock (_jobQueue)
            {
                _isComplete = true;
            }
            _signal.Release(_config.WorkerCount);
            await Task.WhenAll(_workers);
        }

        private int ProcessJob(Job job)
        {
            // add logic for processing each job type
            var payload = ParsePayload(job.Payload);
            if (job.Type == JobType.Prime) 
                return ProcessPrimeJob(payload);
            if (job.Type == JobType.IO) 
                return ProcessIOJob(payload);
            throw new ArgumentException("Invalid job type");
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
            for (int i = 2; i <= Math.Sqrt(n); i++)
                if (n % i == 0) return false;
            return true;
        }
    }
}
