﻿using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using RepositoryAnaltyicsApi.Interfaces;
using RepositoryAnalyticsApi.Extensibility;
using RepositoryAnalyticsApi.InternalModel.AppSettings;
using RepositoryAnalyticsApi.ServiceModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace RepositoryAnaltyicsApi.Managers
{
    public class RepositoryAnalysisManager : IRepositoryAnalysisManager
    {
        private IRepositoryManager repositoryManager;
        private IRepositorySourceManager repositorySourceManager;
        private IEnumerable<IDependencyScraperManager> dependencyScraperManagers;
        private IEnumerable<IDeriveRepositoryTypeAndImplementations> typeAndImplementationDerivers;
        private IDeriveRepositoryDevOpsIntegrations devOpsIntegrationsDeriver;
        private IDistributedCache distributedCache;
        private ILogger<RepositoryAnalysisManager> logger;
        private Caching cachingSettings;

        public RepositoryAnalysisManager(
            IRepositoryManager repositoryManager, 
            IRepositorySourceManager repositorySourceManager, 
            IEnumerable<IDependencyScraperManager> dependencyScraperManagers, 
            IEnumerable<IDeriveRepositoryTypeAndImplementations> typeAndImplementationDerivers, 
            IDeriveRepositoryDevOpsIntegrations devOpsIntegrationsDeriver,
            IDistributedCache distributedCache,
            ILogger<RepositoryAnalysisManager> logger,
            Caching cachingSettings)
        {
            this.repositoryManager = repositoryManager;
            this.repositorySourceManager = repositorySourceManager;
            this.dependencyScraperManagers = dependencyScraperManagers;
            this.typeAndImplementationDerivers = typeAndImplementationDerivers;
            this.devOpsIntegrationsDeriver = devOpsIntegrationsDeriver;
            this.distributedCache = distributedCache;
            this.logger = logger;
            this.cachingSettings = cachingSettings;
        }

        public async Task CreateAsync(RepositoryAnalysis repositoryAnalysis)
        {
            var parsedRepoUrl = ParseRepositoryUrl();

            // maybe change the name of this var to reflect how it's different than the repositoryAnalysis.repsoitoryId?
            var repositoryId = $"{parsedRepoUrl.Host}|{parsedRepoUrl.Owner}|{parsedRepoUrl.Name}";

            var repository = await repositoryManager.ReadAsync(repositoryId, repositoryAnalysis.AsOf).ConfigureAwait(false);

            if (repositoryAnalysis.OnlyReprocessTypeAndImplementationData)
            {
                if (repository != null)
                {
                    repository.Snapshot.TypesAndImplementations = await ScrapeRepositoryTypeAndImplementation(
                                           parsedRepoUrl.Owner,
                                           parsedRepoUrl.Name,
                                           repository.Snapshot.BranchUsed,
                                           repository.Snapshot.Files,
                                           repository.Snapshot.Dependencies,
                                           repository.CurrentState.Topics?.Select(topic => topic.Name),
                                           repositoryAnalysis.AsOf).ConfigureAwait(false);

                    await repositoryManager.UpsertAsync(repository, repositoryAnalysis.AsOf).ConfigureAwait(false);
                }
                else
                {
                    throw new ArgumentException($"The repository {repository.CurrentState.Name} has not yet been fully processed so only reprocessing part of it is not possible");
                }
            }
            else
            {
                DateTime? repositoryLastUpdatedOn = null;

                // If a last updated time for the repo was provided, use that to hopefully save an API call
                if (repositoryAnalysis.RepositoryLastUpdatedOn.HasValue)
                {
                    repositoryLastUpdatedOn = repositoryAnalysis.RepositoryLastUpdatedOn.Value;
                }
                else
                {
                    var repositorySummary = await repositorySourceManager.ReadRepositorySummaryAsync(parsedRepoUrl.Owner, parsedRepoUrl.Name).ConfigureAwait(false);

                    repositoryLastUpdatedOn = repositorySummary.UpdatedAt;
                }

                if (repositoryAnalysis.ForceCompleteRefresh || repository == null || repositoryLastUpdatedOn > repository.CurrentState.RepositoryLastUpdatedOn)
                {

                    // Do repository summary call to get the commit Id of the latest commit and the date that commit was pushed for the snapshot
                    // populate the snapshot date with the corresponding manager calls (E.G. ScrapeDependenciesAsync) 
                    // Do full repository read to get all the current state stuff (including calls to get derived data like devops integrations)
                    var sourceRepository = await repositorySourceManager.ReadRepositoryAsync(parsedRepoUrl.Owner, parsedRepoUrl.Name).ConfigureAwait(false);

                    var repositoryCurrentState = new RepositoryCurrentState();
                    repositoryCurrentState.Id = repositoryId;
                    repositoryCurrentState.Name = sourceRepository.Name;
                    repositoryCurrentState.Owner = parsedRepoUrl.Owner;
                    repositoryCurrentState.DefaultBranch = sourceRepository.DefaultBranchName;
                    repositoryCurrentState.HasIssues = sourceRepository.IssueCount > 0;
                    repositoryCurrentState.HasProjects = sourceRepository.ProjectCount > 0;
                    repositoryCurrentState.HasPullRequests = sourceRepository.PullRequestCount > 0;
                    repositoryCurrentState.RepositoryCreatedOn = sourceRepository.CreatedAt;
                    repositoryCurrentState.RepositoryLastUpdatedOn = sourceRepository.PushedAt;

                    repositoryCurrentState.Teams = sourceRepository.Teams;
                    repositoryCurrentState.Topics = sourceRepository.TopicNames?.Select(name => new RepositoryTopic { Name = name }).ToList();
                    repositoryCurrentState.DevOpsIntegrations = await ScrapeDevOpsIntegrations(repositoryCurrentState.Name).ConfigureAwait(false);

                    // Need to pick a branch for the snapshot stuff
                    string branchName = null;

                    if (sourceRepository.BranchNames.Contains("master"))
                    {
                        branchName = "master";
                    }
                    else if (sourceRepository.BranchNames.Contains("development"))
                    {
                        branchName = "development";
                    }
                    else if (!string.IsNullOrWhiteSpace(sourceRepository.DefaultBranchName))
                    {
                        branchName = sourceRepository.DefaultBranchName;
                    }

                    RepositorySnapshot repositorySnapshot = null;

                    if (branchName != null)
                    {
                        repositorySnapshot = new RepositorySnapshot();
                        // Have to set the windows in the manager
                        repositorySnapshot.RepositoryCurrentStateRepositoryId = repositoryCurrentState.Id;
                        repositorySnapshot.TakenOn = DateTime.Now;
                        repositorySnapshot.BranchUsed = branchName;
                        repositorySnapshot.Dependencies = await ScrapeDependenciesAsync(parsedRepoUrl.Owner, parsedRepoUrl.Name, branchName, repositoryAnalysis.AsOf).ConfigureAwait(false);
                        repositorySnapshot.Files = await repositorySourceManager.ReadFilesAsync(parsedRepoUrl.Owner, parsedRepoUrl.Name, branchName, repositoryAnalysis.AsOf).ConfigureAwait(false);
                        repositorySnapshot.TypesAndImplementations = await ScrapeRepositoryTypeAndImplementation(
                            parsedRepoUrl.Owner,
                            parsedRepoUrl.Name,
                            branchName,
                            repositorySnapshot.Files,
                            repositorySnapshot.Dependencies,
                            repositoryCurrentState.Topics?.Select(topic => topic.Name),
                            repositoryAnalysis.AsOf).ConfigureAwait(false);
                    }

                    var updatedRepository = new Repository
                    {
                        CurrentState = repositoryCurrentState,
                        Snapshot = repositorySnapshot
                    };

                    await repositoryManager.UpsertAsync(updatedRepository, repositoryAnalysis.AsOf).ConfigureAwait(false);
                }
            }

            (string Owner, string Name, string Host) ParseRepositoryUrl()
            {
                var repositoryUri = new Uri(repositoryAnalysis.RepositoryId);
                var owner = repositoryUri.Segments[1].TrimEnd('/');
                var name = repositoryUri.Segments[2].TrimEnd('/');
                var host = repositoryUri.Host;

                return (owner, name, host);
            }
        }

        private async Task<RepositoryDevOpsIntegrations> ScrapeDevOpsIntegrations(string repositoryName)
        {
            if (devOpsIntegrationsDeriver != null)
            {
                var cacheKey = $"devOpsIntegration|{repositoryName}";

                var devOpsIntegrations = await distributedCache.GetAsync<RepositoryDevOpsIntegrations>(cacheKey).ConfigureAwait(false);

                if (devOpsIntegrations == null)
                {
                    logger.LogDebug($"retrieving {cacheKey} from source");

                    devOpsIntegrations = await devOpsIntegrationsDeriver.DeriveIntegrationsAsync(repositoryName).ConfigureAwait(false);

                    if (devOpsIntegrations != null)
                    {
                        var cacheOptions = new DistributedCacheEntryOptions
                        {
                            SlidingExpiration = TimeSpan.FromSeconds(cachingSettings.Durations.DevOpsIntegrations)
                        };

                        await distributedCache.SetAsync(cacheKey, devOpsIntegrations, cacheOptions).ConfigureAwait(false);
                    }
                }

                return devOpsIntegrations;
            }
            else
            {
                return null;
            }
        }

        private async Task<List<RepositoryDependency>> ScrapeDependenciesAsync(string owner, string name, string defaultBranch, DateTime? asOf = null)
        {
            var allDependencies = new List<RepositoryDependency>();

            var sourceFileRegexes = dependencyScraperManagers.Select(dependencyManager => dependencyManager.SourceFileRegex);
            var sourceFiles = await repositorySourceManager.ReadFilesAsync(owner, name, defaultBranch, asOf).ConfigureAwait(false);

            // Get the files that all the dependency scrapers need so we can read them all in one shot and have them
            // cached for each dependency scraper
            var sourceFilesToRead = new HashSet<string>();
            foreach (var sourceFile in sourceFiles)
            {
                foreach (var sourceFileRegex in sourceFileRegexes)
                {
                    if (sourceFileRegex.IsMatch(sourceFile.FullPath))
                    {
                        sourceFilesToRead.Add(sourceFile.FullPath);
                    }
                }
            }

            if (sourceFilesToRead.Any())
            {
                // Get all the file contents that will be needed read and in cache
                await repositorySourceManager.GetMultipleFileContentsAsync(owner, name, defaultBranch, sourceFilesToRead.ToList(), asOf).ConfigureAwait(false);

                foreach (var dependencyManager in dependencyScraperManagers)
                {
                    var dependencies = await dependencyManager.ReadAsync(owner, name, defaultBranch, asOf).ConfigureAwait(false);
                    allDependencies.AddRange(dependencies);
                }
            }

            return allDependencies;
        }

        private async Task<List<RepositoryTypeAndImplementations>> ScrapeRepositoryTypeAndImplementation(string owner, string name, string branch, IEnumerable<RepositoryFile> files, IEnumerable<RepositoryDependency> dependencies, IEnumerable<string> topicNames, DateTime? asOf)
        {
            var typesAndImplementations = new List<RepositoryTypeAndImplementations>();

            // TODO: This can be changed from a func to just a property now that the list has already been computed
            var readFilesAsync = new Func<Task<List<RepositoryFile>>>(async () =>
                await Task.FromResult(files.ToList())
            );

            foreach (var typeAndImplementationDeriver in typeAndImplementationDerivers)
            {
                if (typeAndImplementationDeriver is IRequireDependenciesAccess)
                {
                    (typeAndImplementationDeriver as IRequireDependenciesAccess).Dependencies = dependencies;
                }
                if (typeAndImplementationDeriver is IRequireTopicsAccess)
                {
                    (typeAndImplementationDeriver as IRequireTopicsAccess).TopicNames = topicNames;
                }
                if (typeAndImplementationDeriver is IRequireFileListAccess)
                {
                    (typeAndImplementationDeriver as IRequireFileListAccess).ReadFileListAsync = readFilesAsync;
                }

                var typeAndImplementationInfo = await typeAndImplementationDeriver.DeriveImplementationAsync(name);

                if (typeAndImplementationInfo != null)
                {
                    typesAndImplementations.Add(typeAndImplementationInfo);
                }
            }

            return typesAndImplementations;
        }
    }
}
