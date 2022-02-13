﻿using FastTests;
using FastTests.Server.JavaScript;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using SlowTests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_11893 : RavenTestBase
    {
        public RavenDB_11893(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void CreatingNewCountersViaPatchByQueryShouldNotUpdateMetadataMoreThanOncePerDoc(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    for (int i = 0; i < 10; i++)
                    {
                        session.Store(new User(), $"users/{i}");
                    }
                    session.SaveChanges();
                }

                store.Operations
                    .Send(new PatchByQueryOperation(new IndexQuery
                    {
                        Query = @"from Users as u
                                  update
                                  {
                                      incrementCounter(u, 'Downloads')
                                  }"
                    })).WaitForCompletion();


                var stats = store.Maintenance.Send(new GetStatisticsOperation());

                Assert.Equal(40, stats.LastDocEtag);
            }
        }
    }
}
