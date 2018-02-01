﻿using Newtonsoft.Json.Linq;
using RepositoryAnaltyicsApi.Interfaces;
using RepositoryAnalyticsApi.ServiceModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RepositoryAnaltyicsApi.Managers.Dependencies
{
    public class BowerDependencyManager : IDependencyManager
    {
        private IRepositorySourceManager repositorySourceManager;

        public BowerDependencyManager(IRepositorySourceManager repositorySourceManager)
        {
            this.repositorySourceManager = repositorySourceManager;
        }

        public List<RepositoryDependency> Read(string repositoryId)
        {
            var dependencies = new List<RepositoryDependency>();

            var files = repositorySourceManager.ReadFiles(repositoryId);

            var bowerJsonFile = files.FirstOrDefault(file => file.Name == "bower.json");

            if (bowerJsonFile != null)
            {
                var bowerJsonContent = repositorySourceManager.ReadFileContent(repositoryId, bowerJsonFile.FullPath); 

                var jObject = JObject.Parse(bowerJsonContent);

                var bowerProdDependencies = jObject["dependencies"];

                if (bowerProdDependencies != null)
                {
                    foreach (var token in bowerProdDependencies)
                    {
                        var property = token as JProperty;

                        var dependency = new RepositoryDependency();
                        dependency.Environment = "Production";
                        dependency.Source = "bower";
                        dependency.Name = property.Name;
                        var cleansedVersionMatch = Regex.Match(property.Value.ToString(), @"[\d\.]+");
                        dependency.Version = cleansedVersionMatch.Value;
                        dependency.MajorVersion = Regex.Match(dependency.Version, @"\d+").Value;

                        dependencies.Add(dependency);
                    }
                }

                var bowerDevDependencies = jObject["devDependencies"];

                if (bowerDevDependencies != null)
                {
                    foreach (var token in bowerDevDependencies)
                    {
                        var property = token as JProperty;

                        var dependency = new RepositoryDependency();
                        dependency.Environment = "Development";
                        dependency.Source = "bower";
                        dependency.Name = property.Name;
                        var cleansedVersionMatch = Regex.Match(property.Value.ToString(), @"[\d\.]+");
                        dependency.Version = cleansedVersionMatch.Value;
                        dependency.MajorVersion = Regex.Match(dependency.Version, @"\d+").Value;

                        dependencies.Add(dependency);
                    }
                }

            }

            return dependencies;
        }
    }
}
