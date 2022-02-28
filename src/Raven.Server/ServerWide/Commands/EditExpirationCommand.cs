﻿using Raven.Client.Documents.Operations.Expiration;
using Raven.Client.ServerWide;
using Raven.Server.Utils;
using Sparrow.Json.Parsing;
using Raven.Server.Config.Categories;

namespace Raven.Server.ServerWide.Commands
{
    public class EditExpirationCommand : UpdateDatabaseCommand
    {
        public ExpirationConfiguration Configuration;
        public void UpdateDatabaseRecord(DatabaseRecord databaseRecord)
        {
            databaseRecord.Expiration = Configuration;
        }

        public EditExpirationCommand()
        {
        }

        public EditExpirationCommand(ExpirationConfiguration configuration, string databaseName, string uniqueRequestId) : base(databaseName, uniqueRequestId)
        {
            Configuration = configuration;
        }

        public override void UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            record.Expiration = Configuration;
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Configuration)] = TypeConverter.ToBlittableSupportedType(Configuration);
        }
    }
}
