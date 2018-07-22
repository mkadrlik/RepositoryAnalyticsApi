﻿using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using RepositoryAnaltyicsApi.Interfaces;
using RepositoryAnalyticsApi.ServiceModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RepositoryAnalyticsApi.Repositories
{
    public class MongoDependencyRepository : IDependencyRepository
    {
        private IMongoCollection<BsonDocument> mongoCollection;

        public MongoDependencyRepository(IMongoCollection<BsonDocument> mongoCollection)
        {
            this.mongoCollection = mongoCollection;
        }

        public async Task<List<RepositoryDependencySearchResult>> SearchAsync(string name, DateTime? asOf)
        {
            var searchResults = new List<RepositoryDependencySearchResult>();

            var options = new AggregateOptions()
            {
                AllowDiskUse = false
            };

            var matchStatements = new BsonArray();
            matchStatements.Add(new BsonDocument().Add("Dependencies.Name", name));

            if (asOf.HasValue)
            {
                matchStatements.Add(new BsonDocument().Add("WindowStartsOn", new BsonDocument().Add("$lte", new BsonDateTime(asOf.Value))));
                matchStatements.Add(new BsonDocument().Add("WindowEndsOn", new BsonDocument().Add("$gte", new BsonDateTime(asOf.Value))));
            }

            PipelineDefinition<BsonDocument, BsonDocument> pipeline = new BsonDocument[]
            {
                new BsonDocument("$match", new BsonDocument()
                        .Add("$and", matchStatements)),
                new BsonDocument("$unwind", new BsonDocument()
                        .Add("path", "$Dependencies")),
                new BsonDocument("$match", new BsonDocument()
                        .Add("$and", matchStatements)),
                new BsonDocument("$group", new BsonDocument()
                        .Add("_id", new BsonDocument()
                                .Add("Name", "$Dependencies.Name")
                                .Add("Version", "$Dependencies.Version")
                        )
                        .Add("count", new BsonDocument()
                                .Add("$sum", 1.0)
                        )),
                new BsonDocument("$sort", new BsonDocument()
                        .Add("_id.Name", 1.0))
            };

            using (var cursor = await mongoCollection.AggregateAsync(pipeline, options))
            {
                while (await cursor.MoveNextAsync())
                {
                    var batch = cursor.Current;
                    foreach (BsonDocument document in batch)
                    {
                        var jObject = JObject.Parse(document.ToJson());

                        var searchResult = new RepositoryDependencySearchResult
                        {
                            Count = jObject["count"].Value<int>(),
                            RepositoryDependency = new RepositoryDependency
                            {
                                Name = jObject["_id"]["Name"].Value<string>(),
                                Version = jObject["_id"]["Version"].Value<string>()
                            }
                        };

                        searchResults.Add(searchResult);
                    }

                    return searchResults;
                }
            }

            return searchResults;
        }


        public async Task<List<string>> SearchNamesAsync(string name, DateTime? asOf)
        {
            var dependencyNames = new List<string>();

            var options = new AggregateOptions()
            {
                AllowDiskUse = false
            };

            var matchStatements = new BsonArray();
            matchStatements.Add(new BsonDocument().Add("Dependencies.Name", new BsonRegularExpression(name, "i")));

            if (asOf.HasValue)
            {
                matchStatements.Add(new BsonDocument().Add("WindowStartsOn", new BsonDocument().Add("$lte", new BsonDateTime(asOf.Value))));
                matchStatements.Add(new BsonDocument().Add("WindowEndsOn", new BsonDocument().Add("$gte", new BsonDateTime(asOf.Value))));
            }

            PipelineDefinition<BsonDocument, BsonDocument> pipeline = new BsonDocument[]
            {
                new BsonDocument("$match", new BsonDocument()
                        .Add("$and", matchStatements)),
                new BsonDocument("$unwind", new BsonDocument()
                        .Add("path", "$Dependencies")),
                new BsonDocument("$match", new BsonDocument()
                        .Add("$and", matchStatements)),
                new BsonDocument("$group", new BsonDocument()
                        .Add("_id", new BsonDocument()
                                .Add("Name", "$Dependencies.Name")
                        )),
                new BsonDocument("$sort", new BsonDocument()
                        .Add("_id.Name", 1.0))
            };

            using (var cursor = await mongoCollection.AggregateAsync(pipeline, options))
            {
                while (await cursor.MoveNextAsync())
                {
                    var batch = cursor.Current;
                    foreach (BsonDocument document in batch)
                    {
                        var jObject = JObject.Parse(document.ToJson());

                        var dependencyName = jObject["_id"]["Name"].Value<string>();

                        dependencyNames.Add(dependencyName);
                    }
                }
            }

            return dependencyNames;
        }
    }
}