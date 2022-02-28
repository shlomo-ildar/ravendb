﻿using System;
using FastTests;
using FastTests.Server.JavaScript;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Queries;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_8161 : RavenTestBase
    {
        public RavenDB_8161(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void Can_delete_all_entries_from_compressed_tree_in_map_reduce_index(string jsEngineType)
        {
            var path = NewDataPath();
            var options = Options.ForJavaScriptEngine(jsEngineType);
            options.Path = path;
            using (var store = GetDocumentStore(options))
            {
                store.Maintenance.Send(new CreateSampleDataOperation());

                for (int i = 0; i < 3; i++)
                {
                    store.Operations.Send(new PatchByQueryOperation(new IndexQuery
                    {
                        Query = @"FROM Orders UPDATE { put(""orders/"", this); } "
                    })).WaitForCompletion(TimeSpan.FromSeconds(300));
                }

                WaitForIndexing(store);

                var operation = store.Operations.Send(new DeleteByQueryOperation(new IndexQuery() { Query = "FROM orders" }));
                operation.WaitForCompletion(TimeSpan.FromSeconds(60));

                WaitForIndexing(store);

                var indexStats = store.Maintenance.Send(new GetIndexesStatisticsOperation());

                foreach (var stats in indexStats)
                {
                    Assert.Equal(0, stats.ErrorsCount);
                }
            }
        }
    }
}
