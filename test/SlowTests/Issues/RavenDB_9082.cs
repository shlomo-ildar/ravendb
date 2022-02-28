﻿using System.Linq;
using FastTests;
using FastTests.Server.JavaScript;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_9082 : RavenTestBase
    {
        public RavenDB_9082(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void CanUseProjectionWithArrowFunctionAndStatementBody(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                store.Maintenance.Send(new CreateSampleDataOperation());

                var queries = new[]
                {
                    @"from Orders as o
where id() == 'orders/830-A'
select {
    TopLines: o.Lines.sort((a,b)=> b.PricePerUnit - a.PricePerUnit)
        .map(a=>{ return { Name: a.ProductName } })
        .splice(0, 2)
}",
                    @"from Orders as o
where id() == 'orders/830-A'
select {
    Cost: o.Lines.reduce((a,l) => a + l.PricePerUnit, 0)
}"
                };

                foreach (var query in queries)
                {
                    using (var s = store.OpenSession())
                    {
                        Assert.NotEmpty(s.Advanced.RawQuery<object>(query).ToList());
                    }
                }
            }
        }
    }
}
