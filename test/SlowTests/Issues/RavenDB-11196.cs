﻿using System;
using FastTests.Server.JavaScript;
using Raven.Client.Documents.Operations;
using Raven.Tests.Core.Utils.Entities;
using SlowTests.Server.Documents.ETL;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_11196 : EtlTestBase
    {
        public RavenDB_11196(ITestOutputHelper output) : base(output)
        {
        }
        
        [Theory]
        [JavaScriptEngineClassData]
        public void Should_be_James(string jsEngineType)
        {
            using (var src = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            using (var dest = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                AddEtl(src, dest, "Users", script:
                    @"
this.Name = 'James';

loadToUsers(this);

var person = { Name: this.Name };

loadToPeople(person);
"
                );

                var etlDone = WaitForEtl(src, (n, s) => s.LoadSuccesses > 0);

                using (var session = src.OpenSession())
                {
                    session.Store(new User()
                    {
                        Name = "Joe",
                        LastName = "Doe",
                    }, "users/1");

                    session.SaveChanges();
                }

                etlDone.Wait(TimeSpan.FromMinutes(100));

                using (var session = dest.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    Assert.NotNull(user);
                    Assert.Equal("James", user.Name);

                    var person = session.Advanced.LoadStartingWith<Person>("users/1/people/")[0];

                    Assert.NotNull(person);
                    Assert.Equal("James", person.Name); // throws here, actual: Joe
                }
            }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void CanDeleteEverything(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                store.Maintenance.Send(new CreateSampleDataOperation());

                WaitForIndexing(store);

                var operation = store.Operations.Send(new PatchByQueryOperation("from @all_docs as doc update {  del(id(doc)); }"));
                operation.WaitForCompletion();

                var stats = store.Maintenance.Send(new GetStatisticsOperation());
                Assert.Equal(8, stats.CountOfDocuments); // hi-lo
            }
        }
    }
}
