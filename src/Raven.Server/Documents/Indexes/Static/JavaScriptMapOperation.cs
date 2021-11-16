using System;
using System.Collections.Generic;
using System.Linq;
using Esprima.Ast;
using Jint;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Runtime.Environments;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide.JavaScript;
using Raven.Server.Extensions.Jint;
using Raven.Server.Documents.Indexes.Static.Utils;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.Patch.Jint;
using JintPreventResolvingTasksReferenceResolver = Raven.Server.Documents.Patch.Jint.JintPreventResolvingTasksReferenceResolver;
using V8Exception = V8.Net.V8Exception;
using JavaScriptException = Jint.Runtime.JavaScriptException;

namespace Raven.Server.Documents.Indexes.Static
{
    public class JavaScriptMapOperation
    {
        private readonly JavaScriptIndexUtils _jsIndexUtils;
        private readonly IJsEngineHandle _engineHandle;
        private IJavaScriptEngineForParsing EngineForParsing { get; }
        protected readonly Engine _engineStaticJint;

        public FunctionInstance MapFuncJint;
        public JsHandle MapFunc;

        private readonly JintPreventResolvingTasksReferenceResolver _resolver;

        public bool HasDynamicReturns;

        public bool HasBoostedFields;

        public HashSet<string> Fields = new HashSet<string>();
        public Dictionary<string, IndexFieldOptions> FieldOptions = new Dictionary<string, IndexFieldOptions>();
        public string IndexName { get; set; }

        public JavaScriptMapOperation(JavaScriptIndexUtils jsIndexUtils, FunctionInstance mapFuncJint, JsHandle mapFunc, string indexName, string mapString)
        {
            EngineForParsing = jsIndexUtils.EngineForParsing;
            _engineStaticJint = (Engine)EngineForParsing;

            _jsIndexUtils = jsIndexUtils;
            _engineHandle = _jsIndexUtils.EngineHandle;

            MapFunc = new JsHandle(ref mapFunc);
            IndexName = indexName;
            MapString = mapString;

            if (_engineHandle.EngineType == JavaScriptEngineType.Jint)
                _resolver = ((JintEngineEx)_engineHandle).RefResolver;
        }

        ~JavaScriptMapOperation()
        {
            MapFunc.Dispose();
        }
        
        public IEnumerable<JsHandle> IndexingFunction(IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                _engineHandle.ResetCallStack();
                _engineHandle.ResetConstraints();

                if (_jsIndexUtils.GetValue(item, out JsHandle jsItem) == false)
                    continue;

#if DEBUG
                _engineHandle.MakeSnapshot("map");
#endif

                if (jsItem.IsBinder)
                {
                    using (jsItem)
                    {
                        JsHandle jsRes = JsHandle.Empty(_engineHandle.EngineType);
                        try
                        {
                            if (!MapFunc.IsFunction)
                            {
                                throw new JavaScriptIndexFuncException($"MapFunc is not a function");
                            }
                            jsRes = MapFunc.StaticCall(jsItem);
                            jsRes.ThrowOnError();
                        }
                        catch (JavaScriptException jse)
                        {
                            var (message, success) = JavaScriptIndexFuncException.PrepareErrorMessageForJavaScriptIndexFuncException(MapString, jse);
                            if (success == false)
                                throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", jse);
                            throw new JavaScriptIndexFuncException($"Failed to execute map script, {message}", jse);
                        }
                        catch (V8Exception jse)
                        {
                            var (message, success) = JavaScriptIndexFuncException.PrepareErrorMessageForJavaScriptIndexFuncException(MapString, jse);
                            jsRes.Dispose();
                            if (success == false)
                                throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", jse);
                            throw new JavaScriptIndexFuncException($"Failed to execute map script, {message}", jse);
                        }
                        catch (Exception e)
                        {
                            jsRes.Dispose();
                            throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", e);
                        }
                        finally
                        {
                            _engineHandle.ForceGarbageCollection();
                        }

                        using (jsRes)
                        {
                            if (jsRes.IsArray)
                            {
                                var length = (uint)jsRes.ArrayLength;
                                for (int i = 0; i < length; i++)
                                {
                                    var arrItem = jsRes.GetProperty(i);
                                    using (arrItem) 
                                    { 
                                        if (arrItem.IsObject)
                                        {
                                            yield return arrItem; // being yield it is converted to blittable object and not disposed - so disposing it here
                                        }
                                        else
                                        {
                                            // this check should be to catch map errors
                                            throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", new Exception($"At least one of map results is not object: {jsRes.ToString()}"));
                                        }
                                    }
                                }
                            }
                            else if (jsRes.IsObject)
                            {
                                yield return jsRes;// being yield it is converted to blittable object and not disposed - so disposing it here
                            }
                            // we ignore everything else by design, we support only
                            // objects and arrays, anything else is discarded
                        }
                    }
                    _engineHandle.ForceGarbageCollection();

#if DEBUG
                    _engineHandle.CheckForMemoryLeaks("map");
#endif
                }
                else
                {
                    throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", new Exception($"Entry item is not document: {jsItem.ToString()}"));
                }
                
                _resolver?.ExplodeArgsOn(null, null);
            }
        }
        
        public void Analyze(Engine engine)
        {
            HasDynamicReturns = false;
            HasBoostedFields = false;

            IFunction theFuncAst;
            switch (MapFuncJint)
            {
                case ScriptFunctionInstance sfi:
                    theFuncAst = sfi.FunctionDeclaration;
                    break;

                default:
                    return;
            }

            var res = CheckIfSimpleMapExpression(engine, theFuncAst);
            if (res != null)
            {
                MapFuncJint = res.Value.Function;
                theFuncAst = res.Value.FunctionAst;
            }

            foreach (var returnStatement in JavaScriptIndexUtils.GetReturnStatements(theFuncAst))
            {
                if (returnStatement.Argument == null) // return;
                    continue;

                switch (returnStatement.Argument)
                {
                    case ObjectExpression oe:

                        //If we got here we must validate that all return statements have the same structure.
                        //Having zero fields means its the first return statements we encounter that has a structure.
                        if (Fields.Count == 0)
                        {
                            foreach (var prop in oe.Properties)
                            {
                                if (prop is Property property)
                                {
                                    var fieldName = property.GetKey(engine);
                                    var fieldNameAsString = fieldName.AsString();
                                    if (fieldName == "_")
                                        HasDynamicReturns = true;

                                    Fields.Add(fieldNameAsString);

                                    var fieldValue = property.Value;
                                    if (IsBoostExpression(fieldValue))
                                        HasBoostedFields = true;
                                }
                            }
                        }
                        else if (CompareFields(oe) == false)
                        {
                            throw new InvalidOperationException($"Index {IndexName} contains different return structure from different code paths," +
                                                                $" expected properties: {string.Join(", ", Fields)} but also got:{string.Join(", ", oe.Properties.Select(x => x.GetKey(engine)))}");
                        }

                        break;

                    case CallExpression ce:

                        if (IsBoostExpression(ce))
                            HasBoostedFields = true;
                        else
                            HasDynamicReturns = true;

                        break;

                    default:
                        HasDynamicReturns = true;
                        break;
                }
            }

            static bool IsBoostExpression(Expression expression)
            {
                return expression is CallExpression ce && ce.Callee is Identifier identifier && identifier.Name == "boost";
            }
        }

        protected (FunctionInstance Function, IFunction FunctionAst)? CheckIfSimpleMapExpression(Engine engine, IFunction function)
        {
            var field = function.TryGetFieldFromSimpleLambdaExpression();
            if (field == null)
                return null;
            var properties = new List<Expression>
            {
                new Property(PropertyKind.Data, new Identifier(field), false,
                    new StaticMemberExpression(new Identifier("self"), new Identifier(field)), false, false)
            };

            if (MoreArguments != null)
            {
                for (int i = 0; i < MoreArguments.Length; i++)
                {
                    var arg = MoreArguments.Get(i.ToString()).As<FunctionInstance>();

                    if (!(arg is ScriptFunctionInstance sfi))
                        continue;
                    var moreFuncAst = sfi.FunctionDeclaration;
                    field = moreFuncAst.TryGetFieldFromSimpleLambdaExpression();
                    if (field != null)
                    {
                        properties.Add(new Property(PropertyKind.Data, new Identifier(field), false,
                        new StaticMemberExpression(new Identifier("self"), new Identifier(field)), false, false));
                    }
                }
            }

            var functionExp = new FunctionExpression(
                function.Id,
                NodeList.Create(new List<Expression> { new Identifier("self") }),
                new BlockStatement(NodeList.Create(new List<Statement>
                {
                    new ReturnStatement(new ObjectExpression(NodeList.Create(properties)))
                })),
                generator: false,
                function.Strict,
                async: false);
            var functionObject = new ScriptFunctionInstance(
                    engine,
                    functionExp,
                    LexicalEnvironment.NewDeclarativeEnvironment(engine, engine.ExecutionContext.LexicalEnvironment),
                    function.Strict
                );
            return (functionObject, functionExp);
        }

        public ArrayInstance MoreArguments { get; set; }
        public string MapString { get; internal set; }

        protected bool CompareFields(ObjectExpression oe)
        {
            if (Fields.Count != oe.Properties.Count)
                return false;
            foreach (var p in oe.Properties)
            {
                var key = p.GetKey(_engineStaticJint);
                var keyAsString = key.AsString();
                if (Fields.Contains(keyAsString) == false)
                    return false;
            }

            return true;
        }
    }
}
