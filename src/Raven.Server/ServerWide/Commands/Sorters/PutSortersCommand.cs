﻿using System.Collections.Generic;
using Raven.Client.Documents.Queries.Sorting;
using Raven.Client.ServerWide;
using Raven.Server.Utils;
using Sparrow.Json.Parsing;
using Raven.Server.Config.Categories;

namespace Raven.Server.ServerWide.Commands.Sorters
{
    public class PutSortersCommand : UpdateDatabaseCommand
    {
        public List<SorterDefinition> Sorters = new List<SorterDefinition>();

        public PutSortersCommand()
        {
            // for deserialization
        }

        public PutSortersCommand(string databaseName, string uniqueRequestId)
            : base(databaseName, uniqueRequestId)
        {
        }

        public override void UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            if (Sorters != null)
            {
                foreach (var definition in Sorters)
                    record.AddSorter(definition);
            }
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Sorters)] = TypeConverter.ToBlittableSupportedType(Sorters);
        }
    }
}
