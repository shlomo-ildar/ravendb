using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Raven.Client.ServerWide.JavaScript;
using Jint.Native;
using Raven.Server.Config.Categories;
using JSFunction = V8.Net.JSFunction;
using JSValueType = V8.Net.JSValueType;

namespace Raven.Server.Documents.Patch
{
    public interface IJavaScriptEngineForParsing
    {
        JsHandle GlobalObject { get; }
        
        JsHandle GetGlobalProperty(string propertyName);

        void SetGlobalProperty(string name, JsHandle value);
        
        void Execute(string source, string sourceName = "anonymousCode.js", bool throwExceptionOnError = true);

        void ExecuteWithReset(string source, string sourceName = "anonymousCode.js", bool throwExceptionOnError = true);

        IDisposable DisableConstraints();
    }
    
    //public delegate JsValue JintFunction(JsValue self, JsValue[] args); // TODO [shlomo] to discuss with Pawel moving and using it inside Jint

    public interface IJsEngineHandle : IJavaScriptEngineForParsing, IDisposable
    {

        IDisposable WriteLock { get;  }
        
        void SetContext()
        
        JavaScriptEngineType EngineType { get;  }
            
        [CanBeNull]
        IJavaScriptOptions JsOptions { get;  }
        
        JsHandle ImplicitNull { get;  }
        
        JsHandle ExplicitNull { get;  }
        
        JsHandle JsonStringify { get;  }
        
        void ForceGarbageCollection();

        void MakeSnapshot(string name);

        void CheckForMemoryLeaks(string name);
        
        void TryCompileScript(string script);

        IDisposable ChangeMaxStatements(int value);

        IDisposable ChangeMaxDuration(int value);

        void ResetCallStack();

        void ResetConstraints();

        JsHandle FromObjectGen(object obj, bool keepAlive = false);

        JsHandle CreateClrCallBack(string propertyName, (Func<JsValue, JsValue[], JsValue> Jint, JSFunction V8) funcTuple, bool keepAlive = true);

        void SetGlobalClrCallBack(string propertyName, (Func<JsValue, JsValue[], JsValue> Jint, JSFunction V8) funcTuple);
        
        JsHandle CreateObject();
        
        JsHandle CreateEmptyArray();

        JsHandle CreateArray(JsHandle[] items);
        
        JsHandle CreateArray(System.Array items);
        
        JsHandle CreateArray(IEnumerable<object> items);

        JsHandle CreateUndefinedValue();

        JsHandle CreateNullValue();

        JsHandle CreateValue(bool value);

        JsHandle CreateValue(Int32 value);

        JsHandle CreateValue(double value);

        JsHandle CreateValue(string value);

        JsHandle CreateValue(TimeSpan ms);

        JsHandle CreateValue(DateTime value);

        JsHandle CreateError(string message, JSValueType errorType);
    }
}
