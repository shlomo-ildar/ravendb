using System;
using System.Collections.Generic;
using Jint.Native.Function;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Server.Config;

namespace Raven.Server.Documents.Indexes.Static
{
    public abstract class AbstractCountersAndTimeSeriesJavaScriptIndex : AbstractJavaScriptIndex
    {
        private const string NameProperty = "name";

        protected AbstractCountersAndTimeSeriesJavaScriptIndex(IndexDefinition definition, RavenConfiguration configuration, string mapPrefix, string allItems, long indexVersion)
            : base(definition, configuration, mappingFunctions => ModifyMappingFunctions(mappingFunctions, mapPrefix), GetMapCode(allItems), indexVersion)
        {
        }

        private static string GetMapCode(string allItems)
        {
            if (allItems is null)
                throw new ArgumentNullException(nameof(allItems));

            return @$"
function map() {{
    var collectionArg = null;
    var nameArg = null;
    var lambdaArg = null;

    if (arguments.length == 3) {{
        collectionArg = arguments[0];
        nameArg = arguments[1];
        lambdaArg = arguments[2];
    }} else if (arguments.length == 2) {{
        collectionArg = arguments[0];
        nameArg = '{allItems}';
        lambdaArg = arguments[1];
    }} else if (arguments.length == 1) {{
        collectionArg = '{Constants.Documents.Collections.AllDocumentsCollection}';
        nameArg = '{allItems}';
        lambdaArg = arguments[0];
    }}

    var map = {{
        collection: collectionArg,
        name: nameArg,
        method: lambdaArg,
        moreArgs: Array.prototype.slice.call(arguments, arguments.length)
    }};

    globalDefinition.maps.push(map);
}}";
        }

        private static void ModifyMappingFunctions(List<string> mappingFunctions, string mapPrefix)
        {
            if (mapPrefix is null)
                throw new ArgumentNullException(nameof(mapPrefix));

            for (int i = 0; i < mappingFunctions.Count; i++)
            {
                if (mappingFunctions[i].StartsWith(mapPrefix, StringComparison.OrdinalIgnoreCase) == false)
                    continue;

                mappingFunctions[i] = mappingFunctions[i].Substring(mapPrefix.Length);
            }
        }

        protected override void OnInitializeEngine()
        {
        }

        protected override void ProcessMaps(List<string> mapList, List<MapMetadata> mapReferencedCollections, out Dictionary<string, Dictionary<string, List<JavaScriptMapOperation>>> collectionFunctions)
        {
            var mapsArrayStatic = _definitionsForParsingJint.GetProperty(MapsProperty).Value;
            if (mapsArrayStatic.IsNull() || mapsArrayStatic.IsUndefined() || mapsArrayStatic.IsArray() == false)
                ThrowIndexCreationException($"JavaScript: doesn't contain any map function or '{GlobalDefinitions}.{Maps}' was modified in the script");

            var mapsStatic = mapsArrayStatic.AsArray();
            if (mapsStatic.Length == 0)
                ThrowIndexCreationException($"JavaScript: doesn't contain any map functions or '{GlobalDefinitions}.{Maps}' was modified in the script");


            using (var maps = _definitions.GetProperty(MapsProperty))
            {
                if (maps.IsNull || maps.IsUndefined || maps.IsArray == false)
                    ThrowIndexCreationException($"doesn't contain any map function or '{GlobalDefinitions}.{Maps}' was modified in the script");

                if (maps.ArrayLength == 0)
                    ThrowIndexCreationException($"doesn't contain any map functions or '{GlobalDefinitions}.{Maps}' was modified in the script");

                collectionFunctions = new Dictionary<string, Dictionary<string, List<JavaScriptMapOperation>>>();
                for (int i = 0; i < maps.ArrayLength; i++)
                {
                    var mapObjStatic = mapsStatic.Get(i.ToString());
                    if (mapObjStatic.IsNull() || mapObjStatic.IsUndefined() || mapObjStatic.IsObject() == false)
                        ThrowIndexCreationException($"JavaScript: map function #{i} is not a valid object");
                    var mapStatic = mapObjStatic.AsObject();
                    if (mapStatic.HasProperty(MethodProperty) == false)
                        ThrowIndexCreationException($"JavaScript: map function #{i} is missing its {MethodProperty} property");
                    var funcStatic = mapStatic.Get(MethodProperty).As<FunctionInstance>();
                    if (funcStatic == null)
                        ThrowIndexCreationException($"JavaScript: map function #{i} {MethodProperty} property isn't a 'FunctionInstance'");

                    using (var map = maps.GetProperty(i))
                    {
                        if (map.IsNull || map.IsUndefined || map.IsObject == false)
                            ThrowIndexCreationException($"map function #{i} is not a valid object");
                        if (map.HasProperty(CollectionProperty) == false)
                            ThrowIndexCreationException($"map function #{i} is missing a collection name");
                        using (var mapCollectionStr = map.GetProperty(CollectionProperty))
                        {
                            if (mapCollectionStr.IsStringEx == false)
                                ThrowIndexCreationException($"map function #{i} collection name isn't a string");
                            var mapCollection = mapCollectionStr.AsString;

                            if (collectionFunctions.TryGetValue(mapCollection, out var subCollectionFunctions) == false)
                                collectionFunctions[mapCollection] = subCollectionFunctions = new Dictionary<string, List<JavaScriptMapOperation>>();

                            if (map.HasProperty(NameProperty) == false)
                                ThrowIndexCreationException($"map function #{i} is missing its {NameProperty} property");
                            using (var mapNameStr = map.GetProperty(NameProperty))
                            {
                                if (mapNameStr.IsStringEx == false)
                                    ThrowIndexCreationException($"map function #{i} TimeSeries name isn't a string");
                                var mapName = mapNameStr.AsString;

                                if (subCollectionFunctions.TryGetValue(mapName, out var list) == false)
                                    subCollectionFunctions[mapName] = list = new List<JavaScriptMapOperation>();

                                if (map.HasOwnProperty(MethodProperty) == false)
                                    ThrowIndexCreationException($"map function #{i} is missing its {MethodProperty} property");

                                using (var func = map.GetProperty(MethodProperty))
                                {
                                    if (func.IsFunction == false)
                                        ThrowIndexCreationException($"map function #{i} {MethodProperty} property isn't a 'FunctionInstance'");

                                    var operation = new JavaScriptMapOperation(JsIndexUtils, funcStatic, func, Definition.Name, mapList[i]);
                                    if (mapStatic.HasOwnProperty(MoreArgsProperty))
                                    {
                                        var moreArgsObjJint = mapStatic.Get(MoreArgsProperty);
                                        if (moreArgsObjJint.IsArray())
                                        {
                                            var arrayJint = moreArgsObjJint.AsArray();  
                                            if (arrayJint.Length > 0)
                                            {
                                                operation.MoreArguments = arrayJint;
                                            }
                                        }
                                    }

                                    operation.Analyze(EngineJint);
                                    if (ReferencedCollections.TryGetValue(mapCollection, out var collectionNames) == false)
                                    {
                                        collectionNames = new HashSet<CollectionName>();
                                        ReferencedCollections.Add(mapCollection, collectionNames);
                                    }

                                    collectionNames.UnionWith(mapReferencedCollections[i].ReferencedCollections);

                                    if (mapReferencedCollections[i].HasCompareExchangeReferences)
                                        CollectionsWithCompareExchangeReferences.Add(mapCollection);

                                    list.Add(operation);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
