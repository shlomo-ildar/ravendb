using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using Jint;
using Jint.Native;
using Raven.Server.Extensions.Jint;
using Raven.Server.Documents.Indexes.Static.JavaScript.Jint;
using Raven.Client.ServerWide.JavaScript;
using Raven.Server.Config.Categories;
using JSFunction = V8.Net.JSFunction;
using JSValueType = V8.Net.JSValueType;
using Raven.Client.Exceptions.Documents.Patching;
using Jint.Constraints;
using Raven.Client.Util;

namespace Raven.Server.Documents.Patch.Jint
{
    public class JintEngineEx : Engine, IJsEngineHandle
    {

        public const string ExecEnvCodeJint = @"
var process = {
    env: {
        EXEC_ENV: 'RavenDB',
        ENGINE: 'Jint'
    }
}
";
        public readonly JintPreventResolvingTasksReferenceResolver RefResolver;
        
        public DynamicJsNullJint ImplicitNullJint;
        public DynamicJsNullJint ExplicitNullJint;

        private readonly JsHandle _jsonStringify;
        public JsHandle JsonStringify => _jsonStringify;

        public JintEngineEx(IJavaScriptOptions jsOptions = null, JintPreventResolvingTasksReferenceResolver refResolver = null) : base(options =>
        {            
            if (jsOptions == null)
                options.MaxStatements(1).LimitRecursion(1);
            else
                options.LimitRecursion(64)
                    .SetReferencesResolver(refResolver)
                    .Strict(jsOptions.StrictMode)
                    .MaxStatements(jsOptions.MaxSteps)
                    //.TimeoutInterval(TimeSpan.FromMilliseconds(jsConfiguration.MaxDuration)) // TODO In Jint TimeConstraint2 is the internal class so the approach applied to MaxStatements doesn't work here
                    .AddObjectConverter(new JintGuidConverter())
                    .AddObjectConverter(new JintStringConverter())
                    .AddObjectConverter(new JintEnumConverter())
                    .AddObjectConverter(new JintDateTimeConverter())
                    .AddObjectConverter(new JintTimeSpanConverter())
                    .LocalTimeZone(TimeZoneInfo.Utc);
        })
        {
            RefResolver = refResolver;

            ExecuteWithReset(ExecEnvCodeJint, "ExecEnvCode");

            _jsonStringify = new JsHandle(GetValue("JSON").AsObject().GetProperty("Stringify").Value);
        }

        ~JintEngineEx() 
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void Dispose(bool disposing)
        {
            _jsonStringify.Dispose();
        }

        public void SetBasicConfiguration()
        {
            //.LocalTimeZone(TimeZoneInfo.Utc);  // TODO -> ??? maybe these V8 args: harmony_intl_locale_info, harmony_intl_more_timezone
        }

        // ------------------------------------------ IJavaScriptEngineHandle implementation
        public JavaScriptEngineType EngineType => JavaScriptEngineType.Jint;

        public IDisposable DisableConstraints()
        {
            var disposeMaxStatements = ChangeMaxStatements(0);
            var disposeMaxDuration = ChangeMaxDuration(0);

            void Restore()
            {
                disposeMaxStatements.Dispose();
                disposeMaxDuration.Dispose();
            }
            
            return new DisposableAction(Restore);
        }

        public IDisposable ChangeMaxStatements(int value)
        {
            var maxStatements = FindConstraint<MaxStatements>();
            if (maxStatements == null)
                return null;

            var oldMaxStatements = maxStatements.Max;
            maxStatements.Change(value == 0 ? int.MaxValue : value);

            return new DisposableAction(() => maxStatements.Change(oldMaxStatements));
        }

        public IDisposable ChangeMaxDuration(int value)
        {
            var maxDuration = FindConstraint<MaxStatements>(); // TODO [shlomo] to expose in Jint TimeConstraint2 that is now internal, add Change method to it and replace MaxStatements to TimeConstraint2
            if (maxDuration == null)
                return null;

            var oldMaxDuration = maxDuration.Max;
            maxDuration.Change(value == 0 ? int.MaxValue : value); // TODO [shlomo] to replace on switching to TimeConstraint2: TimeSpan.FromMilliseconds(value == 0 ? int.MaxValue : value));

            return new DisposableAction(() => maxDuration.Change(oldMaxDuration));
        }
        
        public void ForceGarbageCollection()
        {}

        public void MakeSnapshot(string name)
        {}
        
        public void CheckForMemoryLeaks(string name)
        {}

        public void TryCompileScript(string script)
        {
            try
            {
                Execute(script);
            }
            catch (Exception e)
            {
                throw new JavaScriptParseException("Failed to parse:" + Environment.NewLine + script, e);
            }
            
        }

        public void Execute(string source, string sourceName = "anonymousCode.js", bool throwExceptionOnError = true)
        {
            if (throwExceptionOnError)
                this.Execute(source);
            else
            {
                try
                {
                    this.Execute(source);
                }
                catch
                {
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExecuteWithReset(string source, string sourceName = "anonymousCode.js", bool throwExceptionOnError = true)
        {
            this.ExecuteWithReset(source, throwExceptionOnError);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public JsHandle GetGlobalProperty(string propertyName)
        {
            return new JsHandle(GetValue(propertyName));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetGlobalProperty(string name, JsHandle value)
        {
            SetValue(name, value.Jint);
        }

        public JsHandle FromObjectGen(object obj, bool keepAlive = false)
        {
            return new JsHandle(JsValue.FromObject(this, obj));
        }

        public JsHandle CreateClrCallBack(string propertyName, (Func<JsValue, JsValue[], JsValue> Jint, JSFunction V8) funcTuple, bool keepAlive = true)
        {
            return new JsHandle(this.CreateClrCallBack(propertyName, funcTuple.Jint));
        }

        public void SetGlobalClrCallBack(string propertyName, (Func<JsValue, JsValue[], JsValue> Jint, JSFunction V8) funcTuple)
        {
            this.SetGlobalClrCallBack(propertyName, funcTuple.Jint);
        }

        public JsHandle CreateObject()
        {
            return new JsHandle(Object.Construct(System.Array.Empty<JsValue>())); //new ObjectInstance(this));
        }

        public JsHandle CreateEmptyArray()
        {
            var be = (Engine)this;
            return new JsHandle(be.CreateEmptyArray());
        }
        
        public JsHandle CreateArray(System.Array items)
        {
            int arrayLength = items.Length;
            var jsItems = new JsValue[arrayLength];
            for (int i = 0; i < arrayLength; ++i)
            {
                jsItems[i] = this.FromObject(items.GetValue(i));
            }
            return new JsHandle(this.CreateArrayWithDisposal(jsItems));
        }

        public JsHandle CreateArray(IEnumerable<object> items)
        {
            var be = (Engine)this;
            var list = be.CreateEmptyArray();
            void PushKey(object value)
            {
                var jsValue = be.FromObject(value);
                list.AsObject().StaticCall("push", jsValue);
            }

            foreach (var item in items)
                PushKey(item);
            return new JsHandle(list);
        }

        public JsHandle CreateUndefinedValue()
        {
            return new JsHandle(JsValue.Undefined);
        }
        
        public JsHandle CreateNullValue()
        {
            return new JsHandle(JsValue.Null);
        }
        
        public JsHandle CreateValue(bool value)
        {
            return new JsHandle(new JsBoolean(value));
        }

        public JsHandle CreateValue(Int32 value)
        {
            return new JsHandle(new JsNumber(value));
        }

        public JsHandle CreateValue(double value)
        {
            return new JsHandle(new JsNumber(value));
        }

        public JsHandle CreateValue(string value)
        {
            return new JsHandle(new JsString(value));
        }

        public JsHandle CreateValue(TimeSpan ms)
        {
            var be = (Engine)this;
            return new JsHandle(be.FromObject(ms));
        }

        public JsHandle CreateValue(DateTime value)
        {
            var be = (Engine)this;
            return new JsHandle(be.FromObject(value));
        }

        public JsHandle CreateError(string message, JSValueType errorType)
        {
            return new JsHandle(message, errorType);
        }
    }
}
