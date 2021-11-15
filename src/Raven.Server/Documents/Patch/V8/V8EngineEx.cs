#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using V8.Net;
using Raven.Client.ServerWide.JavaScript;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.Static;
using Raven.Server.Documents.Indexes.Static.JavaScript.V8;
using Raven.Server.Documents.Indexes.Static.Counters.V8;
using Raven.Server.Documents.Indexes.Static.TimeSeries;
using Raven.Server.Documents.Indexes.Static.TimeSeries.V8;
using Sparrow;
using Sparrow.Json;
using Raven.Client.Util;
using Jint.Native;
using Raven.Client.Exceptions.Documents.Patching;
using Raven.Server.Config.Settings;

namespace Raven.Server.Documents.Patch.V8
{
    public class V8EngineEx : V8Engine, IJsEngineHandle
    {
        
        public class JsConverter : IJsConverter
        {
            public InternalHandle ConvertToJs(V8Engine engine, object obj, bool keepAlive = false)
            {
                return obj switch 
                {
                    LazyNumberValue lnv => engine.CreateValue(lnv.ToDouble(CultureInfo.InvariantCulture)),
                    StringSegment ss => engine.CreateValue(ss.ToString()),
                    LazyStringValue lsv => engine.CreateValue(lsv.ToString()),
                    LazyCompressedStringValue lcsv => engine.CreateValue(lcsv.ToString()),
                    Guid guid => engine.CreateValue(guid.ToString()),
                    TimeSpan timeSpan => engine.CreateValue(timeSpan.ToString()),
                    DateTime dateTime => engine.CreateValue(dateTime.ToString(DefaultFormat.DateTimeOffsetFormatsToWrite)),
                    DateTimeOffset dateTimeOffset => engine.CreateValue(dateTimeOffset.ToString(DefaultFormat.DateTimeOffsetFormatsToWrite)),
                    _ => InternalHandle.Empty
                };
            }
        }
        
// env object helps to distinguish between execution environments like 'RavendDB' and 'Node.js' and engines like 'V8' and 'Jint'.
// Node.js can be used both for testing and alternative execution (with modified logic).
        public const string ExecEnvCodeV8 = @"
var process = {
    env: {
        EXEC_ENV: 'RavenDB',
        ENGINE: 'V8'
    }
}
";

        public DynamicJsNullV8 ImplicitNullV8;
        public DynamicJsNullV8 ExplicitNullV8;

        private readonly JsHandle _jsonStringify;
        public JsHandle JsonStringify => _jsonStringify;
        
        public InternalHandle JsonStringifyV8;

        public static void DisposeJsObjectsIfNeeded(object value)
        {
            if (value is InternalHandle jsValue)
            {
                jsValue.Dispose();
            }
            else if (value is object[] arr)
            {
                for(int i=0; i < arr.Length; i++)
                {
                    if (arr[i] is InternalHandle jsItem)
                        jsItem.Dispose();       
                }
            }
        }
        
        public static void DisposeAndCollectGarbage(List<object> items, string? snapshotName)
        {
            V8Engine engine = null;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var h = (InternalHandle)items[i];
                if (engine == null)
                    engine = h.Engine;
                h.Dispose();
            }

            engine?.ForceV8GarbageCollection();
            if (snapshotName != null)
                engine?.CheckForMemoryLeaks(snapshotName);

        }
        
        public void SetBasicConfiguration()
        {
            //.LocalTimeZone(TimeZoneInfo.Utc);  // TODO -> ??? maybe these V8 args: harmony_intl_locale_info, harmony_intl_more_timezone
        }

        public void SetOptions(IJavaScriptOptions jsOptions)
        {
            SetBasicConfiguration();
            if (jsOptions == null)
                return;
            string strictModeFlag = jsOptions.StrictMode ? "--use_strict" : "--no-use_strict";
            string[] optionsCmd = {strictModeFlag};
            SetFlagsFromCommandLine(optionsCmd);
            SetMaxDuration((int)jsOptions.MaxDuration.GetValue(TimeUnit.Milliseconds));
        }

        // ------------------------------------------ IJavaScriptEngineHandle implementation
        public JavaScriptEngineType EngineType => JavaScriptEngineType.V8;

        public IDisposable DisableConstraints()
        {
            return DisableMaxDuration();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ForceGarbageCollection()
        {
            ForceV8GarbageCollection();
        }

        public void TryCompileScript(string script)
        {
            try
            {
                using (var jsComiledScript = Compile(script, "script", true))
                {}
            }
            catch (Exception e)
            {
                throw new JavaScriptParseException("Failed to parse:" + Environment.NewLine + script, e);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(string source, string sourceName = "anonymousCode.js", bool throwExceptionOnError = true)
        {
            this.Execute(source, sourceName, throwExceptionOnError);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExecuteWithReset(string source, string sourceName = "anonymousCode.js", bool throwExceptionOnError = true)
        {
            ((V8Engine)this).ExecuteWithReset(source, sourceName, throwExceptionOnError);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsHandle GetGlobalProperty(string propertyName)
        {
            return new JsHandle(GlobalObject.GetProperty(propertyName));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetGlobalProperty(string propertyName, JsHandle value)
        {
            GlobalObject.SetProperty(propertyName, value.V8);
        }

        public JsHandle FromObjectGen(object obj, bool keepAlive = false)
        {
            return new JsHandle(FromObject(obj, keepAlive));
        }

        public JsHandle CreateClrCallBack(string propertyName, (Func<JsValue, JsValue[], JsValue> Jint, JSFunction V8) funcTuple, bool keepAlive = true)
        {
            return new JsHandle(CreateClrCallBack(funcTuple.V8, keepAlive));
        }

        public void SetGlobalClrCallBack(string propertyName, (Func<JsValue, JsValue[], JsValue> Jint, JSFunction V8) funcTuple)
        {
            SetGlobalClrCallBack(propertyName, funcTuple.V8);
        }

        public new JsHandle CreateObject()
        {
            return new JsHandle(base.CreateObject());
        }

        public new JsHandle CreateEmptyArray()
        {
            return new JsHandle(base.CreateEmptyArray());
        }
        
        public new JsHandle CreateArray(System.Array items)
        {
            return new JsHandle(base.CreateArray(items));
        }
        
        public new JsHandle CreateArray(IEnumerable<object> items)
        {
            return new JsHandle(base.CreateArray(items));
        }

        public JsHandle CreateUndefinedValue()
        {
            return new JsHandle(JavaScriptEngineType.V8);
        }
        
        public new JsHandle CreateNullValue()
        {
            return new JsHandle(base.CreateNullValue());
        }
        
        public new JsHandle CreateValue(bool value)
        {
            return new JsHandle(base.CreateValue(value));
        }

        public new JsHandle CreateValue(Int32 value)
        {
            return new JsHandle(base.CreateValue(value));
        }

        public new JsHandle CreateValue(double value)
        {
            return new JsHandle(base.CreateValue(value));
        }

        public new JsHandle CreateValue(string value)
        {
            return new JsHandle(base.CreateValue(value));
        }

        public new JsHandle CreateValue(TimeSpan ms)
        {
            return new JsHandle(base.CreateValue(ms));
        }

        public new JsHandle CreateValue(DateTime value)
        {
            return new JsHandle(base.CreateValue(value));
        }

        public new JsHandle CreateError(string message, JSValueType errorType)
        {
            return new JsHandle(base.CreateError(message, errorType));
        }

        // ------------------------------------------ internal implementation
        public readonly TypeBinder TypeBinderBlittableObjectInstance;
        public readonly TypeBinder TypeBinderTask;
        public readonly TypeBinder TypeBinderTimeSeriesSegmentObjectInstance;
        public readonly TypeBinder TypeBinderDynamicTimeSeriesEntries;
        public readonly TypeBinder TypeBinderDynamicTimeSeriesEntry;
        public readonly TypeBinder TypeBinderCounterEntryObjectInstance;
        public readonly TypeBinder TypeBinderAttachmentNameObjectInstance;
        public readonly TypeBinder TypeBinderAttachmentObjectInstance;
        public readonly TypeBinder TypeBinderLazyNumberValue;

        public V8EngineEx(IJavaScriptOptions jsOptions = null, bool autoCreateGlobalContext = true) : base(autoCreateGlobalContext, jsConverter: new JsConverter())
        {
            SetOptions(jsOptions);
            
            ImplicitNullV8 = new DynamicJsNullV8(this, isExplicitNull: false);
            ExplicitNullV8 = new DynamicJsNullV8(this, isExplicitNull: true);

            ExecuteWithReset(ExecEnvCodeV8, "ExecEnvCode");

            TypeBinderBlittableObjectInstance = RegisterType<BlittableObjectInstanceV8>(null, true);
            TypeBinderBlittableObjectInstance.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<BlittableObjectInstanceV8.CustomBinder, BlittableObjectInstanceV8>((BlittableObjectInstanceV8)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(BlittableObjectInstanceV8));

            TypeBinderTask = RegisterType<Task>(null, true, ScriptMemberSecurity.ReadWrite);
            TypeBinderTask.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<TaskCustomBinder, Task>((Task)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(Task));


            TypeBinderTimeSeriesSegmentObjectInstance = RegisterType<TimeSeriesSegmentObjectInstanceV8>(null, false);
            TypeBinderTimeSeriesSegmentObjectInstance.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<TimeSeriesSegmentObjectInstanceV8.CustomBinder, TimeSeriesSegmentObjectInstanceV8>((TimeSeriesSegmentObjectInstanceV8)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(TimeSeriesSegmentObjectInstanceV8));

            TypeBinderDynamicTimeSeriesEntries = RegisterType<DynamicArray>(null, false);
            TypeBinderDynamicTimeSeriesEntries.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<DynamicTimeSeriesEntriesCustomBinder, DynamicArray>((DynamicArray)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(DynamicArray));

            TypeBinderDynamicTimeSeriesEntry = RegisterType<DynamicTimeSeriesSegment.DynamicTimeSeriesEntry>(null, false);
            TypeBinderDynamicTimeSeriesEntry.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<DynamicTimeSeriesEntryCustomBinder, DynamicTimeSeriesSegment.DynamicTimeSeriesEntry>((DynamicTimeSeriesSegment.DynamicTimeSeriesEntry)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(DynamicTimeSeriesSegment.DynamicTimeSeriesEntry));


            TypeBinderCounterEntryObjectInstance = RegisterType<CounterEntryObjectInstanceV8>(null, false);
            TypeBinderCounterEntryObjectInstance.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<CounterEntryObjectInstanceV8.CustomBinder, CounterEntryObjectInstanceV8>((CounterEntryObjectInstanceV8)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(CounterEntryObjectInstanceV8));

            TypeBinderAttachmentNameObjectInstance = RegisterType<AttachmentNameObjectInstanceV8>(null, false);
            TypeBinderAttachmentNameObjectInstance.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<AttachmentNameObjectInstanceV8.CustomBinder, AttachmentNameObjectInstanceV8>((AttachmentNameObjectInstanceV8)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(AttachmentNameObjectInstanceV8));

            TypeBinderAttachmentObjectInstance = RegisterType<AttachmentObjectInstanceV8>(null, false);
            TypeBinderAttachmentObjectInstance.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<AttachmentObjectInstanceV8.CustomBinder, AttachmentObjectInstanceV8>((AttachmentObjectInstanceV8)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(AttachmentObjectInstanceV8));

            TypeBinderLazyNumberValue = RegisterType<LazyNumberValue>(null, false);
            TypeBinderLazyNumberValue.OnGetObjectBinder = (tb, obj, initializeBinder)
                => tb.CreateObjectBinder<ObjectBinder, LazyNumberValue>((LazyNumberValue)obj, initializeBinder, keepAlive: true);
            GlobalObject.SetProperty(typeof(LazyNumberValue));

            JsonStringifyV8 = this.Execute("JSON.stringify", "JSON.stringify", true, 0);
            _jsonStringify = new JsHandle(JsonStringifyV8);
        }

        public override void Dispose() 
        {
            JsonStringifyV8.Dispose();
            base.Dispose();
        }

        public IDisposable ChangeMaxStatements(int maxDurationNew)
        {
            // doing nothing as V8 doesn't support limiting MaxStatements
            
            void RestoreMaxStatements()
            {
            }
            return new DisposableAction(RestoreMaxStatements);
            
        }
    }
}
