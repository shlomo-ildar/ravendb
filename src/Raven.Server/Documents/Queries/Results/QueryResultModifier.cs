using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Lucene.Net.Documents;
using Lucene.Net.Store;
using Microsoft.Extensions.Azure;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Includes;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Documents;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Results.TimeSeries;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.Json;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Server.Json.Sync;
using Raven.Client.ServerWide.JavaScript;

namespace Raven.Server.Documents.Queries.Results
{
    public class QueryResultModifier : JsBlittableBridge.IResultModifier
    {
        public static readonly QueryResultModifier Instance = new QueryResultModifier();

        public void Modify(JsHandle json)
        {
            using (var jsMetadata = json.GetProperty(Constants.Documents.Metadata.Key))
            {
                var engine = json.Engine;

                if (!jsMetadata.IsObject)
                {
                    using (var jsMetadataNew = engine.CreateObject())
                        jsMetadata.Set(jsMetadataNew);
                    json.SetProperty(Constants.Documents.Metadata.Key, jsMetadata, throwOnError:false);
                }

                jsMetadata.SetProperty(Constants.Documents.Metadata.Projection, engine.CreateValue(true), throwOnError:false);
            }
        }
    }
}
