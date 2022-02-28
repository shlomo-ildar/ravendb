﻿using FastTests;
using FastTests.Server.JavaScript;
using Raven.Client.Documents.Operations;
using Sparrow.Json;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_13444 : RavenTestBase
    {
        public RavenDB_13444(ITestOutputHelper output) : base(output)
        {
        }

        private class TestDocument
        {
            public int Field { get; set; }
        }

        private class Test2Document
        {
            public double Field { get; set; }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void Can_overwrite_int_to_decimal_by_patch(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new TestDocument
                    {
                        Field = 1
                    }, "testDocuments/1");
                    session.SaveChanges();
                }

                store.Operations.Send(new PatchByQueryOperation(@"
                    from TestDocuments as testDocument
                    update {
                        testDocument.Field = 2.34;
                    }"))
                    .WaitForCompletion();

                using (var session = store.OpenSession())
                {
                    var doc = session.Load<BlittableJsonReaderObject>("testDocuments/1");
                    Assert.True(doc.TryGet("Field", out LazyNumberValue field));
                    Assert.Equal(2.34, field);
                }
            }
        }

        [Theory]
        [JavaScriptEngineClassData]
        public void Patch_on_decimal_that_results_in_round_number_should_not_change_type_to_int(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Test2Document
                    {
                        Field = 1.5
                    }, "testDocuments/1");
                    session.SaveChanges();
                }

                store.Operations.Send(new PatchByQueryOperation(@"
                    from Test2Documents as testDocument
                    update {
                        testDocument.Field -= 0.5;
                    }"))
                    .WaitForCompletion();

                using (var session = store.OpenSession())
                {
                    var doc = session.Load<BlittableJsonReaderObject>("testDocuments/1");
                    Assert.True(doc.TryGet("Field", out LazyNumberValue field));
                    Assert.Equal(1.0, field);
                    Assert.Equal("1.0", field.ToString());
                }
            }
        }
    }
}
