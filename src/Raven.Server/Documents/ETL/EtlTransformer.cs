﻿using System;
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
        public IJsEngineHandle BehaviorsEngineHandle;
        public IJsEngineHandle DocumentEngineHandle;

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
                _behaviorFunctionsRun = Database.Scripts.GetScriptRunner(_jsOptions, _behaviorFunctions, true, out BehaviorsScript, executeScriptsSource: false);

                if (debugMode)
                    BehaviorsScript.DebugMode = true;
            }
            
            _returnMainRun = Database.Scripts.GetScriptRunner(_jsOptions, _mainScript, true, out DocumentScript, executeScriptsSource: false);
            if (DocumentScript == null)
                return;

            if (debugMode)
                DocumentScript.DebugMode = true;
            
            BehaviorsEngineHandle = BehaviorsScript.ScriptEngineHandle;
            DocumentEngineHandle = DocumentScript.ScriptEngineHandle;

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

            DocumentEngineHandle.SetGlobalClrCallBack(Transformation.LoadTo, (LoadToFunctionTranslatorJint, LoadToFunctionTranslatorV8));

            foreach (var collection in LoadToDestinations)
            {
                var name = Transformation.LoadTo + collection;
                DocumentEngineHandle.SetGlobalClrCallBack(name, 
                    ((Func<JsValue, JsValue[], JsValue>)((value, values) => LoadToFunctionTranslatorInnerJint(collection, value, values)),
                    (engine, isConstructCall, self, args) => LoadToFunctionTranslatorInnerV8(collection, self, args))
                );
                BehaviorsEngineHandle.SetGlobalClrCallBack(name, (ReturnSelfJint, ReturnSelfV8));
            }
            
            const string loadAttachment = Transformation.LoadAttachment;
            DocumentEngineHandle.SetGlobalClrCallBack(loadAttachment, (LoadAttachmentJint, LoadAttachmentV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(loadAttachment, (ReturnSelfJint, ReturnSelfV8));

            const string loadCounter = Transformation.CountersTransformation.Load;
            DocumentEngineHandle.SetGlobalClrCallBack(loadCounter, (LoadCounterJint, LoadCounterV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(loadCounter, (ReturnSelfJint, ReturnSelfV8));

            const string loadTimeSeries = Transformation.TimeSeriesTransformation.LoadTimeSeries.Name;
            DocumentEngineHandle.SetGlobalClrCallBack(loadTimeSeries, (LoadTimeSeriesJint, LoadTimeSeriesV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(loadTimeSeries, (ReturnSelfJint, ReturnSelfV8));

            const string getAttachments = "getAttachments";
            DocumentEngineHandle.SetGlobalClrCallBack(getAttachments, (GetAttachmentsJint, GetAttachmentsV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(getAttachments, (ReturnSelfJint, ReturnSelfV8));

            const string hasAttachment = "hasAttachment";
            DocumentEngineHandle.SetGlobalClrCallBack(hasAttachment, (HasAttachmentJint, HasAttachmentV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(hasAttachment, (ReturnSelfJint, ReturnSelfV8));

            const string getCounters = "getCounters";
            DocumentEngineHandle.SetGlobalClrCallBack(getCounters, (GetCountersJint, GetCountersV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(getCounters, (ReturnSelfJint, ReturnSelfV8));

            const string hasCounter = "hasCounter";
            DocumentEngineHandle.SetGlobalClrCallBack(hasCounter, (HasCounterJint, HasCounterV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(hasCounter, (ReturnSelfJint, ReturnSelfV8));
            
            const string hasTimeSeries = Transformation.TimeSeriesTransformation.HasTimeSeries.Name;
            DocumentEngineHandle.SetGlobalClrCallBack(hasTimeSeries, (HasTimeSeriesJint, HasTimeSeriesV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(hasTimeSeries, (ReturnSelfJint, ReturnSelfV8));
            
            const string getTimeSeries = Transformation.TimeSeriesTransformation.GetTimeSeries.Name;
            DocumentEngineHandle.SetGlobalClrCallBack(getTimeSeries, (GetTimeSeriesJint, GetTimeSeriesV8));
            BehaviorsEngineHandle.SetGlobalClrCallBack(getTimeSeries, (ReturnSelfJint, ReturnSelfV8));
            
            DocumentScript.ExecuteScriptsSource();
            BehaviorsScript.ExecuteScriptsSource();
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
