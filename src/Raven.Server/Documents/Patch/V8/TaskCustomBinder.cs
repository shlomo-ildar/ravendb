using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using V8.Net;


namespace Raven.Server.Documents.Patch.V8
{

    public class TaskCustomBinder : ObjectBinderEx<Task>
    {
        public static InternalHandle CreateObjectBinder(V8EngineEx engine, Task oi, bool keepAlive = false) 
        {
            return engine.CreateObjectBinder<TaskCustomBinder>(oi, engine.Context.TypeBinderTask(), keepAlive: keepAlive);
        }

        public static InternalHandle GetRunningTaskResult(V8Engine engine, Task task)
        {
            try
            {
                var value = $"{{Ignoring Task.Result as task's status is {task.Status.ToString()}}}.";
                if (task.IsFaulted)
                    value += Environment.NewLine + "Exception: " + task.Exception;
                return engine.CreateValue(value);
            }
            catch (Exception e)
            {
                var engineEx = (V8EngineEx)engine;
                engineEx.Context.JsContext.LastException = e;
                return engine.CreateError(e.ToString(), JSValueType.ExecutionError);
            }
        }

        public override InternalHandle NamedPropertyGetter(ref string propertyName)
        {
            try
            {
                if (ObjClr.IsCompleted == false && propertyName == nameof(Task<int>.Result))
                {
                    return GetRunningTaskResult(Engine, ObjClr);
                }
                return base.NamedPropertyGetter(ref propertyName);
            }
            catch (Exception e)
            {
                var engineEx = (V8EngineEx)Engine;
                engineEx.Context.JsContext.LastException = e;
                return Engine.CreateError(e.ToString(), JSValueType.ExecutionError);
            }
        }

    }
}
