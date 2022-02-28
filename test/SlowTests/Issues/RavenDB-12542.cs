﻿using System.Linq;
using FastTests;
using FastTests.Server.JavaScript;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Tests.Infrastructure.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_12542 : RavenTestBase
    {
        public RavenDB_12542(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void Single_node_index_query_should_work()
        {
            using (var store = GetDocumentStore())
            {
                CreateNorthwindDatabase(store, Raven.Client.Documents.Smuggler.DatabaseItemType.Documents | Raven.Client.Documents.Smuggler.DatabaseItemType.Indexes);
                WaitForIndexing(store);
                using (var session = store.OpenSession())
                {
                    var queryResultsFromIndex =
                        session.Advanced.RawQuery<JObject>("match (index 'Orders/Totals' as o)").ToArray();

                    var queryResultsFromCollection =
                        session.Advanced.RawQuery<JObject>("match (Orders as o)").ToArray();

                    Assert.Equal(queryResultsFromCollection, queryResultsFromIndex);
                }
            }
        }

        [Fact]
        public void Where_clause_in_index_node_expression_should_work()
        {
            using (var store = GetDocumentStore())
            {
                CreateNorthwindDatabase(store, Raven.Client.Documents.Smuggler.DatabaseItemType.Documents | Raven.Client.Documents.Smuggler.DatabaseItemType.Indexes);
                new Index_Orders_ByEmployee().Execute(store);
                WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var queryResultsFromIndex =
                        session.Advanced.RawQuery<JObject>("match (index 'Orders/Totals' where Employee = 'employees/4-A')").ToArray();

                    var queryResultsFromCollection =
                        session.Advanced.RawQuery<JObject>("from index 'Orders/ByEmployee' where Employee = 'employees/4-A'").ToArray();

                    Assert.Equal(queryResultsFromCollection, queryResultsFromIndex);
                }
            }
        }

        public class Index_Orders_ByEmployee : AbstractIndexCreationTask
        {
            public override string IndexName => "Orders/ByEmployee";

            public override IndexDefinition CreateIndexDefinition()
            {
                return new IndexDefinition
                {
                    Maps =
                    {
                        @"from order in docs.Orders select new { order.Employee }"
                    }
                };
            }
        }

        [Fact]
        public void Index_query_expression_inside_edge_expressions_should_throw()
        {
            using (var store = GetDocumentStore())
            {
                CreateNorthwindDatabase(store, Raven.Client.Documents.Smuggler.DatabaseItemType.Documents | Raven.Client.Documents.Smuggler.DatabaseItemType.Indexes);
                WaitForIndexing(store);
                using (var session = store.OpenSession())
                {
                    //this use-case is a bit silly, but why not throw explicit & informative exception even in such case :)
                    Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<JObject>("match (Employee as e)-[index 'Orders/Totals' as o select Employee]->(Employee as anotherE)").ToArray());
                }
            }
        }

        [Fact]
        public void Select_clause_inside_node_expressions_should_throw()
        {
            using (var store = GetDocumentStore())
            {
                CreateNorthwindDatabase(store, Raven.Client.Documents.Smuggler.DatabaseItemType.Documents | Raven.Client.Documents.Smuggler.DatabaseItemType.Indexes);
                WaitForIndexing(store);
                using (var session = store.OpenSession())
                {
                    Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<JObject>("match (index 'Orders/Totals' as o select Employee)").ToArray());
                    Assert.Throws<InvalidQueryException>(() => session.Advanced.RawQuery<JObject>("match (Orders as o select Employee)").ToArray());
                }
            }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void Pattern_match_node_index_query_should_work(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                CreateNorthwindDatabase(store, Raven.Client.Documents.Smuggler.DatabaseItemType.Documents | Raven.Client.Documents.Smuggler.DatabaseItemType.Indexes);
                new IndexOrdersProductsWithPricePerUnit().Execute(store);
                WaitForIndexing(store);
                using (var session = store.OpenSession())
                {
                    var queryResults =
                        session.Advanced.RawQuery<JObject>(
                            @"match (index 'Orders/Totals')-[Lines where PricePerUnit > 200 select Product]->(index 'Product/Search' as ps)
                              select id(ps) as ProductId
                             ").ToArray().Select(x => x["ProductId"].Value<string>()).ToArray();

                    var referenceQueryResults = session.Advanced.RawQuery<Order>(@"from index 'Orders/ProductsWithPricePerUnit' where PricePerUnit > 200")
                        .ToArray()
                        .SelectMany(x => x.Lines).ToArray().Where(x => x.PricePerUnit > 200).Select(x => x.Product).ToArray();

                    Assert.Equal(referenceQueryResults, queryResults);
                }
            }
        }

        public class IndexOrdersProductsWithPricePerUnit : AbstractIndexCreationTask
        {
            public override string IndexName => "Orders/ProductsWithPricePerUnit";

            public override IndexDefinition CreateIndexDefinition()
            {
                return new IndexDefinition
                {
                    Maps =
                    {
                          @"from order in docs.Orders
                            from orderLine in order.Lines
                            select new { Product = orderLine.Product, PricePerUnit = orderLine.PricePerUnit }"
                    }
                };
            }
        }
    }
}
