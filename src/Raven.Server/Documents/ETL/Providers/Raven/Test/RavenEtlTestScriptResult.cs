using System.Collections.Generic;
using Raven.Client.Documents.Commands.Batches;
using Raven.Server.Documents.ETL.Test;
using Raven.Client.ServerWide.JavaScript;

namespace Raven.Server.Documents.ETL.Providers.Raven.Test
{
    public class RavenEtlTestScriptResult : TestEtlScriptResult
    {
        public RavenEtlTestScriptResult()
        {
        }
        
        public List<ICommandData> Commands { get; set; }
    }
}
