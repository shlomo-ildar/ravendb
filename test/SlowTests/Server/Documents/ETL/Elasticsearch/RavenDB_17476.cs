﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests.Server.JavaScript;
using Orders;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.ETL.ElasticSearch;
using Raven.Server.Documents.ETL.Providers.ElasticSearch;
using Raven.Server.Documents.ETL.Providers.ElasticSearch.Test;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.ETL.ElasticSearch
{

    public class RavenDB_17476 : ElasticSearchEtlTestBase
    {
        public RavenDB_17476(ITestOutputHelper output) : base(output)
        {
        }

        private string ScriptWithNoIdMethodUsage(string jsEngineType)
        {
            var optChaining = jsEngineType == "Jint" ? "" : "?";
            return @$"
var orderData = {{
OrderLinesCount: this.OrderLines{optChaining}.length,
TotalCost: 0
}};

for (var i = 0; i < this.Lines{optChaining}.length; i++) {{
var line = this.Lines[i];
var cost = (line.Quantity * line.PricePerUnit) *  ( 1 - line.Discount);
orderData.TotalCost += cost;
loadTo" + OrderLinesIndexName + @$"({{
    Qty: line.Quantity,
    Product: line.Product,
    Cost: cost
}});
}}

loadTo" + OrdersIndexName + @"(orderData);
";
        }

        [RequiresElasticSearchTheory]
        [JavaScriptEngineClassData]
        public void CanOmitDocumentIdPropertyInJsonPassedToLoadTo(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            using (GetElasticClient(out var client))
            {
                var config = SetupElasticEtl(store, ScriptWithNoIdMethodUsage(jsEngineType), DefaultIndexes, new List<string> { "Orders" });

                var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                using (var session = store.OpenSession())
                {
                    session.Store(new Order
                    {
                        Lines = new List<OrderLine>
                        {
                            new OrderLine { PricePerUnit = 3, Product = "Cheese", Quantity = 3 },
                            new OrderLine { PricePerUnit = 4, Product = "Bear", Quantity = 2 },
                        }
                    });
                    session.SaveChanges();
                }

                AssertEtlDone(etlDone, TimeSpan.FromMinutes(1), store.Database, config);

                var ordersCount = client.Count<object>(c => c.Index(OrdersIndexName));
                var orderLinesCount = client.Count<object>(c => c.Index(OrderLinesIndexName));

                Assert.True(ordersCount.IsValid);
                Assert.True(orderLinesCount.IsValid);

                Assert.Equal(1, ordersCount.Count);
                Assert.Equal(2, orderLinesCount.Count);

                etlDone.Reset();

                using (var session = store.OpenSession())
                {
                    session.Delete("orders/1-A");

                    session.SaveChanges();
                }

                AssertEtlDone(etlDone, TimeSpan.FromMinutes(1), store.Database, config);

                var ordersCountAfterDelete = client.Count<object>(c => c.Index(OrdersIndexName));
                var orderLinesCountAfterDelete = client.Count<object>(c => c.Index(OrderLinesIndexName));

                Assert.True(ordersCount.IsValid);
                Assert.True(orderLinesCount.IsValid);

                Assert.Equal(0, ordersCountAfterDelete.Count);
                Assert.Equal(0, orderLinesCountAfterDelete.Count);
            }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public async Task TestScriptWillHaveDocumentIdPropertiesNotAddedExplicitlyInTheScript(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new Order
                    {
                        Lines = new List<OrderLine>
                        {
                            new OrderLine { PricePerUnit = 3, Product = "Milk", Quantity = 3 },
                            new OrderLine { PricePerUnit = 4, Product = "Bear", Quantity = 2 },
                        }
                    });
                    await session.SaveChangesAsync();
                }

                var result1 = store.Maintenance.Send(new PutConnectionStringOperation<ElasticSearchConnectionString>(new ElasticSearchConnectionString
                {
                    Name = "simulate", Nodes = new[] { "http://localhost:9200" }
                }));
                Assert.NotNull(result1.RaftCommandIndex);

                var database = GetDatabase(store.Database).Result;

                using (database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                {
                    using (ElasticSearchEtl.TestScript(
                               new TestElasticSearchEtlScript
                               {
                                   DocumentId = "orders/1-A",
                                   Configuration = new ElasticSearchEtlConfiguration
                                   {
                                       Name = "simulate",
                                       ConnectionStringName = "simulate",
                                       ElasticIndexes =
                                       {
                                           new ElasticSearchIndex { IndexName = OrdersIndexName, DocumentIdProperty = "Id" },
                                           new ElasticSearchIndex { IndexName = OrderLinesIndexName, DocumentIdProperty = "OrderId" },
                                           new ElasticSearchIndex { IndexName = "NotUsedInScript", DocumentIdProperty = "OrderId" },
                                       },
                                       Transforms =
                                       {
                                           new Transformation { Collections = { "Orders" }, Name = "OrdersAndLines", Script = ScriptWithNoIdMethodUsage(jsEngineType) }
                                       }
                                   }
                               }, database, database.ServerStore, context, out var testResult))
                    {
                        var result = (ElasticSearchEtlTestScriptResult)testResult;

                        Assert.Equal(0, result.TransformationErrors.Count);

                        Assert.Equal(2, result.Summary.Count);

                        var orderLines = result.Summary.First(x => x.IndexName == OrderLinesIndexName);

                        Assert.Equal(2, orderLines.Commands.Length); // delete by query and bulk

                        Assert.Contains(@"""OrderId"":""orders/1-a""", orderLines.Commands[1]);

                        var orders = result.Summary.First(x => x.IndexName == OrdersIndexName);

                        Assert.Equal(2, orders.Commands.Length); // delete by query and bulk

                        Assert.Contains(@"""Id"":""orders/1-a""", orders.Commands[1]);
                    }
                }
            }
        }
    }
}
