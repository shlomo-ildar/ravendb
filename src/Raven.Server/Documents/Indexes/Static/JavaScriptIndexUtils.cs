﻿using System;
using System.Collections.Generic;
using System.Linq;
using Esprima.Ast;
using Jint;
using Jint.Native;
using Raven.Client;
using Raven.Server.Documents.Indexes.Static.Counters;
using Raven.Server.Documents.Indexes.Static.TimeSeries;
using Raven.Server.Documents.Patch;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Static.Utils
{
    public class JavaScriptIndexUtils
    {
        public readonly IJavaScriptUtils JsUtils;
        public readonly IJsEngineHandle EngineHandle;

        public readonly IJavaScriptEngineForParsing EngineForParsing;

        public JavaScriptIndexUtils(IJavaScriptUtils jsUtils, IJavaScriptEngineForParsing engineForParsing)
        {
            JsUtils = jsUtils;
            EngineHandle = JsUtils.EngineHandle;
            EngineForParsing = engineForParsing;
        }

        public static IEnumerable<ReturnStatement> GetReturnStatements(IFunction function)
        {
            if (function is ArrowFunctionExpression arrowFunction && arrowFunction.Body is ObjectExpression objectExpression)
            {
                // looks like we have following case:
                // x => ({ Name: x.Name })
                // wrap with return statement and we're done
                return new[] { new ReturnStatement(objectExpression) };
            }

            return GetReturnStatements(function.Body);
        }
        
        public static IEnumerable<ReturnStatement> GetReturnStatements(Node stmt)
        {
            // here we only traverse the single statement, we don't try to traverse into
            // complex expression, etc. This is to avoid too much complexity such as:
            // return (function() { return { a: 1})();, etc.
            switch (stmt?.Type)
            {
                case null:
                case Nodes.BreakStatement:
                case Nodes.DebuggerStatement:
                case Nodes.EmptyStatement:
                case Nodes.ContinueStatement:
                case Nodes.ThrowStatement:

                case Nodes.ExpressionStatement: // cannot contain return that we are interested in

                    return Enumerable.Empty<ReturnStatement>();

                case Nodes.BlockStatement:
                    return GetReturnStatements(((BlockStatement)stmt).Body);
                case Nodes.DoWhileStatement:
                    return GetReturnStatements(((DoWhileStatement)stmt).Body);
                case Nodes.ForStatement:
                    return GetReturnStatements(((ForStatement)stmt).Body);
                case Nodes.ForInStatement:
                    return GetReturnStatements(((ForInStatement)stmt).Body);

                case Nodes.IfStatement:
                    var ifStatement = ((IfStatement)stmt);
                    return GetReturnStatements(ifStatement.Consequent)
                        .Concat(GetReturnStatements(ifStatement.Alternate));

                case Nodes.LabeledStatement:
                    return GetReturnStatements(((LabeledStatement)stmt).Body);

                case Nodes.SwitchStatement:
                    return GetReturnStatements(((SwitchStatement)stmt).Cases.SelectMany(x => x.Consequent));

                case Nodes.TryStatement:
                    return GetReturnStatements(((TryStatement)stmt).Block);

                case Nodes.WhileStatement:
                    return GetReturnStatements(((WhileStatement)stmt).Body);

                case Nodes.WithStatement:
                    return GetReturnStatements(((WithStatement)stmt).Body);

                case Nodes.ForOfStatement:
                    return GetReturnStatements(((ForOfStatement)stmt).Body);

                case Nodes.ReturnStatement:
                    return new[] { (ReturnStatement)stmt };

                default:
                    return Enumerable.Empty<ReturnStatement>();
            }
        }

        private static IEnumerable<ReturnStatement> GetReturnStatements(IEnumerable<StatementListItem> items)
        {
            foreach (var item in items)
            {
                if (item is Statement nested)
                {
                    foreach (var returnStatement in GetReturnStatements(nested))
                    {
                        yield return returnStatement;
                    }
                }
            }
        }

        public JsHandle GetValueOrThrow(object item, bool isMapReduce = false, bool keepAlive = false)
        {
            if (GetValue(item, out JsHandle jsValueHandle, isMapReduce: true) == false)
                throw new InvalidOperationException($"Can't convert value to JS: {item}");
            return jsValueHandle;
        }

        public bool GetValue(object item, out JsHandle jsItem, bool isMapReduce = false, bool keepAlive = false)
        {
            jsItem = JsHandle.Empty;
            string changeVector = null;
            DateTime? lastModified = null;

            if (item == null)
            {
                jsItem = EngineHandle.CreateNullValue();
                return true;
            }

            switch (item)
            {
                case DynamicBlittableJson dbj:
                {
                    var id = dbj.GetId();
                    if (isMapReduce == false && id == DynamicNullObject.Null)
                        return false;

                    dbj.EnsureMetadata();

                    if (dbj.TryGetDocument(out var doc))
                    {
                        IBlittableObjectInstance boi = JsUtils.CreateBlittableObjectInstanceFromDoc(JsUtils, null, dbj.BlittableJson, doc);
                        jsItem = boi.CreateJsHandle(keepAlive);
                    }
                    else
                    {
                        if (dbj[Constants.Documents.Metadata.LastModified] is DateTime lm)
                            lastModified = lm;

                        if (dbj[Constants.Documents.Metadata.ChangeVector] is string cv)
                            changeVector = cv;

                        IBlittableObjectInstance boi = JsUtils.CreateBlittableObjectInstanceFromScratch(JsUtils, null, dbj.BlittableJson, id, lastModified, changeVector);
                        jsItem = boi.CreateJsHandle(keepAlive);
                    }

                    return true;
                }
                case DynamicTimeSeriesSegment dtss:
                {
                    var bo = JsUtils.CreateTimeSeriesSegmentObjectInstance(JsUtils.EngineHandle, dtss);
                    jsItem = bo.CreateJsHandle(keepAlive: keepAlive);
                    return true;
                }
                case DynamicCounterEntry dce:
                {
                    var bo = JsUtils.CreateCounterEntryObjectInstance(JsUtils.EngineHandle, dce);
                    jsItem = bo.CreateJsHandle(keepAlive: keepAlive);
                    return true;
                }
                case BlittableJsonReaderObject bjro:
                {
                    //This is the case for map-reduce
                    IBlittableObjectInstance bo = JsUtils.CreateBlittableObjectInstanceFromScratch(JsUtils, null, bjro, null, null, null);
                    jsItem = bo.CreateJsHandle(keepAlive);
                    return true;
                }
            }

            try
            {
                jsItem = EngineHandle.FromObjectGen(item);
                jsItem.ThrowOnError();
            }
            catch (Exception)
            {
                jsItem.Dispose();
                return false;
            }

            return true;
        }

        public JsHandle StringifyObject(JsHandle jsValue)
        {
            // json string of the object
            return EngineHandle.JsonStringify().StaticCall(jsValue);
        }
        
        [ThreadStatic]
        private static JsValue[] _oneItemArray;

        public static object StringifyObject(JsValue jsValue)
        {
            if (_oneItemArray == null)
                _oneItemArray = new JsValue[1];
            _oneItemArray[0] = jsValue;
            try
            {
                // json string of the object
                return jsValue.AsObject().Engine.Realm.Intrinsics.Json.Stringify(JsValue.Null, _oneItemArray);
            }
            finally
            {
                _oneItemArray[0] = null;
            }
        }
    }
}
