﻿using MongoDB.Driver;
using RepositoryAnaltyicsApi.Interfaces;
using RepositoryAnalyticsApi.ServiceModel;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RepositoryAnalyticsApi.Repositories
{
    public class MongoRepositoriesTypeNamesRepository : IRepositoriesTypeNamesRepository
    {
        private IMongoCollection<RepositorySnapshot> mongoCollection;

        public MongoRepositoriesTypeNamesRepository(IMongoCollection<RepositorySnapshot> mongoCollection)
        {
            this.mongoCollection = mongoCollection;
        }

        public async Task<List<string>> ReadAsync()
        {
            var typeNames = new List<string>();

            using (var cursor = await mongoCollection.DistinctAsync<string>("TypesAndImplementations.TypeName", FilterDefinition<RepositorySnapshot>.Empty))
            {
                while (await cursor.MoveNextAsync())
                {
                    var batch = cursor.Current;
                    foreach (var typeName in batch)
                    {
                        typeNames.Add(typeName);
                    }
                }
            }

            return typeNames;
        }
    }
}