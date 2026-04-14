using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace ProcessingSystem.Models
{
    public class SystemConfig
    {
        public int WorkerCount { get; set; }
        public int MaxQueueSize { get; set; }

        [XmlArray("Jobs")]
        [XmlArrayItem("Job")]
        public List<Job> Jobs { get; set; } = new List<Job>();
    }
}
