﻿using FastTests;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Xunit;
using Raven.Client;
using System.Threading.Tasks;
using FastTests.Server.JavaScript;
using Tests.Infrastructure.Entities;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_11032:RavenTestBase
    {
        public RavenDB_11032(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public async Task PatchByIndexShouldSupportDeclaredFunctions(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Order
                    {
                        Company = "companies/1"
                    }, "orders/1");
                    await session.SaveChangesAsync();
                }

                var operation = await store
    .Operations
    .SendAsync(new PatchByQueryOperation(new IndexQuery
    {
        QueryParameters = new Parameters
        {
            {"newCompany", "companies/2" }
        },
        Query = @"
declare function UpdateCompany(o, newVal)
{
o.Company = newVal;
}
from Orders as o                  
update
{
    UpdateCompany(o,$newCompany);
    
}"
    }));
                await operation.WaitForCompletionAsync();

                using (var session = store.OpenAsyncSession())
                {
                    var doc = await session.LoadAsync<Order>("orders/1");
                    Assert.Equal("companies/2", doc.Company);
                }

                
            }
        }
    }
}
