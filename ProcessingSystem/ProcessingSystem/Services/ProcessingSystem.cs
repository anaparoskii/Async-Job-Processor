using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
                        if (job != null) ProcessJob(job); 
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
            // switch JobHandle initialization
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (_jobQueue)
            {
                if (_processedJobs.Contains(job.Id))
                    return new JobHandle();

                if (_jobQueue.Count >= _config.MaxQueueSize)
                    return null;

                _processedJobs.Add(job.Id);
                _jobQueue.Enqueue(job, job.Priority);
            }
            _signal.Release();
            return new JobHandle();
        }

        private void ProcessJob(Job job)
        {
            // add logic for processing each job type
        }
    }
}
