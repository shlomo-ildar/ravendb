﻿using FastTests;
using FastTests.Server.JavaScript;
using Raven.Client.Documents.Operations;
using Raven.Tests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_8892 : RavenTestBase
    {
        public RavenDB_8892(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void Can_delete_property_in_patch(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "joe",
                        LastName = "doe"
                    }, "users/1");

                    session.SaveChanges();
                }

                store.Operations.Send(new PatchByQueryOperation("from Users update { delete this.Name; }")).WaitForCompletion();

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    Assert.Null(user.Name);
                    Assert.NotNull(user.LastName);
                }
            }
        }
    }
}
