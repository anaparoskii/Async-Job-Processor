using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessingSystem.Models
{
    public class JobRecord
    {
        public Guid JobId { get; set; }
        public JobType Type { get; set; }
        public double ExecutionTimeSeconds { get; set; }
    }
}
