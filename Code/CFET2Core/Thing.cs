using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Reflection;
using Jtext103.CFET2.Core.Attributes;

namespace Jtext103.CFET2.Core
{
    public abstract class Thing:CFET2Module
    {
        /// <summary>
        /// this get called just after add thing is called, that's the thing is instantiated and probed, now you don't have a proper hub 
        /// </summary>
        /// <param name="initObj"></param>
        public virtual void TryInit(object initObj)
        {
            //does nothing by defaule
        }


        /// <summary>
        /// this get called just after all thing is added, that's the thing is instantiated and probed, now you do have a proper hub 
        /// </summary>
        public virtual void Start()
        {
            //does nothing by defaule
        }

        /// <summary>
        /// the path to the thing this is valid only after the thing is added to the hub
        /// </summary>
        public string Path { get; internal set; }

        public string ConfigFilePath { get; internal set; }

        public string getConfigFilePath(string path)
        {
            string configPath = null;
            if (!File.Exists(path))
            {
                string tempConfigPath = ConfigFilePath + path.ToString().Split('/').Last();
                if (File.Exists(tempConfigPath))
                {
                    configPath = tempConfigPath;
                }
            }
            else
            {
                configPath = path;
            }
            return configPath;
        }

        /// <summary>
        /// return the path for a resouce in this thing
        /// </summary>
        /// <param name="resourceName"></param>
        /// <returns></returns>
        public string GetPathFor(string resourceName)
        {
            return Path + @"/" + getDeclaredResourceName(resourceName);
        }

        private string getDeclaredResourceName(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName))
            {
                return resourceName;
            }

            var member = GetType()
                .GetMember(resourceName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault();
            var attribute = member?.GetCustomAttribute<Cfet2AttributeBase>();
            if (string.IsNullOrEmpty(attribute?.Name))
            {
                return resourceName;
            }

            return attribute.Name;
        }
    }
}
