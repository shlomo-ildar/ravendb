﻿using System;
using System.Threading.Tasks;
using FastTests;
using FastTests.Server.JavaScript;
using FastTests.Utils;
using Raven.Client.Documents.Subscriptions;
using Sparrow.Server;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_11166 : RavenTestBase
    {
        public RavenDB_11166(ITestOutputHelper output) : base(output)
        {
        }

        private class Dog
        {
#pragma warning disable 414
            public string Name;
#pragma warning restore 414
            public string Owner;
        }

        private class Person
        {
#pragma warning disable 414
            public string Name;
#pragma warning restore 414
        }

        [Fact]
        public async Task CanUseSubscriptionWithIncludes()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Person
                    {
                        Name = "Arava"
                    }, "people/1");
                    session.Store(new Dog
                    {
                        Name = "Oscar",
                        Owner = "people/1"
                    });
                    session.SaveChanges();
                }
                var id = store.Subscriptions.Create(new SubscriptionCreationOptions
                {
                    Query = @"from Dogs include Owner"
                });

                using (var sub = store.Subscriptions.GetSubscriptionWorker<Dog>(id))
                {
                    var mre = new AsyncManualResetEvent();
                    var r = sub.Run(batch =>
                    {
                        Assert.NotEmpty(batch.Items);
                        using (var s = batch.OpenSession())
                        {
                            foreach (var item in batch.Items)
                            {
                                s.Load<Person>(item.Result.Owner);
                                var dog = s.Load<Dog>(item.Id);
                                Assert.Same(dog, item.Result);
                            }
                            Assert.Equal(0, s.Advanced.NumberOfRequests);
                        }
                        mre.Set();
                    });
                    Assert.True(await mre.WaitAsync(TimeSpan.FromSeconds(60)));
                    await sub.DisposeAsync();
                    await r;// no error
                }

            }
        }

        [Fact]
        public async Task CanUseSubscriptionRevisionsWithIncludes()
        {
            using (var store = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenSession())
                {
                    session.Store(new Person
                    {
                        Name = "Arava"
                    }, "people/1");
                    
                    session.Store(new Person
                    {
                        Name = "Karmel"
                    }, "people/2");

                    session.Store(new Dog
                    {
                        Name = "Oscar",
                        Owner = "people/1"
                    },"dogs/1");
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    session.Store(new Dog
                    {
                        Name = "Oscar",
                        Owner = "people/2"
                    },"dogs/1");
                    session.SaveChanges();
                }

                var id = store.Subscriptions.Create(new SubscriptionCreationOptions
                {
                    Query = @"from Dogs (Revisions = true) as d include d.Current.Owner, d.Previous.Owner",
                });

                using (var sub = store.Subscriptions.GetSubscriptionWorker<Revision<Dog>>(id))
                {
                    var mre = new AsyncManualResetEvent();
                    var r = sub.Run(batch =>
                    {
                        Assert.NotEmpty(batch.Items);
                        using (var s = batch.OpenSession())
                        {
                            foreach (var item in batch.Items)
                            {
                                if (item.Result.Previous == null)
                                    continue;

                                var currentOwner = s.Load<Person>(item.Result.Current.Owner);
                                Assert.Equal("Karmel", currentOwner.Name);
                                var previousOwner = s.Load<Person>(item.Result.Previous.Owner);
                                Assert.Equal("Arava", previousOwner.Name);
                            }
                            Assert.Equal(0, s.Advanced.NumberOfRequests);
                        }
                        mre.Set();
                    });
                    await mre.WaitAsync(TimeSpan.FromSeconds(60));
                    await sub.DisposeAsync();
                    await r;// no error
                }
            }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public async Task CanUseSubscriptionRevisionsWithIncludesViaJavaScript(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenSession())
                {
                    session.Store(new Person
                    {
                        Name = "Arava"
                    }, "people/1");
                    
                    session.Store(new Person
                    {
                        Name = "Karmel"
                    }, "people/2");

                    session.Store(new Dog
                    {
                        Name = "Oscar",
                        Owner = "people/1"
                    },"dogs/1");
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    session.Store(new Dog
                    {
                        Name = "Oscar",
                        Owner = "people/2"
                    },"dogs/1");
                    session.SaveChanges();
                }

                var optChaining = jsEngineType == "Jint" ? "" : "?";
                var id = store.Subscriptions.Create(new SubscriptionCreationOptions
                {
                    Query = @$"declare function f(d) {{ 
                                include(d.Current.Owner);
                                include(d.Previous{optChaining}.Owner);
                                return d;
                            }}
                            from Dogs (Revisions = true) as dog
                            select f(dog)
                            "
                });

                using (var sub = store.Subscriptions.GetSubscriptionWorker<Revision<Dog>>(id))
                {
                    var mre = new AsyncManualResetEvent();
                    var r = sub.Run(batch =>
                    {
                        Assert.NotEmpty(batch.Items);
                        using (var s = batch.OpenSession())
                        {
                            foreach (var item in batch.Items)
                            {
                                if (item.Result.Previous == null)
                                    continue;

                                var currentOwner = s.Load<Person>(item.Result.Current.Owner);
                                Assert.Equal("Karmel", currentOwner.Name);
                                var previousOwner = s.Load<Person>(item.Result.Previous.Owner);
                                Assert.Equal("Arava", previousOwner.Name);
                            }
                            Assert.Equal(0, s.Advanced.NumberOfRequests);
                        }
                        mre.Set();
                    });
                    await mre.WaitAsync(TimeSpan.FromSeconds(60));
                    await sub.DisposeAsync();
                    await r;// no error
                }
            }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public async Task CanUseSubscriptionWithIncludesViaJavaScript(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Person
                    {
                        Name = "Arava"
                    }, "people/1");
                    session.Store(new Dog
                    {
                        Name = "Oscar",
                        Owner = "people/1"
                    });
                    session.SaveChanges();
                }
                var id = store.Subscriptions.Create(new SubscriptionCreationOptions
                {
                    Query = @"declare function f(d) { 
    include(d.Owner);
    return d;
}
from Dogs as dog
select f(dog)
"
                });

                using (var sub = store.Subscriptions.GetSubscriptionWorker<Dog>(id))
                {
                    var mre = new AsyncManualResetEvent();
                    var r = sub.Run(batch =>
                    {
                        Assert.NotEmpty(batch.Items);
                        using (var s = batch.OpenSession())
                        {
                            foreach (var item in batch.Items)
                            {
                                s.Load<Person>(item.Result.Owner);
                                var dog = s.Load<Dog>(item.Id);
                                Assert.Same(dog, item.Result);
                            }
                            Assert.Equal(0, s.Advanced.NumberOfRequests);
                        }
                        mre.Set();
                    });
                    Assert.True(await mre.WaitAsync(TimeSpan.FromSeconds(60)));
                    await sub.DisposeAsync();
                    await r;// no error
                }

            }
        }
    }
}
