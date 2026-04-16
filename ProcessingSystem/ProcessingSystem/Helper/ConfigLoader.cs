using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using ProcessingSystem.Models;

namespace ProcessingSystem.Helper
{
    public class ConfigLoader
    {
        public static SystemConfig LoadConfig(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Config path must not be empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("Config file not found.", path);

            var serializer = new XmlSerializer(typeof(SystemConfig));
            using var reader = new StreamReader(path);

            var config = serializer.Deserialize(reader) as SystemConfig ?? 
                throw new InvalidOperationException("Failed to deserialize config file.");

            return config;
        }
    }
}
