using System;
using System.Collections.Generic;
using Raven.Client.Documents.Operations.ETL;
using Raven.Server.Documents.ETL.Stats;
using Raven.Server.Documents.Patch;
using Raven.Server.ServerWide.Context;
using Raven.Server.Config.Categories;
using Raven.Client.ServerWide.JavaScript;
using Jint.Native;
using Raven.Server.Config.Settings;
using Raven.Server.Documents.Indexes.Static;

namespace Raven.Server.Documents.ETL
{
    public abstract partial class EtlTransformer<TExtracted, TTransformed, TStatsScope, TEtlPerformanceOperation> : IDisposable 
        where TExtracted : ExtractedItem
        where TStatsScope : AbstractEtlStatsScope<TStatsScope, TEtlPerformanceOperation>
        where TEtlPerformanceOperation : EtlPerformanceOperation
    {
        public IJsEngineHandle EngineHandle;

        protected readonly IJavaScriptOptions _jsOptions;        
        public DocumentDatabase Database { get; }
        protected readonly DocumentsOperationContext Context;
        protected readonly PatchRequest _mainScript;
        protected readonly PatchRequest _behaviorFunctions;
        protected ScriptRunner.SingleRun DocumentScript;
        protected ScriptRunner.SingleRun BehaviorsScript;

        protected TExtracted Current;

        protected ScriptRunner.ReturnRun _returnMainRun;
        protected ScriptRunner.ReturnRun _behaviorFunctionsRun;

        protected EtlTransformer(DocumentDatabase database, DocumentsOperationContext context,
            PatchRequest mainScript, PatchRequest behaviorFunctions)
        {
            Database = database;
            Context = context;
            _mainScript = mainScript;
            _behaviorFunctions = behaviorFunctions;
            _jsOptions = Database?.JsOptions ?? Context?.DocumentDatabase?.JsOptions ?? 
                new JavaScriptOptions(JavaScriptEngineType.Jint, true, 10000, new TimeSetting(100, TimeUnit.Milliseconds));
        }

        public virtual void Initialize(bool debugMode)
        {
            if (_behaviorFunctions != null)
            {
                _behaviorFunctionsRun = Database.Scripts.GetScriptRunner(_jsOptions, _behaviorFunctions, true, out BehaviorsScript);

                if (debugMode)
                    BehaviorsScript.DebugMode = true;
            }
            
            _returnMainRun = Database.Scripts.GetScriptRunner(_jsOptions, _mainScript, true, out DocumentScript);
            if (DocumentScript == null)
                return;

            if (debugMode)
                DocumentScript.DebugMode = true;
            
            EngineHandle = DocumentScript.ScriptEngineHandle;

            var jsEngineType = _jsOptions.EngineType;
            switch (jsEngineType)
            {
                case JavaScriptEngineType.Jint:
                    InitializeJint();
                    break;
                case JavaScriptEngineType.V8:
                    InitializeV8();
                    break;
                default:
                    throw new NotSupportedException($"Not supported JS engine type '{jsEngineType}'.");
            }

            EngineHandle.SetGlobalClrCallBack(Transformation.LoadTo, (LoadToFunctionTranslatorJint, LoadToFunctionTranslatorV8));

            foreach (var collection in LoadToDestinations)
            {
                var name = Transformation.LoadTo + collection;
                EngineHandle.SetGlobalClrCallBack(name, 
                    ((Func<JsValue, JsValue[], JsValue>)((value, values) => LoadToFunctionTranslatorInnerJint(collection, value, values)),
                    (engine, isConstructCall, self, args) => LoadToFunctionTranslatorInnerV8(collection, self, args))
                );
            }
            
            EngineHandle.SetGlobalClrCallBack(Transformation.LoadAttachment, (LoadAttachmentJint, LoadAttachmentV8));

            const string loadCounter = Transformation.CountersTransformation.Load;
            EngineHandle.SetGlobalClrCallBack(loadCounter, (LoadCounterJint, LoadCounterV8));

            const string loadTimeSeries = Transformation.TimeSeriesTransformation.LoadTimeSeries.Name;
            EngineHandle.SetGlobalClrCallBack(loadTimeSeries, (LoadTimeSeriesJint, LoadTimeSeriesV8));

            EngineHandle.SetGlobalClrCallBack("getAttachments", (GetAttachmentsJint, GetAttachmentsV8));

            EngineHandle.SetGlobalClrCallBack("hasAttransfochment", (HasAttachmentJint, HasAttachmentV8));

            EngineHandle.SetGlobalClrCallBack("getCounters", (GetCountersJint, GetCountersV8));

            EngineHandle.SetGlobalClrCallBack("hasCounter", (HasCounterJint, HasCounterV8));
            
            const string hasTimeSeries = Transformation.TimeSeriesTransformation.HasTimeSeries.Name;
            EngineHandle.SetGlobalClrCallBack(hasTimeSeries, (HasTimeSeriesJint, HasTimeSeriesV8));
            
            const string getTimeSeries = Transformation.TimeSeriesTransformation.GetTimeSeries.Name;
            EngineHandle.SetGlobalClrCallBack(getTimeSeries, (GetTimeSeriesJint, GetTimeSeriesV8));
        }
        
        protected abstract string[] LoadToDestinations { get; }

        protected abstract void LoadToFunction(string tableName, ScriptRunnerResult colsAsObject);

        public abstract IEnumerable<TTransformed> GetTransformedResults();

        public abstract void Transform(TExtracted item, TStatsScope stats, EtlProcessState state);

        public static void ThrowLoadParameterIsMandatory(string parameterName)
        {
            throw new ArgumentException($"{parameterName} parameter is mandatory");
        }

        protected static void ThrowInvalidScriptMethodCall(string message)
        {
            throw new InvalidOperationException(message);
        }

        public virtual void Dispose()
        {
            using (_returnMainRun)
            using (_behaviorFunctionsRun)
            {

            }
        }

        public List<string> GetDebugOutput()
        {
            var outputs = new List<string>();

            if (DocumentScript?.DebugOutput != null)
                outputs.AddRange(DocumentScript.DebugOutput);

            if (BehaviorsScript?.DebugOutput != null)
                outputs.AddRange(BehaviorsScript.DebugOutput);

            return outputs;
        }
    }
}
