﻿using System.Linq;
using FastTests.Graph;
using FastTests.Server.JavaScript;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace FastTests.Issues
{
    public class RavenDB_12567 : RavenTestBase
    {
        public RavenDB_12567(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void Recursive_queries_should_handle_self_cycles_properly(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Dog
                    {
                        Name = "Arava",
                        Likes = new [] { "dogs/1" }
                    },"dogs/1");
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var queryResults = session.Advanced.RawQuery<JObject>(@"
                        match (Dogs as d1)- recursive as r (all) { [Likes as path]->(Dogs as d2) }
                        select {
                            Start: id(d1), 
                            Path: r.map(x => x.path).join('->')
                        }
                    ").ToList();

                    Assert.True(queryResults.Count == 1);

                    Assert.Equal("dogs/1",queryResults[0]["Start"].Value<string>());
                    Assert.Equal("dogs/1",queryResults[0]["Path"].Value<string>());
                }
            }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void Recursive_queries_with_self_cycles_and_regular_cycles_should_properly_work(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Dog
                    {
                        Name = "Arava",
                        Likes = new [] { "dogs/1", "dogs/2" }
                    },"dogs/1");

                    session.Store(new Dog
                    {
                        Name = "Oscar",
                        Likes = new [] { "dogs/1" }
                    },"dogs/2");
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var queryResults = session.Advanced.RawQuery<JObject>(@"
                        match (Dogs as d1)- recursive as r (all) { [Likes as path]->(Dogs as d2) }
                        select {
                            Start: id(d1), 
                            Path: r.map(x => x.path).join('->')
                        }
                    ").ToList();

                    Assert.Equal(queryResults.Count, 6);

                    var stronglyTypedQueryResults = queryResults.Select(x => new
                    {
                        Start = x["Start"].Value<string>(),
                        Path = x["Path"].Value<string>()
                    }).ToArray();

                    var dog1StartResults = stronglyTypedQueryResults.Where(x => x.Start == "dogs/1").ToArray();
                    var dog2StartResults = stronglyTypedQueryResults.Where(x => x.Start == "dogs/2").ToArray();

                    Assert.Contains(dog1StartResults, x => x.Path == "dogs/2->dogs/1");
                    Assert.Contains(dog1StartResults, x => x.Path == "dogs/1->dogs/2");
                    Assert.Contains(dog1StartResults, x => x.Path == "dogs/2");
                    Assert.Contains(dog1StartResults, x => x.Path == "dogs/1");

                    Assert.Contains(dog2StartResults, x => x.Path == "dogs/1");
                    Assert.Contains(dog2StartResults, x => x.Path == "dogs/1->dogs/2");

                }
            }
        }
    }
}
