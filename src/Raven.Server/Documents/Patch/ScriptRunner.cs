﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Jint.Native;
using Raven.Client.Exceptions.Documents;
using Raven.Client.Exceptions.Documents.Patching;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Results.TimeSeries;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using JavaScriptException = Jint.Runtime.JavaScriptException;
using Raven.Client.ServerWide.JavaScript;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.TimeSeries;
using PatchJint = Raven.Server.Documents.Patch.Jint;
using PatchV8 = Raven.Server.Documents.Patch.V8;
using V8.Net;

namespace Raven.Server.Documents.Patch
{
    public partial class ScriptRunner
    {
        public class Holder
        {
            public ScriptRunner Parent;
            public SingleRun Value;
            public WeakReference<SingleRun> WeakValue;
        }

        public IJavaScriptOptions JsOptions;
        
        protected readonly ConcurrentQueue<Holder> _cache = new ConcurrentQueue<Holder>();
        protected readonly DocumentDatabase _db;
        protected readonly RavenConfiguration _configuration;
        internal readonly bool _enableClr;
        protected readonly DateTime _creationTime;
        public readonly List<string> ScriptsSource = new List<string>();

        public int NumberOfCachedScripts => _cache.Count(x =>
            x.Value != null ||
            x.WeakValue?.TryGetTarget(out _) == true);

        internal readonly Dictionary<string, DeclaredFunction> TimeSeriesDeclaration = new Dictionary<string, DeclaredFunction>();

        public long Runs;
        protected DateTime _lastRun;

        public string ScriptType { get; internal set; }

        private IJsEngineHandle _tryScriptEngineHandle;

        public static ScriptRunner CreateScriptRunner(DocumentDatabase db, RavenConfiguration configuration, bool enableClr)
        {
            return new ScriptRunner(db, configuration, enableClr);
        }

        public ScriptRunner(DocumentDatabase db, RavenConfiguration configuration, bool enableClr)
        {
            _db = db;
            _configuration = configuration;
            JsOptions = db?.JsOptions ?? _configuration.JavaScript;
            _enableClr = enableClr;
            _creationTime = DateTime.UtcNow;

            var engineType = JsOptions?.EngineType ?? JavaScriptEngineType.Jint;
            _tryScriptEngineHandle = engineType switch
            {
                JavaScriptEngineType.Jint => new PatchJint.JintEngineEx(),
                JavaScriptEngineType.V8 => new PatchV8.V8EngineEx(),
                _ => throw new NotSupportedException($"Not supported JS engine type '{JsOptions}'.")
            };
        }

        public DynamicJsonValue GetDebugInfo(bool detailed = false)
        {
            var djv = new DynamicJsonValue
            {
                ["Type"] = ScriptType,
                ["CreationTime"] = _creationTime,
                ["LastRun"] = _lastRun,
                ["Runs"] = Runs,
                ["CachedScriptsCount"] = _cache.Count
            };
            if (detailed)
                djv["ScriptsSource"] = ScriptsSource;

            return djv;
        }

        public void AddScript(string script)
        {
            ScriptsSource.Add(script);
        }

        public void AddTimeSeriesDeclaration(DeclaredFunction func)
        {
            TimeSeriesDeclaration.Add(func.Name, func);
        }

        public ReturnRun GetRunner(out SingleRun run)
        {
            _lastRun = DateTime.UtcNow;
            Interlocked.Increment(ref Runs);
            if (_cache.TryDequeue(out var holder) == false)
            {
                holder = new Holder
                {
                    Parent = this
                };
            }

            if (holder.Value == null)
            {
                if (holder.WeakValue != null &&
                    holder.WeakValue.TryGetTarget(out run))
                {
                    holder.Value = run;
                    holder.WeakValue = null;
                }
                else
                {
                    holder.Value = new ScriptRunner.SingleRun(_db, _configuration, this, ScriptsSource);
                }
            }

            run = holder.Value;

            return new ReturnRun(run, holder);
        }

        public void TryCompileScript(string script)
        {
            _tryScriptEngineHandle.TryCompileScript(script);
        }

        public static unsafe DateTime GetDateArg(JsHandle arg, string signature, string argName)
        {
            if (arg.IsDate)
                return arg.AsDate;

            if (arg.IsStringEx == false)
                ThrowInvalidDateArgument();

            var s = arg.AsString;
            fixed (char* pValue = s)
            {
                var result = LazyStringParser.TryParseDateTime(pValue, s.Length, out DateTime dt, out _);
                if (result != LazyStringParser.Result.DateTime)
                    ThrowInvalidDateArgument();

                return dt;
            }

            void ThrowInvalidDateArgument() =>
                throw new ArgumentException($"{signature} : {argName} must be of type 'DateInstance' or a DateTime string. {GetTypes(arg)}");
        }

        private static DateTime GetTimeSeriesDateArg(JsHandle arg, string signature, string argName)
        {
            if (arg.IsDate)
                return arg.AsDate;

            if (arg.IsStringEx == false)
                throw new ArgumentException($"{signature} : {argName} must be of type 'DateInstance' or a DateTime string. {GetTypes(arg)}");

            return TimeSeriesRetriever.ParseDateTime(arg.AsString);
        }
        
        private static string GetTypes(JsHandle value) => $"JintType({value.ValueType}) .NETType({value.GetType().Name})";
 
        
        public partial class SingleRun
        {
            public IJsEngineHandle ScriptEngineHandle;
            public JavaScriptUtilsBase JsUtilsBase;
            
            protected readonly DocumentDatabase _database;
            protected readonly RavenConfiguration _configuration;
            protected readonly IJavaScriptOptions _jsOptions;
            protected JavaScriptEngineType _jsEngineType;

            protected readonly ScriptRunner _runnerBase;
            protected QueryTimingsScope _scope;
            protected QueryTimingsScope _loadScope;
            protected DocumentsOperationContext _docsCtx;
            protected JsonOperationContext _jsonCtx;
            public PatchDebugActions DebugActions;
            public bool DebugMode;
            public List<string> DebugOutput;
            public bool PutOrDeleteCalled;
            public HashSet<string> Includes;
            public HashSet<string> IncludeRevisionsChangeVectors;
            public DateTime? IncludeRevisionByDateTimeBefore;
            public HashSet<string> CompareExchangeValueIncludes;
            protected HashSet<string> _documentIds;

            public bool ReadOnly
            {
                get => JsUtilsBase.ReadOnly;
                set => JsUtilsBase.ReadOnly = value;
            }
            
            public string OriginalDocumentId;
            public bool RefreshOriginalDocument;
            protected readonly ConcurrentLruRegexCache _regexCache = new ConcurrentLruRegexCache(1024);
            public HashSet<string> DocumentCountersToUpdate;
            public HashSet<string> DocumentTimeSeriesToUpdate;

            protected const string _timeSeriesSignature = "timeseries(doc, name)";
            public const string GetMetadataMethod = "getMetadata";

            public SingleRun(DocumentDatabase database, RavenConfiguration configuration, ScriptRunner runner, List<string> scriptsSource)
            {
                _database = database;
                _configuration = configuration;
                _runnerBase = runner;
                _jsOptions = runner.JsOptions;
                _jsEngineType = _jsOptions.EngineType;

                InitializeEngineSpecific(scriptsSource);
            }
            
            ~SingleRun()
            {
                DisposeArgs();
            }

            public static InternalHandle DummyJsCallbackV8(V8Engine engine, bool isConstructCall, InternalHandle self, params InternalHandle[] args)
            {
                throw new InvalidOperationException("Failed to set JS callback for V8");
            }
                
            public static JsValue DummyJsCallbackJint(JsValue self, JsValue[] args)
            {
                throw new InvalidOperationException("Failed to set JS callback for Jint");
            }
                
            public void InitializeEngineSpecific(List<string> scriptsSource)
            {
                switch (_jsEngineType)
                {
                    case JavaScriptEngineType.Jint:
                        InitializeJint();
                        break;
                    case JavaScriptEngineType.V8:
                        InitializeV8();
                        break;
                    default:
                        throw new NotSupportedException($"Not supported JS engine kind '{_jsEngineType}'.");
                }

                ScriptEngineHandle.SetGlobalClrCallBack("getMetadata", (JsUtilsJint != null ? JsUtilsJint.GetMetadata : DummyJsCallbackJint, JsUtilsV8 != null ? JsUtilsV8.GetMetadata : DummyJsCallbackV8));
                ScriptEngineHandle.SetGlobalClrCallBack("metadataFor", (JsUtilsJint != null ? JsUtilsJint.GetMetadata : DummyJsCallbackJint, JsUtilsV8 != null ? JsUtilsV8.GetMetadata : DummyJsCallbackV8));
                ScriptEngineHandle.SetGlobalClrCallBack("id", (JsUtilsJint != null ? JsUtilsJint.GetDocumentId : DummyJsCallbackJint, JsUtilsV8 != null ? JsUtilsV8.GetDocumentId : DummyJsCallbackV8));

                ScriptEngineHandle.SetGlobalClrCallBack("output", (OutputDebugJint, OutputDebugV8));

                //console.log
                using (var consoleObject = ScriptEngineHandle.CreateObject())
                {
                    using (var jsFuncLog = ScriptEngineHandle.CreateClrCallBack("log", (OutputDebugJint, OutputDebugV8), true))
                    {
                        consoleObject.FastAddProperty("log", jsFuncLog, false, false, false);
                    }
                    ScriptEngineHandle.SetGlobalProperty("console", consoleObject);
                }

                //spatial.distance
                using (var spatialObject = ScriptEngineHandle.CreateObject())
                {
                    ScriptEngineHandle.SetGlobalProperty("spatial", spatialObject);
                    using (var jsFuncSpatial = ScriptEngineHandle.CreateClrCallBack("distance", (Spatial_DistanceJint, Spatial_DistanceV8), true))
                    {
                        spatialObject.FastAddProperty("distance", jsFuncSpatial, false, false, false);
                        ScriptEngineHandle.SetGlobalProperty("spatial.distance", jsFuncSpatial);
                    }
                }

                // includes
                using (var includesObject = ScriptEngineHandle.CreateObject())
                {
                    ScriptEngineHandle.SetGlobalProperty("includes", includesObject);
                    using (var jsFuncIncludeDocument = ScriptEngineHandle.CreateClrCallBack("include", (IncludeDocJint, IncludeDocV8), true))
                    {
                        includesObject.FastAddProperty("document", jsFuncIncludeDocument, false, false, false);
                        // includes - backward compatibility
                        ScriptEngineHandle.SetGlobalProperty("include", jsFuncIncludeDocument);
                    }
                    using (var jsFuncIncludeCompareExchangeValue = ScriptEngineHandle.CreateClrCallBack("cmpxchg", (IncludeCompareExchangeValueJint, IncludeCompareExchangeValueV8), true))
                    {              
                        includesObject.FastAddProperty("cmpxchg", jsFuncIncludeCompareExchangeValue, false, false, false);
                    }
                    using (var jsFuncIncludeRevisions = ScriptEngineHandle.CreateClrCallBack("revisions", (IncludeRevisionsJint, IncludeRevisionsV8), true))
                    {                    
                        includesObject.FastAddProperty("revisions", jsFuncIncludeRevisions, false, false, false);
                    }
                }

                ScriptEngineHandle.SetGlobalClrCallBack("load", (LoadDocumentJint, LoadDocumentV8));
                ScriptEngineHandle.SetGlobalClrCallBack("LoadDocument", (ThrowOnLoadDocumentJint, ThrowOnLoadDocumentV8));

                ScriptEngineHandle.SetGlobalClrCallBack("loadPath", (LoadDocumentByPathJint, LoadDocumentByPathV8));
                ScriptEngineHandle.SetGlobalClrCallBack("del", (DeleteDocumentJint, DeleteDocumentV8));
                ScriptEngineHandle.SetGlobalClrCallBack("DeleteDocument", (ThrowOnDeleteDocumentJint, ThrowOnDeleteDocumentV8));
                ScriptEngineHandle.SetGlobalClrCallBack("put", (PutDocumentJint, PutDocumentV8));
                ScriptEngineHandle.SetGlobalClrCallBack("PutDocument", (ThrowOnPutDocumentJint, ThrowOnPutDocumentV8));
                ScriptEngineHandle.SetGlobalClrCallBack("cmpxchg", (CompareExchangeJint, CompareExchangeV8));

                ScriptEngineHandle.SetGlobalClrCallBack("counter", (GetCounterJint, GetCounterV8));
                ScriptEngineHandle.SetGlobalClrCallBack("counterRaw", (GetCounterRawJint, GetCounterRawV8));
                ScriptEngineHandle.SetGlobalClrCallBack("incrementCounter", (IncrementCounterJint, IncrementCounterV8));
                ScriptEngineHandle.SetGlobalClrCallBack("deleteCounter", (DeleteCounterJint, DeleteCounterV8));

                ScriptEngineHandle.SetGlobalClrCallBack("lastModified", (GetLastModifiedJint, GetLastModifiedV8));

                ScriptEngineHandle.SetGlobalClrCallBack("startsWith", (StartsWithJint, StartsWithV8));
                ScriptEngineHandle.SetGlobalClrCallBack("endsWith", (EndsWithJint, EndsWithV8));
                ScriptEngineHandle.SetGlobalClrCallBack("regex", (RegexJint, RegexV8));

                ScriptEngineHandle.SetGlobalClrCallBack("Raven_ExplodeArgs", (ExplodeArgsJint, ExplodeArgsV8));
                ScriptEngineHandle.SetGlobalClrCallBack("Raven_Min", (Raven_MinJint, Raven_MinV8));
                ScriptEngineHandle.SetGlobalClrCallBack("Raven_Max", (Raven_MaxJint, Raven_MaxV8));

                ScriptEngineHandle.SetGlobalClrCallBack("convertJsTimeToTimeSpanString", (ConvertJsTimeToTimeSpanStringJint, ConvertJsTimeToTimeSpanStringV8));
                ScriptEngineHandle.SetGlobalClrCallBack("convertToTimeSpanString", (ConvertToTimeSpanStringJint, ConvertToTimeSpanStringV8));
                ScriptEngineHandle.SetGlobalClrCallBack("compareDates", (CompareDatesJint, CompareDatesV8));

                ScriptEngineHandle.SetGlobalClrCallBack("toStringWithFormat", (ToStringWithFormatJint, ToStringWithFormatV8));

                ScriptEngineHandle.SetGlobalClrCallBack("scalarToRawString", (ScalarToRawStringJint, ScalarToRawStringV8));

                //TimeSeriesV8
                ScriptEngineHandle.SetGlobalClrCallBack("timeseries", (TimeSeriesJint, TimeSeriesV8));
                ScriptEngineHandle.Execute(ScriptRunnerCache.PolyfillJs, "polyfill.js");

                foreach (var script in scriptsSource)
                {
                    try
                    {
                        ScriptEngineHandle.Execute(script);
                    }
                    catch (Exception e)
                    {
                        throw new JavaScriptParseException("Failed to parse: " + Environment.NewLine + script, e);
                    }
                }

                foreach (var ts in _runnerBase.TimeSeriesDeclaration)
                {
                    ScriptEngineHandle.SetGlobalClrCallBack(ts.Key, 
                        (
                            (self, args) => InvokeTimeSeriesFunctionJint(ts.Key, args), 
                            (engine, isConstructCall, self, args) => InvokeTimeSeriesFunctionV8(ts.Key, args)
                        )
                    );
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public JsHandle TranslateToJs(JsonOperationContext context, object o, bool keepAlive = false)
            {
                return JsUtilsBase.TranslateToJs(context, o, keepAlive);
            }

            private JsHandle[] _args = Array.Empty<JsHandle>();

            private void SetArgs(JsonOperationContext jsonCtx, string method, object[] args)
            {
                if (_args.Length != args.Length)
                {
                    DisposeArgs();
                    _args = new JsHandle[args.Length];
                }

                for (var i = 0; i < args.Length; i++)
                {
                    _args[i] = TranslateToJs(jsonCtx, args[i], false);
                }

                if (_jsEngineType == JavaScriptEngineType.Jint)
                {
                    if (method != QueryMetadata.SelectOutput &&
                        _args.Length == 2 &&
                        _args[1].IsObject &&
                        _args[1].Object is PatchJint.BlittableObjectInstanceJint boi)
                    {
                        _refResolverJint.ExplodeArgsOn(null, boi);
                    }
                }
            }

            private void DisposeArgs()
            {
                for (int i = 0; i < _args.Length; ++i)
                {
                    _args[i].Dispose();
                }
                Array.Clear(_args, 0, _args.Length);
                
                if (_jsEngineType == JavaScriptEngineType.Jint)
                    DisposeArgsJint();
            }
            
            private static readonly TimeSeriesStorage.AppendOptions AppendOptionsForScript = new TimeSeriesStorage.AppendOptions
            {
                AddNewNameToMetadata = false
            };

            public ScriptRunnerResult Run(JsonOperationContext jsonCtx, DocumentsOperationContext docCtx, string method, object[] args, QueryTimingsScope scope = null)
            {
                return Run(jsonCtx, docCtx, method, null, args, scope);
            }

            public ScriptRunnerResult Run(JsonOperationContext jsonCtx, DocumentsOperationContext docCtx, string method, string documentId, object[] args, QueryTimingsScope scope = null)
            {
                _docsCtx = docCtx;
                _jsonCtx = jsonCtx ?? ThrowArgumentNull();
                _scope = scope;

                JsUtilsBase.Reset(_jsonCtx);

                Reset();
                OriginalDocumentId = documentId;

                SetArgs(jsonCtx, method, args);

                try
                {
                    using (var jsMethod = ScriptEngineHandle.GetGlobalProperty(method))
                    {
                        using (var jsRes = jsMethod.StaticCall(_args))
                        {
                            jsRes.ThrowOnError();
                            return new ScriptRunnerResult(this, jsRes);
                        }
                    }
                }
                catch (JavaScriptException e)
                {
                    //ScriptRunnerResult is in charge of disposing of the disposable but it is not created (the clones did)
                    JsUtilsJint.Clear();
                    throw CreateFullError(e);
                }
                catch (V8Exception e)
                {
                    //ScriptRunnerResult is in charge of disposing of the disposable but it is not created (the clones did)
                    JsUtilsV8.Clear();
                    throw CreateFullError(e);
                }
                catch (Exception)
                {
                    JsUtilsBase.Clear();
                    throw;
                }
                finally
                {
                    ScriptEngineHandle.ForceGarbageCollection();
                    DisposeArgs();
                    _scope = null;
                    _loadScope = null;
                    _docsCtx = null;
                    _jsonCtx = null;
                }
            }


            private JsHandle InvokeTimeSeriesFunction(string name, params JsHandle[] args)
            {
                AssertValidDatabaseContext("InvokeTimeSeriesFunction");

                if (_runnerBase.TimeSeriesDeclaration.TryGetValue(name, out var func) == false)
                    throw new InvalidOperationException($"Failed to invoke time series function. Unknown time series name '{name}'.");

                object[] tsFunctionArgs = GetTimeSeriesFunctionArgs(name, args, out string docId, out var lazyIds);

                var queryParams = ((Document)tsFunctionArgs[^1]).Data;

                var retriever = new TimeSeriesRetriever(_docsCtx, queryParams, null);

                var streamableResults = retriever.InvokeTimeSeriesFunction(func, docId, tsFunctionArgs, out var type);
                var result = retriever.MaterializeResults(streamableResults, type, addProjectionToResult: false, fromStudio: false);

                foreach (var id in lazyIds)
                {
                    id?.Dispose();
                }

                return TranslateToJs(_jsonCtx, result, true);
            }

            private object[] GetTimeSeriesFunctionArgs(string name, JsHandle[] args, out string docId, out List<IDisposable> lazyIds)
            {
                var tsFunctionArgs = new object[args.Length + 1];
                docId = null;

                lazyIds = new List<IDisposable>();

                for (var index = 0; index < args.Length; index++)
                {
                    if (args[index].Object is IBlittableObjectInstance boi)
                    {
                        var lazyId = _docsCtx.GetLazyString(boi.DocumentId);
                        lazyIds.Add(lazyId);
                        tsFunctionArgs[index] = new Document
                        {
                            Data = boi.Blittable,
                            Id = lazyId
                        };

                        if (index == 0)
                        {
                            // take the Id of the document to operate on
                            // from the first argument (it can be a different document than the original doc)
                            docId = boi.DocumentId;
                        }
                    }
                    else
                    {
                        tsFunctionArgs[index] = Translate(args[index], _jsonCtx);
                    }
                }

                if (docId == null)
                {
                    if (_args[0].IsObject == false ||
                        !(_args[0].Object is IBlittableObjectInstance originalDoc))
                        throw new InvalidOperationException($"Failed to invoke time series function '{name}'. Couldn't find the document ID to operate on. " +
                                                            "A Document instance argument was not provided to the time series function or to the ScriptRunner");

                    docId = originalDoc.DocumentId;
                }

                if (_args[_args.Length - 1].IsObject == false || !(_args[_args.Length - 1].Object is IBlittableObjectInstance queryParams))
                    throw new InvalidOperationException($"Failed to invoke time series function '{name}'. ScriptRunner is missing QueryParameters argument");

                tsFunctionArgs[tsFunctionArgs.Length - 1] = new Document
                {
                    Data = queryParams.Blittable
                };

                return tsFunctionArgs;
            }


            public JsHandle CreateEmptyObject()
            {
                return ScriptEngineHandle.CreateObject();
            }
            
            public object Translate(ScriptRunnerResult result, JsonOperationContext context, JsBlittableBridge.IResultModifier modifier = null, BlittableJsonDocumentBuilder.UsageMode usageMode = BlittableJsonDocumentBuilder.UsageMode.None)
            {
                return Translate(result.RawJsValue, context, modifier, usageMode);
            }

            internal object Translate(JsHandle jsValue, JsonOperationContext context, JsBlittableBridge.IResultModifier modifier = null, BlittableJsonDocumentBuilder.UsageMode usageMode = BlittableJsonDocumentBuilder.UsageMode.None, bool isRoot = true)
            {
                if (jsValue.IsStringEx)
                    return jsValue.AsString;
                if (jsValue.IsBoolean)
                    return jsValue.AsBoolean;
                if (jsValue.IsArray)
                {
                    RuntimeHelpers.EnsureSufficientExecutionStack();
                    var list = new List<object>();
                    for (int i = 0; i < jsValue.ArrayLength; i++)
                    {
                        using (var jsItem = jsValue.GetProperty(i))
                        {
                            list.Add(Translate(jsItem, context, modifier, usageMode, isRoot: false));
                        }
                    }
                    return list;
                }
                if (jsValue.IsObject)
                {
                    if (jsValue.IsNull)
                        return null;
                    return _jsEngineType switch
                    {
                        JavaScriptEngineType.Jint => PatchJint.JsBlittableBridgeJint.Translate(context, ScriptEngineJint, jsValue.Jint.Obj, modifier, usageMode, isRoot: isRoot),
                        JavaScriptEngineType.V8 => PatchV8.JsBlittableBridgeV8.Translate(context, ScriptEngineV8, jsValue.V8.Item, modifier, usageMode, isRoot: isRoot),
                        _ => throw new NotSupportedException($"Not supported JS engine kind '{_jsEngineType}'.")
                    };
                }
                if (jsValue.IsNumberOrIntEx)
                    return jsValue.AsDouble;
                if (jsValue.IsNull || jsValue.IsUndefined)
                    return null;
                throw new NotSupportedException("Unable to translate " + jsValue.ValueType);
            }
            
            public override string ToString()
            {
                return string.Join(Environment.NewLine, _runnerBase.ScriptsSource);
            }

            protected static void AssertValidId()
            {
                throw new InvalidOperationException("The first parameter to put(id, doc, changeVector) must be a string");
            }

            protected void AssertNotReadOnly()
            {
                if (ReadOnly)
                    throw new InvalidOperationException("Cannot make modifications in readonly context");
            }

            protected void AssertValidDatabaseContext(string functionName)
            {
                if (_docsCtx == null)
                    throw new InvalidOperationException($"Unable to use `{functionName}` when this instance is not attached to a database operation");
            }
            
            protected static void ThrowInvalidCounterValue()
            {
                throw new InvalidOperationException("incrementCounter(doc, name, value): 'value' must be a number argument");
            }

            protected static void ThrowInvalidCounterName(string signature)
            {
                throw new InvalidOperationException($"{signature}: 'name' must be a non-empty string argument");
            }

            protected static void ThrowInvalidDocumentArgsType(string signature)
            {
                throw new InvalidOperationException($"{signature}: 'doc' must be a string argument (the document id) or the actual document instance itself");
            }

            protected static void ThrowMissingDocument(string id)
            {
                throw new DocumentDoesNotExistException(id, "Cannot operate on counters of a missing document.");
            }

            protected static void ThrowDeleteCounterNameArg()
            {
                throw new InvalidOperationException("deleteCounter(doc, name): 'name' must be a string argument");
            }

            protected static void ThrowInvalidDeleteCounterDocumentArg()
            {
                throw new InvalidOperationException("deleteCounter(doc, name): 'doc' must be a string argument (the document id) or the actual document instance itself");
            }

            protected static void ThrowInvalidDeleteCounterArgs()
            {
                throw new InvalidOperationException("deleteCounter(doc, name) must be called with exactly 2 arguments");
            }

            protected static JsonOperationContext ThrowArgumentNull()
            {
                throw new ArgumentNullException("jsonCtx");
            }

            protected void Reset()
            {
                if (DebugMode)
                {
                    if (DebugOutput == null)
                        DebugOutput = new List<string>();
                    if (DebugActions == null)
                        DebugActions = new PatchDebugActions();
                }

                Includes?.Clear();
                IncludeRevisionsChangeVectors?.Clear();
                IncludeRevisionByDateTimeBefore = null;
                CompareExchangeValueIncludes?.Clear();
                DocumentCountersToUpdate?.Clear();
                DocumentTimeSeriesToUpdate?.Clear();
                PutOrDeleteCalled = false;
                OriginalDocumentId = null;
                RefreshOriginalDocument = false;
                
                ScriptEngineHandle.ResetCallStack();
                ScriptEngineHandle.ResetConstraints();
            }
        }

        public struct ReturnRun : IDisposable
        {
            private SingleRun _run;
            private Holder _holder;

            public ReturnRun(SingleRun run, Holder holder)
            {
                _run = run;
                _holder = holder;
            }

            public void Dispose()
            {
                if (_run == null)
                    return;

                _run.ReadOnly = false;

                _run.DebugMode = false;
                _run.DebugOutput?.Clear();
                _run.DebugActions?.Clear();
                _run.IncludeRevisionsChangeVectors?.Clear();
                _run.IncludeRevisionByDateTimeBefore = null;

                _run.Includes?.Clear();
                _run.CompareExchangeValueIncludes?.Clear();

                _run.OriginalDocumentId = null;
                _run.RefreshOriginalDocument = false;

                _run.DocumentCountersToUpdate?.Clear();
                _run.DocumentTimeSeriesToUpdate?.Clear();

                _holder.Parent._cache.Enqueue(_holder);
                _run = null;
            }
        }

        public bool RunIdleOperations()
        {
            while (_cache.TryDequeue(out var holder))
            {
                var val = holder.Value;
                if (val != null)
                {
                    // move the cache to weak reference value
                    holder.WeakValue = new WeakReference<SingleRun>(val);
                    holder.Value = null;
                    _cache.Enqueue(holder);
                    continue;
                }

                var weak = holder.WeakValue;
                if (weak == null)
                    continue;// no value, can discard it

                // The first item is a weak ref that wasn't clear?
                // The CLR can free it later, and then we'll act
                if (weak.TryGetTarget(out _))
                {
                    _cache.Enqueue(holder);
                    return true;
                }

                // the weak ref has no value, can discard it
            }

            return false;
        }
    }
}
