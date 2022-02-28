﻿using System;
using System.Linq;
using FastTests;
using FastTests.Server.JavaScript;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Tests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_10505 : RavenTestBase
    {
        public RavenDB_10505(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void ChangingCollectionNameByPutAndDeleteShouldNotDeleteTheOriginalTheTombstone(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Arek"
                    }, "users/1");

                    session.SaveChanges();

                    session.Query<User>().Customize(x => x.WaitForNonStaleResults())
                        .Where(x => x.Name == "Arek").ToList();
                }

                store.Operations.Send(new PatchByQueryOperation(@"
from Users as u
update
{
    del(id(u));
    this[""@metadata""][""@collection""] = ""People"";
    put(id(u), this);
}
")).WaitForCompletion(TimeSpan.FromSeconds(30));

                WaitForIndexing(store);

                var entriesCount = WaitForValue(() => store.Maintenance.Send(new GetIndexStatisticsOperation("Auto/Users/ByName")).EntriesCount, 0);

                Assert.Equal(0, entriesCount);
            }
        }
    }
}
