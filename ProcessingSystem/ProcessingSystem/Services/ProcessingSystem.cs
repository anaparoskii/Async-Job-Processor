using System;
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

        public ProcessingSystem(SystemConfig config)
        {
            _config = config;
        }
    }
}
