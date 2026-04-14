using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace ProcessingSystem.Models
{
    public class Job
    {
        [XmlIgnore]
        public Guid Id { get; set; }

        [XmlAttribute("Type")]
        public JobType Type { get; set; }

        [XmlAttribute("Payload")]
        public string Payload { get; set; } = string.Empty;

        [XmlAttribute("Priority")]
        public int Priority { get; set; }
    }
}
