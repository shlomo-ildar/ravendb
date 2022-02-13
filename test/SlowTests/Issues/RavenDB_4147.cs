﻿// -----------------------------------------------------------------------
//  <copyright file="RavenDB_4147.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using FastTests;
using FastTests.Server.JavaScript;
using Raven.Client.Documents.Operations;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_4147 : RavenTestBase
    {
        public RavenDB_4147(ITestOutputHelper output) : base(output)
        {
        }

        public class Dates
        {
            public string Date1 { get; set; }
            public string Date2 { get; set; }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void LastModifiedShouldBeAvailableInPatchContext(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Dates());
                    session.SaveChanges();
                }

                WaitForIndexing(store);

                store.Operations.Send(new PatchOperation("dates/1-A", null, new PatchRequest
                {
                    Script = "this.Date1 = lastModified(this);"
                }));

                using (var session = store.OpenSession())
                {
                    var dates = session.Load<Dates>("dates/1-A");
                    Assert.NotNull(dates.Date1);
                }
            }
        }
    }
}
