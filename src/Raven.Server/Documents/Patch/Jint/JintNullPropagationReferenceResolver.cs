using System;
using Jint;
using Jint.Native;
using Jint.Runtime.Interop;
using Jint.Runtime.References;
using Raven.Server.Documents.Indexes.Static.JavaScript.Jint;
using Raven.Client;

namespace Raven.Server.Documents.Patch.Jint
{
    public abstract class JintNullPropagationReferenceResolver : IReferenceResolver
    {
        protected JsValue _selfInstance;
        protected BlittableObjectInstanceJint _args;

        public virtual bool TryUnresolvableReference(Engine engine, Reference reference, out JsValue value)
        {
            var name = reference.GetReferencedName()?.AsString();
            if (_args == null || name == null || name.StartsWith('$') == false)
            {
                value = name == "length" ? 0 : Null.Instance;
                return true;
            }

            value = _args.Get(name.Substring(1));
            return true;
        }

        public virtual bool TryPropertyReference(Engine engine, Reference reference, ref JsValue value)
        {
            if (reference.GetReferencedName() == Constants.Documents.Metadata.Key && 
                reference.GetBase() is BlittableObjectInstanceJint boi)
            {
                value = engine.Invoke(ScriptRunner.SingleRun.GetMetadataMethod, boi);
                return true;
            }
            
            // [shlomo] fixed test FilteredMinAndMaxProjectionAgainstEmptyCollection by switching off reduce processing as it seemed to cause searching reduce property on null value
            /*if (reference.GetReferencedName() == "reduce" &&
                value.IsArray() && value.AsArray().Length == 0)
            {
                value = Null.Instance;
                return true;
            }*/

            if (value is DynamicJsNullJint)
            {
                value = DynamicJsNullJint.ImplicitNullJint;
                return true;
            }

            return value.IsNull() || value.IsUndefined();
        }

        public bool TryGetCallable(Engine engine, object callee, out JsValue value)
        {
            if (callee is Reference reference)
            {
                var baseValue = reference.GetBase();

                if (baseValue.IsUndefined() ||
                    baseValue.IsArray() && baseValue.AsArray().Length == 0)
                {
                    var name = reference.GetReferencedName().AsString();
                    switch (name)
                    {
                        case "reduce":
                            value = new ClrFunctionInstance(engine, "reduce", (thisObj, values) => values.Length > 1 ? values[1] : JsValue.Null);
                            return true;
                        case "concat":
                            value = new ClrFunctionInstance(engine, "concat", (thisObj, values) => values[0]);
                            return true;
                        case "some":
                        case "includes":
                            value = new ClrFunctionInstance(engine, "some", (thisObj, values) => false);
                            return true;
                        case "every":
                            value = new ClrFunctionInstance(engine, "every", (thisObj, values) => true);
                            return true;
                        case "map":
                        case "filter":
                        case "reverse":
                            value = new ClrFunctionInstance(engine, "map", (thisObj, values) => engine.Realm.Intrinsics.Array.Construct(Array.Empty<JsValue>()));
                            return true;
                    }
                }
            }

            // fixed for compatibility the case of calling non-existing method by disabling TryGetCallable (test SmallLogTransformerTest)
            // (as old Jint version hasn't actually called it, when the new one has started to call it and it stopped throwing as before)
            value = JsValue.Undefined;
            return false;
            //value = new ClrFunctionInstance(engine, "function", (thisObj, values) => thisObj);
            //return true;
        }

        public bool CheckCoercible(JsValue value)
        {
            return true;
        }
    }
}
