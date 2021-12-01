﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FastTests;
using FastTests.Server.JavaScript;
using Jint.Constraints;
using Orders;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;
using Raven.Server.Config.Settings;
using Raven.Server.Documents.Indexes.Static;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_14687 : RavenTestBase
    {
        public RavenDB_14687(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public async Task IndexSpecificSettingShouldBeRespected(string jsEngineType)
        {
            var initialStrictModeForScript = false;
            var initialMaxStepsForScript = 10;
            var initialMaxDurationForScript = new TimeSetting(20, TimeUnit.Milliseconds);

            using (var store = GetDocumentStore(Options.ForJavaScriptEngine(jsEngineType, 
                record =>
                {
                    record.Settings[RavenConfiguration.GetKey(x => x.Indexing.JsStrictMode)] = initialStrictModeForScript.ToString();
                    record.Settings[RavenConfiguration.GetKey(x => x.Indexing.JsMaxSteps)] = initialMaxStepsForScript.ToString();
                    record.Settings[RavenConfiguration.GetKey(x => x.Indexing.JsMaxDuration)] = initialMaxDurationForScript.GetValue(TimeUnit.Milliseconds).ToString();
                })))
            {
                var index = new MyJSIndex(jsEngineType, null, null, null);
                index.Execute(store);

                var database = await GetDocumentDatabaseInstanceFor(store);

                var indexInstance1 = (MapIndex)database.IndexStore.GetIndex(index.IndexName);
                var compiled1 = (JavaScriptIndex)indexInstance1._compiled;

                Assert.Equal(initialStrictModeForScript, compiled1.EngineHandle.JsOptions.StrictMode);
                Assert.Equal(initialMaxStepsForScript, compiled1.EngineHandle.JsOptions.MaxSteps);
                Assert.Equal(initialMaxDurationForScript.GetValue(TimeUnit.Milliseconds), compiled1.EngineHandle.JsOptions.MaxDuration.GetValue(TimeUnit.Milliseconds));

                const bool strictModeForScript = true;
                const int maxStepsForScript = 1001;
                var maxDurationForScript = new TimeSetting(101, TimeUnit.Milliseconds);
                index = new MyJSIndex(jsEngineType, strictModeForScript, maxStepsForScript, maxDurationForScript);
                index.Execute(store);

                WaitForIndexing(store);

                var indexInstance2 = (MapIndex)database.IndexStore.GetIndex(index.IndexName);
                var compiled2 = (JavaScriptIndex)indexInstance2._compiled;

                Assert.NotEqual(indexInstance1, indexInstance2);
                Assert.NotEqual(compiled1, compiled2);

                Assert.Equal(strictModeForScript, compiled2.EngineHandle.JsOptions.StrictMode);
                Assert.Equal(maxStepsForScript, compiled2.EngineHandle.JsOptions.MaxSteps);
                Assert.Equal(maxDurationForScript.GetValue(TimeUnit.Milliseconds), compiled2.EngineHandle.JsOptions.MaxDuration.GetValue(TimeUnit.Milliseconds));

                using (var session = store.OpenSession())
                {
                    session.Store(new Company());

                    session.SaveChanges();
                }

                WaitForIndexing(store);

                RavenTestHelper.AssertNoIndexErrors(store);
            }
        }

        private class MyJSIndex : AbstractJavaScriptIndexCreationTask
        {
            public MyJSIndex(string jsEngineType, bool? strictModeForScript, int? maxStepsForScript, TimeSetting? maxDurationForScript)
            {
                var optionalChaining = jsEngineType switch
                {
                    "Jint" => "",
                    "V8" => "?",
                    _ => throw new NotSupportedException($"Not supported jsEngineType '{jsEngineType}'.")
                };

                var mapCode = @"
map('Companies', (company) => {
/*JINT_START*/
//})
/*JINT_END*/
    var x = [];
    for (var i = 0; i < 50; i++) {
        x.push(i);
    }
    if (company.Address{optionalChaining}.Country === 'USA') {
        return {
            Name: company.Name,
            Phone: company.Phone,
            City: company.Address.City
        };
    }
})";

                mapCode = mapCode.Replace("{optionalChaining}", optionalChaining);
                
                Maps = new HashSet<string>()
                {
                    mapCode
                };

                if (strictModeForScript.HasValue)
                    Configuration[RavenConfiguration.GetKey(x => x.Indexing.JsStrictMode)] = strictModeForScript.ToString();
                if (maxStepsForScript.HasValue)
                    Configuration[RavenConfiguration.GetKey(x => x.Indexing.JsMaxSteps)] = maxStepsForScript.ToString();
                if (maxDurationForScript.HasValue)
                    Configuration[RavenConfiguration.GetKey(x => x.Indexing.JsMaxDuration)] = maxDurationForScript?.GetValue(TimeUnit.Milliseconds).ToString();
            }
        }
    }
}
