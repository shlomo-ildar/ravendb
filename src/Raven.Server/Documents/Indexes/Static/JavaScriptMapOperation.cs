﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Esprima.Ast;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Runtime;
using Jint.Runtime.Environments;
using V8.Net;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Patch;
using Raven.Server.Extensions;
using Raven.Server.Documents.Indexes.Static.Utils;

namespace Raven.Server.Documents.Indexes.Static
{
    public class JavaScriptMapOperation
    {
        public FunctionInstance MapFunc;

        public bool HasDynamicReturns;

        public bool HasBoostedFields;

        public HashSet<string> Fields = new HashSet<string>();
        public Dictionary<string, IndexFieldOptions> FieldOptions = new Dictionary<string, IndexFieldOptions>();
        private readonly JavaScriptIndexUtils _javaScriptIndexUtils;
        private readonly V8EngineEx _engine;
        public string IndexName { get; set; }
        public InternalHandle MapFuncV8;

        public JavaScriptMapOperation(JavaScriptIndexUtils javaScriptIndexUtils, FunctionInstance mapFunc, InternalHandle mapFuncV8, string indexName, string mapString)
        {
            _javaScriptIndexUtils = javaScriptIndexUtils;
            _engine = _javaScriptIndexUtils.Engine;

            MapFunc = mapFunc;
            InternalHandle mapFuncV8Aux = mapFuncV8; // it is using in the caller so there is no neither need nor possibility to modify its _Object and we can modify it just for the aux value
            MapFuncV8 = new InternalHandle(ref mapFuncV8Aux, true);
            IndexName = indexName;
            MapString = mapString;
        }

        ~JavaScriptMapOperation()
        {
            MapFuncV8.Dispose();
        }

        public IEnumerable<InternalHandle> IndexingFunction(IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                _engine.ResetCallStack();
                _engine.ResetConstraints();

                if (_javaScriptIndexUtils.GetValue(item, out InternalHandle jsItem) == false) {
                    continue;
                }

                if (jsItem.IsBinder)
                {
                    using (jsItem.Object) // here the whole BOI objects tree is disposed without GC involvement
                    {
                        InternalHandle jsRes = InternalHandle.Empty;
                        try
                        {
                            if (!MapFuncV8.IsFunction) {
                                throw new JavaScriptIndexFuncException($"MapFuncV8 is not a function");
                            }
                            jsRes = MapFuncV8.StaticCall(jsItem);

                            //using (var jsRes1 = new InternalHandle(ref jsRes, true)) {}
                            /*using (var jsStrRes = _engine.JsonStringify.StaticCall(new InternalHandle(ref jsRes, true))) {
                                var strRes = jsStrRes.AsString; // for debugging 
                            }*/
                            jsRes.ThrowOnError();
                        }
                        catch (V8Exception jse)
                        {
                            var (message, success) = JavaScriptIndexFuncException.PrepareErrorMessageForJavaScriptIndexFuncException(MapString, jse);
                            if (success == false)
                                throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", jse);
                            throw new JavaScriptIndexFuncException($"Failed to execute map script, {message}", jse);
                        }
                        catch (Exception e)
                        {
                            jsRes.Dispose();
                            throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", e);
                        }

                        using (jsRes) // the parts of the doc (objects and arrays) that are contained in the map result won't be disposed here, but in the outer using block)
                        {
                            if (jsRes.IsArray)
                            {
                                var length = (uint)jsRes.ArrayLength;
                                for (int i = 0; i < length; i++)
                                {
                                    var arrItem = jsRes.GetProperty(i);
                                    using (arrItem) { 
                                        if (arrItem.IsObject) {
                                            yield return arrItem; // being yield it is converted to blittable object and not disposed - so disposing it here
                                        }
                                        else {
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
                            else {
                                // this check should be to catch map errors
                                throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", new Exception($"Map result is not object: {jsRes.ToString()}"));
                            }
                        }
                        // we ignore everything else by design, we support only
                        // objects and arrays, anything else is discarded
                    }
                }
                else {
                    throw new JavaScriptIndexFuncException($"Failed to execute {MapString}", new Exception($"Entry item is not document: {jsItem.ToString()}"));
                }
            }
        }

        public void Analyze(Engine engine)
        {
            HasDynamicReturns = false;
            HasBoostedFields = false;

            IFunction theFuncAst;
            switch (MapFunc)
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
                MapFunc = res.Value.Function;
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

        private (FunctionInstance Function, IFunction FunctionAst)? CheckIfSimpleMapExpression(Engine engine, IFunction function)
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

        private bool CompareFields(ObjectExpression oe)
        {
            if (Fields.Count != oe.Properties.Count)
                return false;
            foreach (var p in oe.Properties)
            {
                var key = p.GetKey(_javaScriptIndexUtils.EngineJint);
                var keyAsString = key.AsString();
                if (Fields.Contains(keyAsString) == false)
                    return false;
            }

            return true;
        }
    }
}
