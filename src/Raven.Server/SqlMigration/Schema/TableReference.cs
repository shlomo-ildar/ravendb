using System.Collections.Generic;
using Raven.Server.Utils;
using Sparrow.Json.Parsing;
using Raven.Client.ServerWide.JavaScript;

namespace Raven.Server.SqlMigration.Schema
{
    public class TableReference : IDynamicJson
    {
        protected IJavaScriptOptions _jsOptions;
        public string Schema { get; set; }
        public string Table { get; set; }
        public List<string> Columns { get; set; } = new List<string>();

        public TableReference(IJavaScriptOptions jsOptions, string schema, string table)
        {
            _jsOptions = jsOptions;
            Schema = schema;
            Table = table;
        }

        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(Table)] = Table,
                [nameof(Schema)] = Schema,
                [nameof(Columns)] = TypeConverter.ToBlittableSupportedType(Columns)
            };
        }
    }
}
