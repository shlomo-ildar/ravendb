﻿using Raven.Client.ServerWide;
using Raven.Server.Utils;
using Sparrow.Json.Parsing;
using Raven.Server.Config.Categories;

namespace Raven.Server.ServerWide.Commands
{
    public class EditDocumentsCompressionCommand : UpdateDatabaseCommand
    {
        public DocumentsCompressionConfiguration Configuration;
        
        public EditDocumentsCompressionCommand()
        {
        }
        
        public void UpdateDatabaseRecord(DatabaseRecord databaseRecord)
        {
            databaseRecord.DocumentsCompression = Configuration;
        }

        public override void UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            record.DocumentsCompression = Configuration;
        }
        
        public EditDocumentsCompressionCommand(DocumentsCompressionConfiguration configuration, string databaseName, string uniqueRequestId) : base(databaseName, uniqueRequestId)
        {
            Configuration = configuration;
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Configuration)] = TypeConverter.ToBlittableSupportedType(Configuration);
        }
    }
}
