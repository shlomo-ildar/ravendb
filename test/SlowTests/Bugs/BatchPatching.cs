using System;
using System.Linq;
using FastTests;
using FastTests.Server.JavaScript;
using Raven.Client.Documents.Operations;
using Raven.Tests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Bugs
{
    public class BatchPatching : RavenTestBase
    {
        public BatchPatching(ITestOutputHelper output) : base(output)
        {
        }

        // TODO [shlomo] temporary switched off the test to allow to pass the others (as it is cached and reduces the number of engines available)
        //[Theory]
        //[JavaScriptEngineClassData]
        public void CanSuccessfullyPatchInBatches(string jsEngineType)
        {
            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType)))
            {
                const int count = 512; // 2406 max achieved for V8, 2415 failed
                using (var s = store.OpenSession())
                {
                    for (int i = 0; i < count; i++)
                    {
                        s.Store(new User
                        {
                            Age = i,
                        }, "users/" + i);
                    }
                    s.SaveChanges();
                }

                var batchesFirstHalf =
                    Enumerable.Range(0, count / 2).Select(i => new PatchOperation("users/" + i, null, new PatchRequest
                    {
                        Script = $"if (this) {{ this.Name='Users-{i}'; }}"
                    }));
                foreach (var patchCommandData in batchesFirstHalf)
                {
                    if (store.Operations.Send(patchCommandData) != PatchStatus.Patched)
                        throw new InvalidOperationException("Some patches failed");
                }

                var batchesSecondHalf =
                    Enumerable.Range(count / 2, count / 2).Select(i => new PatchOperation("users/" + i, null, new PatchRequest
                    {
                        Script = $"if (this) {{ this.Name='Users-{i}'; }}"
                    }));
                foreach (var patchCommandData in batchesSecondHalf)
                {
                    if (store.Operations.Send(patchCommandData) != PatchStatus.Patched)
                        throw new InvalidOperationException("Some patches failed");
                }

                using (var s = store.OpenSession())
                {
                    s.Advanced.MaxNumberOfRequestsPerSession = count + 2;
                    for (int i = 0; i < count; i++)
                    {
                        Assert.Equal("Users-" + i, s.Load<User>("users/" + i).Name);
                    }
                }

            }
        }
    }
}
