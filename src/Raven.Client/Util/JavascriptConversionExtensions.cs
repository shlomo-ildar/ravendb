﻿#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Lambda2Js;
using Newtonsoft.Json;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Extensions;
using Sparrow;
using Sparrow.Extensions;

namespace Raven.Client.Util
{
    public delegate void WriterAction(JavascriptWriter writer);
    public delegate void ContextAction(JavascriptConversionContext context);

    public delegate void WrapperAction(JavascriptConversionContext context, ContextAction obj);
    
    internal class JavascriptConversionExtensions
    {
        internal const string TransparentIdentifier = "<>h__TransparentIdentifier";
        private const string DefaultAliasPrefix = "__rvn";

        public static void WriteObjectPropertyAccess(JavascriptConversionContext context, ContextAction obj, WriterAction access,
            WrapperAction objWrapper = null, Expression expression = null, bool addDot = false)
        {
                WriteObjectPropertyAccess(context, obj, new WriterAction[] {access},  objWrapper, expression, addDot);
        }

        public static void WriteObjectPropertyAccess(JavascriptConversionContext context, ContextAction obj, ContextAction access, 
            WrapperAction objWrapper = null, Expression expression = null, bool addDot = false)
        {
            WriteObjectPropertyAccess(context, obj, new ContextAction[] {access},  objWrapper, expression, addDot);
        }

        public static void WriteObjectPropertyAccess(JavascriptConversionContext context, ContextAction obj, WriterAction[] accesses,
            WrapperAction objWrapper = null, Expression expression = null, bool addDot = false)
        {
            var accessesCtx = new ContextAction[accesses.Length];
            int i = 0;
            foreach (var access in accesses)
            {
                ContextAction accessCtx = access == null
                    ? null
                    : context =>
                    {
                        var writer = context.GetWriter();
                        access(writer);
                    };

                accessesCtx[i++] = accessCtx;
            }

            WriteObjectPropertyAccess(context, obj, accessesCtx,  objWrapper, expression, addDot);
        }

        public static void WriteObjectPropertyAccess(JavascriptConversionContext context, ContextAction obj, ContextAction[] accesses, 
            WrapperAction objWrapper = null, Expression expression = null, bool addDot = false)
        {
            void WriteObjWrapper(ContextAction obj)
            {
                if (objWrapper == null)
                    obj(context);
                else
                    objWrapper(context, obj);
            }
            
            context.PreventDefault();
            var writer = context.GetWriter();

            var accessesToDo = accesses.Where((x => x != null));
            var accessCount = accessesToDo.Count();

            writer.Write(new string('(', accessCount));
            WriteObjWrapper(obj);

            if (accesses == null)
                return;

            bool useOptionalChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).UseOptionalChanining;
            var optChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).OptionalChanining;

            using (expression != null ? writer.Operation(expression) : null)
            {
                foreach (var access in accessesToDo)
                {
                    if (useOptionalChaining)
                    {
                        writer.Write(optChaining);
                        if (addDot)
                            writer.Write(".");
                    }

                    access(context);
                    writer.Write(")");
                }
            }
        }
        
        public class CustomMethods : JavascriptConversionExtension
        {
            public readonly Dictionary<string, object> Parameters = new Dictionary<string, object>();
            public int Suffix { get; set; }
            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var methodCallExpression = context.Node as MethodCallExpression;

                var nameAttribute = methodCallExpression?
                    .Method
                    .GetCustomAttributes(typeof(JavascriptMethodNameAttribute), false)
                    .OfType<JavascriptMethodNameAttribute>()
                    .FirstOrDefault();

                if (nameAttribute == null)
                    return;
                context.PreventDefault();

                var optChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).OptionalChanining;

                var writer = context.GetWriter();
                writer.Write($"{optChaining}.");
                writer.Write(nameAttribute.Name);
                writer.Write("(");

                var args = new List<Expression>();
                foreach (var expression in methodCallExpression.Arguments)
                {
                    if (expression.Type.IsArray == false)
                    {
                        args.Add(expression);
                        continue;
                    }

                    if (expression is NewArrayExpression newArrayExpression)
                    {
                        args.AddRange(newArrayExpression.Expressions);
                        continue;
                    }

                    if (LinqPathProvider.GetValueFromExpressionWithoutConversion(expression, out var arrayValue) == false)
                    {
                        throw new InvalidOperationException($"Couldn't get value from an array expression: {expression}");
                    }

                    var array = arrayValue as Array;
                    Debug.Assert(array != null);

                    foreach (var value in array)
                    {
                        args.Add(Expression.Constant(value));
                    }
                }

                if (nameAttribute.Name == "filter")
                {
                    if (args[0] is LambdaExpression lambda)
                    {
                        writer.Write(lambda.Parameters[0].Name);
                        writer.Write(" => ");
                        writer.Write(" !("); // negate body
                        context.Visitor.Visit(lambda.Body);
                        writer.Write(")");
                    }
                }
                else
                {
                    for (var i = 0; i < args.Count; i++)
                    {
                        var name = $"arg_{Parameters.Count}_{Suffix}";
                        if (i != 0)
                            writer.Write(", ");
                        writer.Write("args.");
                        writer.Write(name);
                        object val;
                        if (LinqPathProvider.GetValueFromExpressionWithoutConversion(args[i], out val))
                            Parameters[name] = val;
                    }
                }

                if (nameAttribute.PositionalArguments != null)
                {
                    for (int i = args.Count;
                        i < nameAttribute.PositionalArguments.Length;
                        i++)
                    {
                        if (i != 0)
                            writer.Write(", ");
                        context.Visitor.Visit(Expression.Constant(nameAttribute.PositionalArguments[i]));
                    }
                }

                writer.Write(")");
            }
        }

        public class DictionarySupport : JavascriptConversionExtension
        {
            public enum DictionaryInnerCall
            {
                None,
                Key,
                Value,
                KeyValue,
                Map
            }

            private DictionaryInnerCall _innerCallExpected;

            private string _paramName = string.Empty;

            private void HandleMap(JavascriptConversionContext context, MethodCallExpression mce)
            {
                if (mce.Arguments.Count < 2 || !(mce.Arguments[1] is LambdaExpression lambda))
                    return;

                _paramName = lambda.Parameters[0]?.Name;

                context.PreventDefault();
                var writer = context.GetWriter();
                using (writer.Operation(lambda))
                {
                    writer.Write("Object.map(");
                    context.Visitor.Visit(mce.Arguments[0]);
                    writer.Write(", function(v, k){ return ");
                    context.Visitor.Visit(lambda.Body);
                    writer.Write(";})");

                    _innerCallExpected = default;
                    _paramName = string.Empty;
                }
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                // Rewrite .Count / .Values / .Keys
                if (context.Node is MemberExpression contextNode
                    && contextNode.Expression != null
                    && LinqMethodsSupport.IsDictionary(contextNode.Expression.Type))
                {
                    context.PreventDefault();
                    // Get KeyValueType identifier:
                    var keyValuePairType = typeof(KeyValuePair<,>)
                                .MakeGenericType(contextNode.Expression.Type.GetGenericArguments());

                    switch (contextNode.Member.Name)
                    {
                        case "Count":
                            {
                                var expression = Expression.Call(
                                    typeof(Enumerable),
                                    "Count",
                                    new[] { keyValuePairType },
                                    contextNode.Expression
                                );
                                _innerCallExpected = DictionaryInnerCall.Key;
                                context.Visitor.Visit(expression);
                                return;
                            }
                        case "Keys":
                            _innerCallExpected = DictionaryInnerCall.Key;
                            context.Visitor.Visit(contextNode.Expression);
                            return;

                        case "Values":
                            _innerCallExpected = DictionaryInnerCall.Value;
                            context.Visitor.Visit(contextNode.Expression);
                            return;
                    }
                }

                // Only call it when we do a methodCall on a memberExpression of type dictionary
                if (context.Node is MethodCallExpression callNode
                    && callNode.Arguments.Count > 0
                    && LinqMethodsSupport.IsDictionary(callNode.Arguments[0].Type))
                {
                    if (_innerCallExpected == default)
                    {
                        // If not given, decide on method name if we should shorten:
                        switch (callNode.Method.Name)
                        {
                            default:
                                _innerCallExpected = DictionaryInnerCall.KeyValue;
                                break;

                            case "Count":
                                _innerCallExpected = DictionaryInnerCall.Key;
                                break;

                            case "SelectMany":
                                return;

                            case "Select":
                                _innerCallExpected = DictionaryInnerCall.Map;
                                HandleMap(context, callNode);
                                return;
                        }
                    }
                }

                // Now we translate the memberExpression
                else if (_innerCallExpected != default
                    && _innerCallExpected != DictionaryInnerCall.Map
                    && LinqMethodsSupport.IsDictionary(context.Node.Type))
                {
                    var currentCall = _innerCallExpected;
                    _innerCallExpected = default;

                    var writer = context.GetWriter();
                    using (writer.Operation(context.Node))
                    {
                        ContextAction obj = context =>
                        {
                            context.Visitor.Visit(context.Node);
                        };

                        WrapperAction objWrapper = (context, obj) =>
                        {
                            var writer = context.GetWriter();
                            writer.Write("Object.keys(");
                            obj(context);
                            writer.Write(")");
                        };

                        WriterAction access = null;
                        // Do not translate Key (we already have the keys here!)
                        if (currentCall != DictionaryInnerCall.Key)
                        {
                            access = writer =>
                            {
                                writer.Write(".map(function(a){");

                                switch (currentCall)
                                {
                                    case DictionaryInnerCall.Value:
                                        writer.Write("return ");
                                        context.Visitor.Visit(context.Node);
                                        writer.Write("[a];");
                                        break;

                                    case DictionaryInnerCall.KeyValue:
                                        writer.Write("return{Key: a,Value:");
                                        context.Visitor.Visit(context.Node);
                                        writer.Write("[a]};");
                                        break;
                                }

                                writer.Write("})");
                            };
                        }

                        WriteObjectPropertyAccess(context, obj, access, objWrapper);
                    }
                }

                if (_innerCallExpected == DictionaryInnerCall.Map
                    && context.Node is MemberExpression memberExpression
                    && memberExpression.Member.Name.In("Value", "Key"))
                {
                    var p = GetParameterAndCheckInternalMemberName(memberExpression, out var hasInternalKeyOrValue);

                    if (hasInternalKeyOrValue == false
                        && p?.Name == _paramName
                        && p?.Type.GenericTypeArguments.Length > 0
                        && p.Type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                    {
                        context.PreventDefault();
                        var writer = context.GetWriter();
                        using (writer.Operation(memberExpression))
                        {
                            writer.Write(memberExpression.Member.Name == "Value" ? "v" : "k");
                        }
                    }
                }
            }
        }

        public class LinqMethodsSupport : JavascriptConversionExtension
        {
            public static readonly LinqMethodsSupport Instance = new LinqMethodsSupport();

            private static HashSet<Type> _numericTypes = new HashSet<Type>
            {
                typeof(decimal), typeof(byte), typeof(sbyte),
                typeof(short), typeof(ushort), typeof(uint),
                typeof(int), typeof(long), typeof(ulong),
                typeof(double), typeof(float)
            };

            private LinqMethodsSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (context.Node is MemberExpression node &&
                    node.Member.Name == "Count" &&
                    IsCollection(node.Member.DeclaringType))
                {
                    HandleCount(context, node.Expression);
                    return;
                }

                var methodCallExpression = context.Node as MethodCallExpression;
                var method = methodCallExpression?.Method;
                if (method == null || method.IsSpecialName)
                    return;

                var methodName = method.Name;
                if (IsCollection(methodCallExpression.Method.DeclaringType) == false)
                    return;

                var useOptChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).UseOptionalChanining;
                var optChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).OptionalChanining;

                string? reduceInitial = null;
                string? concatArg = null;
                
                ContextAction obj;
                string newName;
                switch (methodName)
                {
                    case "Any":
                        {
                            if (methodCallExpression.Arguments.Count > 1)
                            {
                                newName = "some";
                                break;
                            }

                            obj = context =>
                            {
                                context.Visitor.Visit(methodCallExpression.Arguments[0]);
                            };
                                
                            var writer = context.GetWriter();

                            WriterAction access = writer =>
                            {
                                writer.Write(".length");
                                if (useOptChaining)
                                    writer.Write("??0");
                            };
                            
                            WriteObjectPropertyAccess(context, obj, access, expression: methodCallExpression);
                            writer.Write(" > 0");
                            return;
                        }
                    case "All":
                        newName = "every";
                        break;

                    case "Select":
                    case "Sum":
                        newName = "map";
                        break;

                    case "Where":
                        newName = "filter";
                        break;

                    case "IndexOf":
                    case "Contains":
                        newName = "indexOf";
                        break;

                    case "Cast":
                    case "ToList":
                    case "ToArray":
                        {
                            context.PreventDefault();
                            context.Visitor.Visit(methodCallExpression.Arguments[0]);
                            return;
                        }

                    case "Concat":
                        newName = "concat";
                        break;

                    case "Average":
                        {
                            context.PreventDefault();
                            // -- Rewrite expression to Sum() (using second (if available) argument Types) / Count() (using last argument Type)

                            var sum = Expression.Call(
                                typeof(Enumerable),
                                "Sum",
                                methodCallExpression.Arguments.Count > 1 ?
                                    new Type[] { methodCallExpression.Arguments[1].Type.GenericTypeArguments.First() } :
                                    new Type[] { },
                                methodCallExpression.Arguments.ToArray());

                            // Get resulting type by interface of IEnumerable<>
                            var typeArguments = methodCallExpression.Arguments[0].Type.GetInterfaces()
                                .Concat(new[] { methodCallExpression.Arguments[0].Type })
                                .First(a => a.IsGenericType && a.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                                .GetGenericArguments()
                                .First();
                            var count = Expression.Call(typeof(Enumerable), "Count", new Type[] { typeArguments }, methodCallExpression.Arguments[0]);

                            // When doing the divide, make sure count matches the sum type

                            context.Visitor.Visit(sum);

                            var writer = context.GetWriter();
                            using (writer.Operation(methodCallExpression))
                            {
                                writer.Write("/(");
                                context.Visitor.Visit(Expression.Convert(count, sum.Type));

                                // Avoid division by 0
                                writer.Write("||1)");
                                return;
                            }
                        }
                    case "ToDictionary":
                        {
                            var writer = context.GetWriter();
                            using (writer.Operation(methodCallExpression))
                            {
                                WriterAction access = writer =>
                                {
                                    writer.Write(".reduce(function(_obj, _cur) {");
                                    writer.Write("_obj[");
                                    context.Visitor.Visit(methodCallExpression.Arguments[1]);
                                    writer.Write("(_cur)] = ");

                                    if (methodCallExpression.Arguments.Count > 2)
                                    {
                                        context.Visitor.Visit(methodCallExpression.Arguments[2]);
                                        writer.Write("(_cur);");
                                    }
                                    else
                                    {
                                        writer.Write("_cur;");
                                    }

                                    writer.Write("return _obj;");
                                    writer.Write("}, {})");
                                    
                                    if (useOptChaining)
                                        writer.Write("??{}");
                                };
                                
                                WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access);
                            }
                            return;
                        }
                    case "FirstOrDefault":
                    case "First":
                        {
                            WriterAction access = writer =>
                            {
                                if (methodCallExpression.Arguments.Count > 1)
                                {
                                    writer.Write(".find");
                                    context.Visitor.Visit(methodCallExpression.Arguments[1]);
                                    return;
                                }

                                writer.Write("[0]");
                            };
                            
                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access, 
                                expression: methodCallExpression, addDot: methodCallExpression.Arguments.Count <= 1);
                            return;
                        }
                    case "Last":
                    case "LastOrDefault":
                        {
                            WriterAction access = writer =>
                            {
                                if (methodCallExpression.Arguments.Count > 1)
                                {
                                    writer.Write(".slice().reverse().find");
                                    context.Visitor.Visit(methodCallExpression.Arguments[1]);
                                    return;
                                }

                                // arrayExpr.slice([-1])[0] will get the last value
                                writer.Write(".slice(-1)[0]");
                            };

                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access, expression: methodCallExpression);
                            return;
                        }
                    case "ElementAt":
                        {
                            WriterAction access = writer =>
                            {
                                writer.Write("[");
                                context.Visitor.Visit(methodCallExpression.Arguments[1]);
                                writer.Write("]");
                            };

                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access, 
                                expression: methodCallExpression, addDot: true);
                            return;
                        }
                    case "Reverse":
                        {
                            WriterAction access = writer =>
                            {
                                writer.Write(".slice().reverse()");
                                if (useOptChaining)
                                    writer.Write("??[]");
                            };

                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access, expression: methodCallExpression);
                            return;
                        }
                    case "Max":
                        {
                            HandleMaxOrMin(context, methodCallExpression, true);
                            return;
                        }
                    case "Min":
                        {
                            HandleMaxOrMin(context, methodCallExpression);
                            return;
                        }
                    case "Skip":
                        {
                            WriterAction access = writer =>
                            {
                                writer.Write(".slice(");
                                context.Visitor.Visit(methodCallExpression.Arguments[1]);
                                writer.Write(", ");
                                context.Visitor.Visit(methodCallExpression.Arguments[0]);
                                writer.Write($"{optChaining}.length)");
                                if (useOptChaining)
                                    writer.Write("??0");
                            };

                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access, expression: methodCallExpression);
                            return;
                        }
                    case "Take":
                        {
                            WriterAction access = writer =>
                            {
                                writer.Write(".slice(0, ");
                                context.Visitor.Visit(methodCallExpression.Arguments[1]);
                                writer.Write(")");
                            };

                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access, expression: methodCallExpression);
                            return;
                        }
                    case "Distinct":
                        {
                            context.PreventDefault();
                            var writer = context.GetWriter();
                            using (writer.Operation(methodCallExpression))
                            {
                                writer.Write("Array.from(new Set(");
                                context.Visitor.Visit(methodCallExpression.Arguments[0]);
                                writer.Write("))");
                                return;
                            }
                        }
                    case "DefaultIfEmpty":
                        {
                            context.PreventDefault();

                            object defaultVal = null;
                            var genericArguments = methodCallExpression.Arguments[0].Type.GetGenericArguments();
                            if (genericArguments.Length > 0)
                            {
                                if (genericArguments[0] == typeof(bool))
                                {
                                    defaultVal = "false";
                                }
                                else if (genericArguments[0] == typeof(char))
                                {
                                    defaultVal = '0';
                                }
                                else
                                {
                                    defaultVal = GetDefault(genericArguments[0]);
                                }
                            }

                            var writer = context.GetWriter();
                            using (writer.Operation(methodCallExpression))
                            {
                                writer.Write($"(function(arr){{return arr.length > 0 ? arr : [{defaultVal ?? "null"}]}})(");
                                context.Visitor.Visit(methodCallExpression.Arguments[0]);
                                writer.Write(")");
                                return;
                            }
                        }
                    case "SelectMany":
                        {
                            if (methodCallExpression.Arguments.Count > 2)
                                return;

                            var writer = context.GetWriter();
                            using (writer.Operation(methodCallExpression))
                            {
                                if (IsDictionary(methodCallExpression.Arguments[0].Type))
                                {
                                    if (!(methodCallExpression.Arguments[1] is LambdaExpression lambda) ||
                                        !(lambda.Body is MemberExpression member) ||
                                        member.Member.Name != "Key" && member.Member.Name != "Value")
                                        return;

                                    obj = context =>
                                    {
                                        context.Visitor.Visit(methodCallExpression.Arguments[0]);
                                    };

                                    WrapperAction objWrapper = (context, obj) =>
                                    {
                                        writer.Write("Object.getOwnPropertyNames(");
                                        obj(context);
                                        writer.Write(")");
                                    };

                                    WriterAction access1 = member.Member.Name == "Value" ?
                                        writer =>
                                        {
                                            writer.Write(".map(function(k){return ");
                                            context.Visitor.Visit(methodCallExpression.Arguments[0]);
                                            writer.Write($"[k]}}){optChaining}");
                                        }
                                        : null;
                                    
                                    WriterAction access2 = writer =>
                                    {
                                        writer.Write(".reduce(function(a, b) { return a.concat(b);},[])");
                                        if (useOptChaining)
                                            writer.Write("??[]");
                                    };
                                    
                                    WriteObjectPropertyAccess(context, obj, new WriterAction[] {access1, access2}, objWrapper);
                                }
                                else
                                {
                                    WriterAction access = writer =>
                                    {
                                        writer.Write(".reduce(function(a, b) { return a.concat(");
                                        context.Visitor.Visit(methodCallExpression.Arguments[1]);
                                        writer.Write("(b)); }, [])");
                                        if (useOptChaining)
                                            writer.Write("??[]");
                                    };

                                    WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access);
                                }

                                return;
                            }
                        }
                    case "Count":
                        {
                            WriterAction access1 = methodCallExpression.Arguments.Count > 1
                                ? writer =>
                                {
                                    writer.Write(".filter");
                                    context.Visitor.Visit(methodCallExpression.Arguments[1]);
                                }
                                : null;

                            WriterAction access2 = writer =>
                            {
                                writer.Write($".length");
                                if (useOptChaining)
                                    writer.Write("??0");
                            };

                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), 
                                new WriterAction[] {access1, access2}, expression: methodCallExpression);
                            return;
                        }
                    case "OrderBy":
                        {
                            OrderByToSort(context, methodCallExpression);
                            return;
                        }
                    case "OrderByDescending":
                        {
                            OrderByToSort(context, methodCallExpression, true);
                            return;
                        }
                    case "ContainsKey":
                        {
                            return;
                        }
                    default:
                        throw new NotSupportedException($"Unable to translate '{methodName}' to RQL operation because this method is not familiar to the RavenDB query provider.")
                        {
                            HelpLink = "DoNotWrap"
                        };
                }


                WriterAction accessTail = methodName == "Sum"
                    ? writer =>
                    {
                        reduceInitial = "0";
                        writer.Write($".reduce(function(a, b) {{ return a + b; }}, {reduceInitial})");
                    }
                    : null;

                bool isArrayFunc = useOptChaining && (newName == "reduce" || newName == "concat" || newName == "some" || newName == "every" || newName == "map" || newName == "filter" || newName == "reverse");
                
                WriterAction accessHead = null;
                if (methodCallExpression.Object != null)
                {
                    accessHead = writer =>
                    {
                        if (isArrayFunc)
                        {
                            writer.Write("?[])");
                        }
                        writer.Write($".{newName}");
                        writer.Write("(");
                        context.Visitor.Visit(methodCallExpression.Arguments[0]);
                        writer.Write(")");

                        if (methodName == "Contains")
                        {
                            writer.Write(">=0");
                        }
                    };

                    obj = context =>
                    {
                        var writer = context.GetWriter();
                        if (isArrayFunc)
                        {
                            writer.Write("(");
                        }

                        context.Visitor.Visit(methodCallExpression.Object);
                    };
                }
                else
                {
                    // When having no other arguments, don't call the function, when it's a .map operation
                    // .Sum()/.Average()/.Select() for example can be called without arguments, which means,
                    // map all, though .map() without arguments returns an empty list
                    accessHead = writer =>
                    {
                        if (isArrayFunc)
                        {
                            writer.Write("?[])");
                        }

                        if (newName != "map" || methodCallExpression.Arguments.Count > 1)
                        {
                            writer.Write($".{newName}");
                            writer.Write("(");
                            if (methodCallExpression.Arguments.Count > 1)
                            {
                                context.Visitor.Visit(methodCallExpression.Arguments[1]);
                            }

                            writer.Write(")");
                        }
                    };
                    
                    obj = context =>
                    {
                        var writer = context.GetWriter();
                        if (isArrayFunc)
                        {
                            writer.Write("(");
                        }

                        context.Visitor.Visit(methodCallExpression.Arguments[0]);
                    };
                }

                WriteObjectPropertyAccess(context, obj, new WriterAction[] {accessHead, accessTail});
            }

            private static void OrderByToSort(JavascriptConversionContext context, MethodCallExpression methodCallExpression, bool desc = false)
            {
                WriterAction access = writer =>
                {
                    string path = null;
                    var isNumber = false;
                    if (methodCallExpression.Arguments.Count == 2 &&
                        methodCallExpression.Arguments[1] is LambdaExpression lambda)
                    {
                        if (lambda.Body is MemberExpression memberExpression)
                        {
                            path = GetMemberPath(memberExpression);

                            isNumber = _numericTypes.Contains(memberExpression.Type);
                        }
                        else if (lambda.Body is BinaryExpression be && be.NodeType == ExpressionType.ArrayIndex &&
                                 be.Right is ConstantExpression ce && be.Left is MemberExpression member)
                        {
                            // e.g. entry.Values[0]
                            var memberPath = GetMemberPath(member);
                            path = $"{memberPath}[{ce.Value}]";
                            isNumber = _numericTypes.Contains(be.Type);
                        }
                    }

                    writer.Write(".sort(");
                    if (path != null)
                    {
                        writer.Write("function (a, b){ return ");

                        if (isNumber)
                        {
                            writer.Write(desc
                                ? $"b.{path} - a.{path}"
                                : $"a.{path} - b.{path}");
                        }
                        else
                        {
                            writer.Write(desc
                                ? $"((a.{path} < b.{path}) ? 1 : (a.{path} > b.{path})? -1 : 0)"
                                : $"((a.{path} < b.{path}) ? -1 : (a.{path} > b.{path})? 1 : 0)");
                        }

                        writer.Write(";}");
                    }

                    writer.Write(")");
                };

                WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), access, expression: methodCallExpression);
            }

            private static string GetMemberPath(MemberExpression memberExpression)
            {
                if (memberExpression.Expression is ParameterExpression)
                    return memberExpression.Member.Name;

                GetInnermostExpression(memberExpression, out var nestedPath, out _);

                return nestedPath != string.Empty
                    ? $"{nestedPath}.{memberExpression.Member.Name}"
                    : memberExpression.Member.Name;
            }

            private static void HandleMaxOrMin(JavascriptConversionContext context, MethodCallExpression methodCallExpression, bool max = false)
            {
                var maxOrMin = max ? "Raven_Max" : "Raven_Min";
                WriterAction access1 = (methodCallExpression.Arguments.Count > 1) ?
                    writer =>
                    {
                        writer.Write(".map");
                        context.Visitor.Visit(methodCallExpression.Arguments[1]);
                    }
                    : null;

                WriterAction access2 = writer =>
                {
                    writer.Write($".reduce(function(a, b) {{ return {maxOrMin}(a, b);}}, null)");
                };

                WriteObjectPropertyAccess(context, context => context.Visitor.Visit(methodCallExpression.Arguments[0]), 
                    new WriterAction[] {access1, access2}, expression: methodCallExpression);
            }

            private static void HandleCount(JavascriptConversionContext context, Expression expression)
            {
                var useOptChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).UseOptionalChanining;

                WriterAction access = writer =>
                {
                    writer.Write(".length");
                    if (useOptChaining)
                        writer.Write("??0");
                };

                WriteObjectPropertyAccess(context, context => context.Visitor.Visit(expression), access, expression: expression);
            }

            public static bool IsCollection(Type type)
            {
                if (type.GetGenericArguments().Length == 0)
                    return type == typeof(Enumerable);

                return typeof(IEnumerable).IsAssignableFrom(type.GetGenericTypeDefinition());
            }

            public static object GetDefault(Type type)
            {
                if (type.IsValueType)
                {
                    return Activator.CreateInstance(type);
                }
                return null;
            }

            public static bool IsDictionary(Type type)
            {
                if (type.GetGenericArguments().Length == 0)
                    return type == typeof(Dictionary<,>) || type == typeof(IDictionary<,>);

                return typeof(IDictionary).IsAssignableFrom(type) ||
                       typeof(Dictionary<,>).IsAssignableFrom(type.GetGenericTypeDefinition()) ||
                       typeof(IDictionary<,>).IsAssignableFrom(type.GetGenericTypeDefinition());
            }
        }

        public class NullableSupport : JavascriptConversionExtension
        {
            public static readonly NullableSupport Instance = new NullableSupport();

            private NullableSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (context.Node is BinaryExpression binaryExpression &&
                    (binaryExpression.NodeType == ExpressionType.GreaterThanOrEqual ||
                     binaryExpression.NodeType == ExpressionType.LessThanOrEqual) &&
                    (binaryExpression.Left.Type.IsNullableType() ||
                     binaryExpression.Right.Type.IsNullableType()))
                {
                    // RavenDB-12359
                    // In order to avoid null >= 0  (and null<=0)
                    // we translate x>=y  to x>y||x===y
                    //https://blog.campvanilla.com/javascript-the-curious-case-of-null-0-7b131644e274

                    var expr = Expression.OrElse(binaryExpression.NodeType == ExpressionType.GreaterThanOrEqual
                            ? Expression.GreaterThan(binaryExpression.Left, binaryExpression.Right)
                            : Expression.LessThan(binaryExpression.Left, binaryExpression.Right),
                        Expression.Equal(binaryExpression.Left, binaryExpression.Right));
                    context.PreventDefault();
                    context.Visitor.Visit(expr);
                    return;
                }

                if (!(context.Node is MemberExpression memberExpression) ||
                    memberExpression.Expression == null ||
                    memberExpression.Expression.Type.IsNullableType() == false)
                    return;

                if (memberExpression.Member.Name == "Value")
                {
                    context.PreventDefault();
                    context.Visitor.Visit(memberExpression.Expression);
                }
                else if (memberExpression.Member.Name == "HasValue")
                {
                    context.PreventDefault();
                    var writer = context.GetWriter();
                    using (writer.Operation(memberExpression))
                    {
                        context.Visitor.Visit(memberExpression.Expression);
                        writer.Write(" != null");
                    }
                }
            }
        }

        public class CompareExchangeSupport : JavascriptConversionExtension
        {
            public static readonly CompareExchangeSupport Instance = new CompareExchangeSupport();

            private CompareExchangeSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var methodCallExpression = context.Node as MethodCallExpression;

                if (methodCallExpression?.Method.Name != nameof(RavenQuery.CmpXchg))
                    return;

                if (methodCallExpression.Method.DeclaringType != typeof(RavenQuery)
                    && (methodCallExpression.Object == null || methodCallExpression.Object.Type != typeof(IDocumentSession)))
                    return;

                var key = methodCallExpression.Arguments[0];

                if (string.IsNullOrEmpty(key.ToString()))
                {
                    throw new NotSupportedException("");
                }

                context.PreventDefault();

                var writer = context.GetWriter();
                using (writer.Operation(methodCallExpression))
                {
                    writer.Write("cmpxchg(");
                    context.Visitor.Visit(key);
                    writer.Write(")");
                }
            }
        }

        public class CounterSupport : JavascriptConversionExtension
        {
            public static readonly CounterSupport Instance = new CounterSupport();

            private CounterSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is MethodCallExpression mce))
                    return;

                if (LinqPathProvider.IsCounterCall(mce) == false)
                    return;

                Expression alias;
                int nameIndex;

                if (mce.Method.DeclaringType != typeof(RavenQuery))
                {
                    alias = (mce.Object as MethodCallExpression)?.Arguments[0];
                    nameIndex = 0;
                }
                else
                {
                    alias = mce.Arguments[0];
                    nameIndex = 1;
                }

                context.PreventDefault();

                var writer = context.GetWriter();
                using (writer.Operation(mce))
                {
                    writer.Write("counter(");
                    context.Visitor.Visit(alias);
                    writer.Write(", ");
                    context.Visitor.Visit(mce.Arguments[nameIndex]);
                    writer.Write(")");
                }
            }
        }

        public class LoadSupport : JavascriptConversionExtension
        {
            public bool HasLoad { get; set; }
            public Expression Arg { get; set; }
            public bool IsEnumerable { get; set; }
            public bool DoNotTranslate { get; set; }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var methodCallExpression = context.Node as MethodCallExpression;

                if (methodCallExpression?.Method.Name != "Load")
                    return;

                if (methodCallExpression.Method.DeclaringType != typeof(RavenQuery)
                    && (methodCallExpression.Object == null || methodCallExpression.Object.Type != typeof(IDocumentSession)))
                    return;

                HasLoad = true;
                Arg = methodCallExpression.Arguments[0];
                IsEnumerable = Arg.Type.IsArray || LinqMethodsSupport.IsCollection(Arg.Type);

                if (IsEnumerable && methodCallExpression.Object?.Type == typeof(IDocumentSession))
                {
                    throw new NotSupportedException("Using IDocumentSession.Load(IEnumerable<string> ids) inside a query is not supported. " +
                                                    "You should use RavenQuery.Load(IEnumerable<string> ids) instead")
                    {
                        HelpLink = "DoNotWrap"
                    };
                }

                context.PreventDefault();

                if (DoNotTranslate)
                    return;

                var writer = context.GetWriter();
                using (writer.Operation(methodCallExpression))
                {
                    writer.Write("load(");
                    context.Visitor.Visit(Arg);
                    writer.Write(")");
                }
            }
        }

        public class MathSupport : JavascriptConversionExtension
        {
            public static readonly MathSupport Instance = new MathSupport();

            private static readonly Dictionary<string, string> SupportedNames = new Dictionary<string, string>{
                {"Abs", "abs"}, {"Acos", "acos"}, {"Asin", "asin"}, {"Atan", "atan"}, {"Atan2", "atan2"},
                {"Ceiling", "ceil"}, {"Cos", "cos"}, {"Exp", "exp"}, {"Floor", "floor"}, {"Log", "log"},
                {"Max", "max"}, {"Min", "min" }, {"Pow", "pow" }, {"Round", "round"}, {"Sin", "sin"},
                {"Sqrt", "sqrt"}, {"Tan", "tan"}
            };

            private MathSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var methodCallExpression = context.Node as MethodCallExpression;
                var method = methodCallExpression?.Method;

                if (method == null || method.DeclaringType != typeof(Math))
                    return;

                if (SupportedNames.ContainsKey(method.Name) == false)
                    throw new NotSupportedException($"Translation of System.Math.{method.Name} to JavaScript is not supported");

                var writer = context.GetWriter();
                context.PreventDefault();

                using (writer.Operation(methodCallExpression))
                {
                    writer.Write("Math.");
                    writer.Write(SupportedNames[method.Name]);

                    writer.Write("(");

                    if (method.Name == "Round" && methodCallExpression.Arguments.Count > 1)
                    {
                        context.Visitor.Visit(methodCallExpression.Arguments[0]);
                        writer.Write(" * Math.pow(10, ");
                        context.Visitor.Visit(methodCallExpression.Arguments[1]);
                        writer.Write(")) / Math.pow(10, ");
                        context.Visitor.Visit(methodCallExpression.Arguments[1]);
                    }
                    else
                    {
                        for (var i = 0; i < methodCallExpression.Arguments.Count; i++)
                        {
                            if (i != 0)
                            {
                                writer.Write(", ");
                            }

                            context.Visitor.Visit(methodCallExpression.Arguments[i]);
                        }
                    }

                    writer.Write(")");
                }
            }
        }

        public class ReservedWordsSupport : JavascriptConversionExtension
        {
            public static readonly ReservedWordsSupport Instance = new ReservedWordsSupport();

            public static readonly List<string> JsReservedWords = new List<string>{
                "function", "debugger", "delete", "export","extends",
                "import", "instanceof", "super","var", "with", "load"
            };

            private ReservedWordsSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is ParameterExpression parameterExpression) || JsReservedWords.Contains(parameterExpression.Name) == false)
                    return;

                var writer = context.GetWriter();
                context.PreventDefault();

                using (writer.Operation(parameterExpression))
                {
                    writer.Write("_");
                    writer.Write(parameterExpression);
                }
            }
        }

        public class WrappedConstantSupport<T> : JavascriptConversionExtension
        {
            private readonly IAbstractDocumentQuery<T> _documentQuery;
            private readonly List<string> _projectionParameters;

            public WrappedConstantSupport(IAbstractDocumentQuery<T> documentQuery, List<string> projectionParameters)
            {
                _documentQuery = documentQuery;
                _projectionParameters = projectionParameters;
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var node = context.Node;
                if (context.Node is MethodCallExpression mce &&
                    mce.Object is MemberExpression)
                {
                    // check if we can get constant value from method call mce

                    foreach (var arg in mce.Arguments)
                    {
                        if (arg.NodeType == ExpressionType.Parameter ||
                            arg is MemberExpression argMemberExpression &&
                            GetParameter(argMemberExpression) != null)
                            return; // can't get constant value from mce
                    }

                    node = mce.Object; // check if mce.Object is a wrapped constant
                }

                if (!(node is MemberExpression memberExpression) ||
                    IsWrappedConstantExpression(memberExpression) == false)
                    return;

                // get constant value from context.Node
                LinqPathProvider.GetValueFromExpressionWithoutConversion(context.Node, out var value);
                var parameter = _documentQuery.ProjectionParameter(value);
                _projectionParameters?.Add(parameter);

                var writer = context.GetWriter();
                context.PreventDefault();

                using (writer.Operation(context.Node))
                {
                    writer.Write(parameter);
                }
            }
        }

        public class JsonPropertyAttributeSupport : JavascriptConversionExtension
        {
            public static readonly JsonPropertyAttributeSupport Instance = new JsonPropertyAttributeSupport();

            private JsonPropertyAttributeSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is MemberExpression memberExpression))
                    return;

                var jsonPropertyAttribute = memberExpression.Member.GetCustomAttributes()
                                                                    .OfType<JsonPropertyAttribute>()
                                                                    .FirstOrDefault();
                if (jsonPropertyAttribute == null)
                    return;

                var writer = context.GetWriter();
                using (writer.Operation(memberExpression))
                {
                    WriterAction access = writer =>
                    {
                        writer.Write(".");
                        writer.Write(jsonPropertyAttribute.PropertyName ?? memberExpression.Member.Name);
                    };
                    
                    WriteObjectPropertyAccess(context, context => context.Visitor.Visit(memberExpression.Expression), access);
                }
            }
        }

        // this extension can be used only in case of CompileToJavascript called with JavascriptCompilationOptions having no flag JsCompilationFlags.ScopeParameter set
        public class MemberExpressionSupport : JavascriptConversionExtension
        {
            public static readonly MemberExpressionSupport Instance = new MemberExpressionSupport();

            private MemberExpressionSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var node = context.Node as MemberExpression;
                var nodeU = context.Node as UnaryExpression;
                if (!(node != null || nodeU != null))
                    return;

                var writer = context.GetWriter();

                var useOptChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).UseOptionalChanining;
                var optChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).OptionalChanining;

                if (nodeU != null)
                {
                    if (nodeU.NodeType == ExpressionType.ArrayLength)
                    {
                        context.PreventDefault();
                        WriterAction access = writer =>
                        {
                            writer.Write(".length");
                            if (useOptChaining)
                                writer.Write("??0");
                        };
                        WriteObjectPropertyAccess(context, context => context.Visitor.Visit(nodeU.Operand), access);
                    }
                    return;
                }

                if (node.Expression == null && (object) node.Member.DeclaringType == (object) typeof (string) && node.Member.Name == "Empty")
                {
                    context.PreventDefault();
                    using (writer.Operation(JavascriptOperationTypes.Literal))
                        writer.Write("''");
                    return;
                }
                using (writer.Operation((Expression) node))
                {
                    JavascriptMetadataProvider metadataProvider = context.Options.GetMetadataProvider();
                    int length = writer.Length;
                    
                    if (node.Expression == null)
                    {
                        Type declaringType = node.Member.DeclaringType;
                        if ((object) declaringType != null)
                        {
                            writer.Write(declaringType.FullName);
                            writer.Write($"{optChaining}.");
                            writer.Write(declaringType.Name);
                        }
                    }
                    else if (node.Expression.Type.IsClosureRootType())
                    {
                        return; // should be processed with default logic
                    }
                    else // if (node.Expression != this.contextParameter) - in case no JsCompilationFlags.ScopeParameter is set this condition is always true as this.contextParameter is null
                    {
                        context.Visitor.Visit(node.Expression);
                    }

                    context.PreventDefault();
                    if (writer.Length > length)
                    {
                        writer.Write($"{optChaining}.");
                    }

                    PropertyInfo member = node.Member as PropertyInfo;
                    if ((object) member?.DeclaringType != null && (object) node.Type == (object) typeof (int) && node.Member.Name == "Count" && TypeHelpers.IsListType(member.DeclaringType))
                        writer.Write("length");
                    else
                        writer.Write(metadataProvider.GetMemberMetadata(node.Member)?.MemberName);
                }
            }
        }
        
        public class ReplaceParameterWithNewName : JavascriptConversionExtension
        {
            private readonly string _newName;
            private readonly ParameterExpression _parameter;

            public ReplaceParameterWithNewName(ParameterExpression parameter, string newName)
            {
                _newName = newName;
                _parameter = parameter;
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var parameter = context.Node as ParameterExpression;
                if (parameter == null || parameter != _parameter)
                    return;

                context.PreventDefault();
                var writer = context.GetWriter();

                using (writer.Operation(parameter))
                {
                    writer.Write(_newName);
                }
            }
        }

        public class TransparentIdentifierSupport : JavascriptConversionExtension
        {
            private bool _doNotIgnore;
            private int _maxSuffixToNotIgnore = -1;

            private static int ParseTransparentIdentifierSuffix(string name)
            {
                var substr = name.Substring(TransparentIdentifier.Length);
                int.TryParse(substr, out var suffix);
                return suffix;
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (context.Node is LambdaExpression lambdaExpression
                    && lambdaExpression.Parameters.Count > 0
                    && lambdaExpression.Parameters[0].Name.StartsWith(TransparentIdentifier))
                {
                    _doNotIgnore = true;

                    var oldMaxSuffix = _maxSuffixToNotIgnore;
                    var suffix = ParseTransparentIdentifierSuffix(lambdaExpression.Parameters[0].Name);

                    if (suffix > _maxSuffixToNotIgnore)
                    {
                        _maxSuffixToNotIgnore = suffix;
                    }

                    context.PreventDefault();

                    var writer = context.GetWriter();
                    using (writer.Operation(lambdaExpression))
                    {
                        writer.Write("function(");
                        writer.Write(lambdaExpression.Parameters[0].Name.Replace(TransparentIdentifier, DefaultAliasPrefix));
                        writer.Write("){return ");
                        context.Visitor.Visit(lambdaExpression.Body);
                        writer.Write(";}");
                    }

                    _maxSuffixToNotIgnore = oldMaxSuffix;

                    return;
                }

                if (context.Node is ParameterExpression p &&
                    p.Name.StartsWith(TransparentIdentifier) &&
                    _doNotIgnore)
                {
                    context.PreventDefault();
                    var writer = context.GetWriter();
                    using (writer.Operation(p))
                    {
                        writer.Write(p.Name.Replace(TransparentIdentifier, DefaultAliasPrefix));
                    }

                    return;
                }

                if (!(context.Node is MemberExpression member))
                    return;

                if (member.Expression is MemberExpression innerMember
                    && innerMember.Member.Name.StartsWith(TransparentIdentifier))
                {
                    context.PreventDefault();

                    var writer = context.GetWriter();
                    using (writer.Operation(innerMember))
                    {
                        WriterAction accessBody = writer =>
                        {
                            var name = member.Member.Name;

                            if (_doNotIgnore && name.StartsWith(TransparentIdentifier))
                            {
                                name = name.Replace(TransparentIdentifier, DefaultAliasPrefix);
                            }
                            else if (ReservedWordsSupport.JsReservedWords.Contains(name))
                            {
                                name = "_" + name;
                            }

                            writer.Write(name);
                        };

                        if (_doNotIgnore)
                        {
                            var suffix = ParseTransparentIdentifierSuffix(innerMember.Member.Name);
                            if (suffix <= _maxSuffixToNotIgnore)
                            {
                                WriterAction access = writer =>
                                {
                                    writer.Write(".");
                                    accessBody(writer);
                                };
                                
                                WriteObjectPropertyAccess(context, context => context.Visitor.Visit(innerMember), access);
                                return;
                            }
                        }

                        accessBody(writer);
                    }

                    return;
                }

                if (member.Expression is ParameterExpression parameter && parameter.Name.StartsWith(TransparentIdentifier))
                {
                    context.PreventDefault();

                    var writer = context.GetWriter();
                    using (writer.Operation(parameter))
                    {
                        var name = member.Member.Name;

                        WriterAction accessBody = writer =>
                        {
                            if (ReservedWordsSupport.JsReservedWords.Contains(name))
                            {
                                name = "_" + name;
                            }

                            writer.Write($"{name}");
                        };

                        if (_doNotIgnore)
                        {
                            var suffix = ParseTransparentIdentifierSuffix(parameter.Name);
                            if (suffix <= _maxSuffixToNotIgnore)
                            {
                                WriterAction access = writer =>
                                {
                                    writer.Write(".");
                                    name = name.Replace(TransparentIdentifier, DefaultAliasPrefix);
                                    
                                    accessBody(writer);
                                };

                                WriteObjectPropertyAccess(context, writer => writer.Write(parameter.Name.Replace(TransparentIdentifier, DefaultAliasPrefix)), access);
                                return;
                            }
                        }

                        accessBody(writer);
                    }
                }
            }
        }

        public class InvokeSupport : JavascriptConversionExtension
        {
            public static readonly InvokeSupport Instance = new InvokeSupport();

            private InvokeSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is InvocationExpression invocationExpression))
                    return;

                context.PreventDefault();
                context.Visitor.Visit(invocationExpression.Expression);

                var writer = context.GetWriter();
                using (writer.Operation(invocationExpression))
                {
                    writer.Write("(");

                    for (var i = 0; i < invocationExpression.Arguments.Count; i++)
                    {
                        if (i != 0)
                        {
                            writer.Write(", ");
                        }

                        context.Visitor.Visit(invocationExpression.Arguments[i]);
                    }

                    writer.Write(")");
                }
            }
        }

        public class ValueTypeParseSupport : JavascriptConversionExtension
        {
            public static readonly ValueTypeParseSupport Instance = new ValueTypeParseSupport();

            private static readonly Dictionary<Type, string> ValueTypes = new Dictionary<Type, string>() {
                { typeof(int), "parseInt" }, { typeof(uint), "parseInt" }, { typeof(double), "parseFloat" }, { typeof(decimal), "parseFloat" },
                { typeof(bool), "'true' == " }, { typeof(char), "" }, { typeof(long), "parseInt" }, { typeof(ulong), "parseInt" },
                { typeof(sbyte), "parseInt" },  { typeof(short), "parseInt" }, {typeof(ushort), "parseInt" }, { typeof(byte), "parseInt" }
            };

            private ValueTypeParseSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var methodCallExpression = context.Node as MethodCallExpression;
                var method = methodCallExpression?.Method;

                if (method == null || method.Name != "Parse" ||
                    !ValueTypes.TryGetValue(method.DeclaringType, out var expr))
                    return;

                context.PreventDefault();
                var writer = context.GetWriter();
                using (writer.Operation(methodCallExpression))
                {
                    writer.Write(expr);
                    writer.Write("(");
                    context.Visitor.Visit(methodCallExpression.Arguments[0]);
                    writer.Write(")");
                }
            }
        }

        public class ToStringSupport : JavascriptConversionExtension
        {
            public static readonly ToStringSupport Instance = new ToStringSupport();

            private ToStringSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is MethodCallExpression mce) ||
                    mce.Method.Name != "ToString")
                    return;

                context.PreventDefault();

                var writer = context.GetWriter();
                using (writer.Operation(mce))
                {
                    if (mce.Arguments.Count == 0)
                    {
                        WriterAction access = writer =>
                        {
                            writer.Write(".toString()");
                        };
                        WriteObjectPropertyAccess(context, context => context.Visitor.Visit(mce.Object), access);
                        return;
                    }

                    writer.Write("toStringWithFormat(");
                    context.Visitor.Visit(mce.Object);

                    foreach (var arg in mce.Arguments)
                    {
                        if (arg.Type == typeof(CultureInfo) &&
                            LinqPathProvider.GetValueFromExpressionWithoutConversion(arg, out var obj) &&
                            obj is CultureInfo culture)
                        {
                            if (culture.Name == string.Empty)
                                continue;
                            writer.Write(", ");
                            writer.Write($"'{culture.Name}'");
                        }
                        else
                        {
                            writer.Write(", ");
                            context.Visitor.Visit(arg);
                        }
                    }

                    writer.Write(")");
                }
            }
        }

        public class DateTimeSupport : JavascriptConversionExtension
        {
            public static DateTimeSupport Instance = new DateTimeSupport();

            private DateTimeSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (context.Node is NewExpression newExp && newExp.Type == typeof(DateTime))
                {
                    context.PreventDefault();
                    var writer = context.GetWriter();
                    using (writer.Operation(newExp))
                    {
                        writer.Write("new Date(Date.UTC(");

                        for (int i = 0; i < newExp.Arguments.Count; i++)
                        {
                            var value = ((ConstantExpression)newExp.Arguments[i]).Value;
                            if (i == 1)
                            {
                                var month = (int)value;
                                writer.Write(month - 1);
                            }
                            else
                            {
                                writer.Write(value);
                            }
                            if (i < newExp.Arguments.Count - 1)
                            {
                                writer.Write(", ");
                            }
                        }
                        writer.Write("))");
                    }

                    return;
                }

                if (context.Node is BinaryExpression binaryExpression &&
                    binaryExpression.Left.Type == typeof(DateTime))
                {
                    var writer = context.GetWriter();
                    context.PreventDefault();

                    using (writer.Operation(context.Node))
                    {
                        writer.Write("compareDates(");

                        context.Visitor.Visit(binaryExpression.Left);
                        writer.Write(", ");

                        context.Visitor.Visit(binaryExpression.Right);

                        if (context.Node.NodeType != ExpressionType.Subtract)
                        {
                            writer.Write($", '{context.Node.NodeType}'");
                        }

                        writer.Write(")");
                    }

                    return;
                }

                if (!(context.Node is MemberExpression node))
                    return;

                if (node.Type == typeof(DateTime) && node.Expression == null)
                {
                    var writer = context.GetWriter();
                    context.PreventDefault();

                    using (writer.Operation(node))
                    {
                        //match DateTime.Now , DateTime.UtcNow, DateTime.Today
                        switch (node.Member.Name)
                        {
                            case "MinValue":
                                writer.Write("new Date(-62135596800000)");
                                break;

                            case "MaxValue":
                                writer.Write("new Date(253402297199999)");
                                break;

                            case "Now":
                                writer.Write("new Date(Date.now())");
                                break;

                            case "UtcNow":
                                writer.Write(
                                    @"(function (date) { return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate(), date.getUTCHours(), date.getUTCMinutes(), date.getUTCSeconds(), date.getUTCMilliseconds()));})(new Date())");
                                break;

                            case "Today":
                                writer.Write("new Date(new Date().setUTCHours(0,0,0,0))");
                                break;
                        }
                    }

                    return;
                }

                if (node.Expression?.Type == typeof(DateTime) && node.Expression is MemberExpression memberExpression)
                {
                    var writer = context.GetWriter();
                    context.PreventDefault();

                    using (writer.Operation(node))
                    {
                        //match expressions like : DateTime.Today.Year , DateTime.Now.Day , user.Birthday.Month , etc

                        ContextAction obj2 = context =>
                        {
                            WriterAction access = null;
                            if (memberExpression.Expression.Type.IsNullableType() == false)
                            {
                                access = writer =>
                                {
                                    writer.Write($".{memberExpression.Member.Name}");
                                };
                            }

                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(memberExpression.Expression), access);
                        };

                        WrapperAction objWrapper2 = (context, obj) =>
                        {
                            writer.Write("new Date(");

                            if (memberExpression.Member.DeclaringType != typeof(DateTime))
                            {
                                writer.Write("Date.parse(");

                                obj2(context);
                                writer.Write(")");
                            }

                            writer.Write(")");
                        };

                        WriterAction access2 = writer =>
                        {
                            switch (node.Member.Name)
                            {
                                case "Year":
                                    writer.Write(IsUtc() ? ".getUTCFullYear()" : ".getFullYear()");
                                    break;

                                case "Month":
                                    writer.Write(IsUtc() ? ".getUTCMonth()+1" : ".getMonth()+1");
                                    break;

                                case "Day":
                                    writer.Write(IsUtc() ? ".getUTCDate()" : ".getDate()");
                                    break;

                                case "Hour":
                                    writer.Write(IsUtc() ? ".getUTCHours()" : ".getHours()");
                                    break;

                                case "Minute":
                                    writer.Write(IsUtc() ? ".getUTCMinutes()" : ".getMinutes()");
                                    break;

                                case "Second":
                                    writer.Write(IsUtc() ? ".getUTCSeconds()" : ".getSeconds()");
                                    break;

                                case "Millisecond":
                                    writer.Write(IsUtc() ? ".getUTCMilliseconds()" : ".getMilliseconds()");
                                    break;

                                case "Ticks":
                                    writer.Write(".getTime()*10000");
                                    break;
                            }
                        };

                        WriteObjectPropertyAccess(context, obj2, access2, objWrapper2);
                        
                        bool IsUtc()
                        {
                            return memberExpression.Member.Name == "UtcNow";
                        }
                    }
                }
            }
        }

        public class TimeSpanSupport : JavascriptConversionExtension
        {
            public static TimeSpanSupport Instance = new TimeSpanSupport();

            private TimeSpanSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is NewExpression newExp) || newExp.Type != typeof(TimeSpan))
                    return;

                context.PreventDefault();
                var writer = context.GetWriter();
                using (writer.Operation(newExp))
                {
                    writer.Write("convertToTimeSpanString(");

                    for (int i = 0; i < newExp.Arguments.Count; i++)
                    {
                        var expression = newExp.Arguments[i];
                        if (expression is ConstantExpression value)
                        {
                            writer.Write(value);
                        }
                        else
                        {
                            context.Visitor.Visit(expression);
                        }

                        if (i < newExp.Arguments.Count - 1)
                        {
                            writer.Write(", ");
                        }
                    }

                    writer.Write(")");
                }
            }
        }

        public class SubscriptionsWrappedConstantSupport : JavascriptConversionExtension
        {
            private readonly DocumentConventions _conventions;

            public SubscriptionsWrappedConstantSupport(DocumentConventions conventions)
            {
                _conventions = conventions;
            }

            private static bool IsWrappedConstantExpression(Expression expression)
            {
                while (expression is MemberExpression memberExpression)
                {
                    expression = memberExpression.Expression;
                }

                return expression is ConstantExpression;
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is MemberExpression memberExpression) ||
                    IsWrappedConstantExpression(memberExpression) == false)
                    return;

                LinqPathProvider.GetValueFromExpressionWithoutConversion(memberExpression, out var value);
                var writer = context.GetWriter();
                using (writer.Operation(JavascriptOperationTypes.Literal))
                {
                    if (value == null)
                    {
                        context.PreventDefault();
                        writer.Write("null");
                        return;
                    }

                    Type valueType = value.GetType();

                    if (memberExpression.Type == typeof(DateTime))
                    {
                        context.PreventDefault();
                        var dateTime = (DateTime)value;
                        writer.Write("new Date(Date.parse('");
                        writer.Write(dateTime.GetDefaultRavenFormat());
                        writer.Write("'))");
                        return;
                    }

                    // Type.IsEnum is unavailable in .netstandard1.3
                    if (valueType.IsEnum)
                    {
                        context.PreventDefault();
                        if (_conventions.SaveEnumsAsIntegers == false)
                        {
                            writer.Write("'");
                            writer.Write(value);
                            writer.Write("'");
                        }
                        else
                        {
                            writer.Write((int)value);
                        }

                        return;
                    }

                    if (valueType.IsArray || LinqMethodsSupport.IsCollection(valueType))
                    {
                        var arr = value as object[] ?? (value as IEnumerable<object>)?.ToArray();
                        if (arr == null)
                            return;

                        context.PreventDefault();

                        writer.Write("[");

                        for (var i = 0; i < arr.Length; i++)
                        {
                            if (i != 0)
                                writer.Write(", ");
                            if (arr[i] is string || arr[i] is char)
                            {
                                writer.Write("'");
                                writer.Write(arr[i]);
                                writer.Write("'");
                            }
                            else
                            {
                                writer.Write(arr[i]);
                            }
                        }
                        writer.Write("]");

                        return;
                    }

                    context.PreventDefault();

                    // if we have a number, write the value without quotation
                    if (_numericTypes.Contains(valueType) || _numericTypes.Contains(Nullable.GetUnderlyingType(valueType)))
                    {
                        writer.Write(value.ToInvariantString());
                    }
                    else
                    {
                        writer.Write("'");
                        writer.Write(value);
                        writer.Write("'");
                    }
                }
            }

            private static readonly HashSet<Type> _numericTypes = new HashSet<Type>
            {
                typeof(Byte),
                typeof(SByte),
                typeof(Int16),
                typeof(UInt16),
                typeof(Int32),
                typeof(UInt32),
                typeof(Int64),
                typeof(UInt64),
                typeof(double),
                typeof(decimal),
                typeof(System.Numerics.BigInteger),
            };
        }

        public class ConstSupport : JavascriptConversionExtension
        {
            private readonly DocumentConventions _conventions;

            public ConstSupport(DocumentConventions conventions)
            {
                _conventions = conventions;
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is ConstantExpression nodeAsConst))
                    return;

                var writer = context.GetWriter();
                using (writer.Operation(nodeAsConst))
                {
                    object nodeValue = nodeAsConst.Value;
                    Type nodeType = nodeAsConst.Type;

                    if (nodeValue == null)
                    {
                        context.PreventDefault();
                        writer.Write("null");
                        return;
                    }

                    // Type.IsEnum is unavailable in .netstandard1.3
                    if (nodeType.IsEnum
                        && _conventions.SaveEnumsAsIntegers == false)
                    {
                        context.PreventDefault();
                        writer.Write("'");
                        writer.Write(nodeValue);
                        writer.Write("'");
                    }

                    if (nodeType.IsArray || LinqMethodsSupport.IsCollection(nodeType))
                    {
                        var arr = nodeValue as object[] ?? (nodeValue as IEnumerable<object>)?.ToArray();
                        if (arr == null)
                            return;

                        context.PreventDefault();

                        writer.Write("[");

                        for (var i = 0; i < arr.Length; i++)
                        {
                            if (i != 0)
                                writer.Write(", ");
                            if (arr[i] is string || arr[i] is char)
                            {
                                writer.Write("'");
                                writer.Write(arr[i]);
                                writer.Write("'");
                            }
                            else
                            {
                                writer.Write(arr[i]);
                            }
                        }
                        writer.Write("]");
                    }
                }
            }
        }

        public class NullCoalescingSupport : JavascriptConversionExtension
        {
            public static NullCoalescingSupport Instance = new NullCoalescingSupport();

            private NullCoalescingSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (context.Node.NodeType != ExpressionType.Coalesce || !(context.Node is BinaryExpression binaryExpression))
                    return;

                context.PreventDefault();

                var test = Expression.NotEqual(
                        binaryExpression.Left,
                        Expression.Constant(null, binaryExpression.Left.Type)
                    );

                var condition = Expression.Condition(test,
                    binaryExpression.Left,
                    Expression.Convert(binaryExpression.Right, binaryExpression.Left.Type));

                var writer = context.GetWriter();
                writer.Write('(');
                context.Visitor.Visit(condition);
                writer.Write(')');
            }
        }

        public class ListInitSupport : JavascriptConversionExtension
        {
            public static readonly ListInitSupport Instance = new ListInitSupport();

            private ListInitSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (context.Node is ListInitExpression lie &&
                    LinqMethodsSupport.IsCollection(lie.Type) &&
                    LinqMethodsSupport.IsDictionary(lie.Type) == false)
                {
                    context.PreventDefault();
                    var writer = context.GetWriter();
                    using (writer.Operation(0))
                    {
                        writer.Write('[');

                        var posStart = writer.Length;
                        foreach (var init in lie.Initializers)
                        {
                            if (writer.Length > posStart)
                                writer.Write(',');

                            if (init.Arguments.Count != 1)
                                throw new Exception(
                                    "Arrays can only be initialized with methods that receive a single parameter for the value");

                            context.Visitor.Visit(init.Arguments[0]);
                        }

                        writer.Write(']');
                    }
                }
            }
        }

        public class ConstantSupport : JavascriptConversionExtension
        {
            public static readonly ConstantSupport Instance = new ConstantSupport();

            private ConstantSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is ConstantExpression ce))
                    return;

                string toWrite = null;
                if (ce.Type == typeof(DateTime))
                {
                    var dateTime = (DateTime)ce.Value;
                    toWrite = dateTime.GetDefaultRavenFormat(dateTime.Kind == DateTimeKind.Utc);
                }
                else if (ce.Type == typeof(DateTimeOffset))
                {
                    var dto = (DateTimeOffset)ce.Value;
                    toWrite = dto.ToString(DefaultFormat.DateTimeOffsetFormatsToWrite);
                }
                else if (ce.Type.IsEnum || ce.Type == typeof(Guid) || ce.Type == typeof(TimeSpan))
                {
                    toWrite = ce.Value.ToString();
                }

                if (toWrite == null)
                    return;

                context.PreventDefault();

                var writer = context.GetWriter();
                writer.Write("'");
                writer.Write(toWrite);
                writer.Write("'");
            }
        }

        public class NewSupport : JavascriptConversionExtension
        {
            public static readonly NewSupport Instance = new NewSupport();

            private NewSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (context.Node is NewExpression newExp)
                {
                    if (newExp.Members != null && newExp.Members.Count > 0)
                    {
                        context.PreventDefault();
                        var resultWriter = context.GetWriter();

                        using (resultWriter.Operation(0))
                        {
                            resultWriter.Write('{');

                            var posStart = resultWriter.Length;
                            for (int itMember = 0; itMember < newExp.Members.Count; itMember++)
                            {
                                var member = newExp.Members[itMember];

                                if (resultWriter.Length > posStart)
                                    resultWriter.Write(',');

                                string name = member.Name;
                                if (member.Name.StartsWith(TransparentIdentifier))
                                {
                                    name = name.Replace(TransparentIdentifier, DefaultAliasPrefix);
                                }

                                if (Regex.IsMatch(name, @"^\w[\d\w]*$"))
                                    resultWriter.Write(name);
                                else
                                    WriteStringLiteral(name, resultWriter);

                                resultWriter.Write(':');
                                context.Visitor.Visit(newExp.Arguments[itMember]);
                            }

                            resultWriter.Write('}');
                        }
                    }

                    if (LinqMethodsSupport.IsCollection(newExp.Type) &&
                        LinqMethodsSupport.IsDictionary(newExp.Type) == false)
                    {
                        var writer = context.GetWriter();
                        context.PreventDefault();

                        if (newExp.Arguments.Count > 0 &&
                            LinqMethodsSupport.IsCollection(newExp.Arguments[0].Type))
                        {
                            context.Visitor.Visit(newExp.Arguments);
                            return;
                        }

                        using (writer.Operation(newExp))
                        {
                            writer.Write("[]");
                        }

                        return;
                    }

                    if (newExp.Arguments.Count == 0)
                    {
                        var writer = context.GetWriter();
                        context.PreventDefault();

                        using (writer.Operation(newExp))
                        {
                            writer.Write("{}");
                        }

                        return;
                    }
                }

                if (context.Node is NewArrayExpression nae)
                {
                    var writer = context.GetWriter();
                    context.PreventDefault();

                    using (writer.Operation(nae))
                    {
                        writer.Write("[");

                        if (nae.Expressions.Count == 1 &&
                            nae.Expressions[0] is ConstantExpression val &&
                            val.Value is int asInt)
                        {
                            if (asInt != 0)
                            {
                                var elementsType = nae.Type.GetElementType();
                                var defaultVal = LinqMethodsSupport.GetDefault(elementsType);

                                if (defaultVal is bool)
                                {
                                    defaultVal = "false";
                                }
                                else if (defaultVal is char)
                                {
                                    defaultVal = '0';
                                }

                                for (var i = 0; i < asInt; i++)
                                {
                                    writer.Write(defaultVal ?? "null");
                                    if (i < asInt - 1)
                                        writer.Write(",");
                                }
                            }
                        }
                        else if (nae.Expressions.Count > 0)
                        {
                            for (var i = 0; i < nae.Expressions.Count; i++)
                            {
                                if (i != 0)
                                    writer.Write(",");
                                context.Visitor.Visit(nae.Expressions[i]);
                            }
                        }

                        writer.Write("]");
                    }

                    return;
                }

                if (context.Node is MethodCallExpression mce &&
                    mce.Method.DeclaringType == typeof(Array) &&
                    mce.Method.Name == "Empty")
                {
                    var writer = context.GetWriter();
                    context.PreventDefault();

                    using (writer.Operation(mce))
                    {
                        writer.Write("[]");
                    }
                }
            }

            private void WriteStringLiteral(string str, JavascriptWriter writer)
            {
                writer.Write('"');
                writer.Write(
                    str
                        .Replace("\\", "\\\\")
                        .Replace("\r", "\\r")
                        .Replace("\n", "\\n")
                        .Replace("\t", "\\t")
                        .Replace("\0", "\\0")
                        .Replace("\"", "\\\""));

                writer.Write('"');
            }
        }

        public class NullComparisonSupport : JavascriptConversionExtension
        {
            public static readonly NullComparisonSupport Instance = new NullComparisonSupport();

            private NullComparisonSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is BinaryExpression binaryExpression) ||
                    (binaryExpression.NodeType != ExpressionType.Equal &&
                     binaryExpression.NodeType != ExpressionType.NotEqual))
                    return;

                if (binaryExpression.Right is ConstantExpression right && right.Value == null)
                {
                    if (binaryExpression.Left is ConstantExpression leftConst && leftConst.Value == null)
                        return;

                    WriteExpression(context, binaryExpression.Left, binaryExpression.NodeType);
                    return;
                }

                if (binaryExpression.Left is ConstantExpression left && left.Value == null)
                {
                    WriteExpression(context, binaryExpression.Right, binaryExpression.NodeType);
                }
            }

            private static void WriteExpression(JavascriptConversionContext context, Expression expression, ExpressionType op)
            {
                var writer = context.GetWriter();
                context.PreventDefault();

                context.Visitor.Visit(expression);
                writer.Write(op == ExpressionType.Equal ? "==null" : "!=null");
            }
        }

        public class NestedConditionalSupport : JavascriptConversionExtension
        {
            public static readonly NestedConditionalSupport Instance = new NestedConditionalSupport();

            private NestedConditionalSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                var cond = context.Node as ConditionalExpression;
                if (cond?.IfTrue is ConditionalExpression || cond?.IfFalse is ConditionalExpression == false)
                    return;

                var writer = context.GetWriter();
                context.PreventDefault();

                using (writer.Operation(cond))
                {
                    context.Visitor.Visit(cond.Test);
                    writer.Write(" ? ");
                    context.Visitor.Visit(cond.IfTrue);
                    writer.Write(" : ");
                    context.Visitor.Visit(cond.IfFalse);
                }
            }
        }

        public class StringSupport : JavascriptConversionExtension
        {
            public static readonly StringSupport Instance = new StringSupport();

            private StringSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is MethodCallExpression mce) ||
                    mce.Method.DeclaringType != typeof(string))
                    return;

                string newName;
                switch (mce.Method.Name)
                {
                    case "PadLeft":
                        newName = "padStart";
                        break;

                    case "PadRight":
                        newName = "padEnd";
                        break;

                    case "Substring":
                        newName = "substr";
                        break;

                    case "StartsWith":
                        newName = "startsWith";
                        break;

                    case "EndsWith":
                        newName = "endsWith";
                        break;

                    case "Join":
                        newName = "join";
                        break;

                    case "Contains":
                        newName = "indexOf";
                        break;

                    case "Split":
                        newName = "split";
                        break;

                    case "Trim":
                        newName = "trim";
                        break;

                    case "ToUpper":
                        newName = "toUpperCase";
                        break;

                    case "ToLower":
                        newName = "toLowerCase";
                        break;

                    case "Replace":
                        newName = "replace";
                        break;

                    case "IsNullOrEmpty":
                        newName = "nullOrEmpty";
                        break;

                    case "IsNullOrWhiteSpace":
                        newName = "nullOrWhitespace";
                        break;

                    case "ToCharArray":
                        newName = "toCharArray";
                        break;

                    case "Format":
                        newName = "format";
                        break;

                    default:
                        return;
                }

                var writer = context.GetWriter();
                context.PreventDefault();

                var optChaining = ((PropertyNameConventionJSMetadataProvider)context.Options.CustomMetadataProvider).OptionalChanining;

                ContextAction obj;
                WriterAction access;
                using (writer.Operation(mce))
                {
                    switch (newName)
                    {
                        case "join":
                            if (mce.Arguments.Count > 2)
                            {
                                writer.Write("[");
                                WriteArguments(context, mce.Arguments, writer, 1);
                                writer.Write("]");
                            }

                            access = writer =>
                            {
                                writer.Write($".{newName}(");
                                context.Visitor.Visit(mce.Arguments[0]);
                                writer.Write($")");
                            };

                            if (mce.Arguments.Count > 2)
                                access(writer);
                            else
                                WriteObjectPropertyAccess(context, context => context.Visitor.Visit(mce.Arguments[1]), access);
                            break;

                        case "nullOrEmpty":
                            writer.Write("(");
                            context.Visitor.Visit(mce.Arguments[0]);
                            writer.Write(" == null || ");
                            context.Visitor.Visit(mce.Arguments[0]);
                            writer.Write(" === '')");
                            break;

                        case "nullOrWhitespace":
                            writer.Write("(!");
                            context.Visitor.Visit(mce.Arguments[0]);
                            writer.Write(" || !");
                            access = writer =>
                            {
                                writer.Write(".trim())");
                            };
                            
                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(mce.Arguments[0]), access);
                            break;

                        case "toCharArray":
                            access = writer =>
                            {
                                if (mce.Arguments.Count > 0)
                                {
                                    writer.Write(".substr(");
                                    context.Visitor.Visit(mce.Arguments[0]);
                                    writer.Write(", ");
                                    context.Visitor.Visit(mce.Arguments[1]);
                                    writer.Write($"){optChaining}");
                                }

                                writer.Write(".split('')");
                            };
                            
                            WriteObjectPropertyAccess(context, context => context.Visitor.Visit(mce.Object), access);
                            break;

                        case "format":
                            obj = context =>
                            {
                                context.Visitor.Visit(mce.Arguments[0]);
                            };
                            access = writer =>
                            {
                                writer.Write(".format(");
                                if (mce.Arguments.Count == 2 && mce.Arguments[1] is NewArrayExpression nae)
                                {
                                    WriteArguments(context, nae.Expressions, writer);
                                }
                                else
                                {
                                    WriteArguments(context, mce.Arguments, writer, 1);
                                }

                                writer.Write(")");
                            };
                            WriteObjectPropertyAccess(context, obj, access);
                            break;

                        default:
                            obj = context =>
                            {
                                context.Visitor.Visit(mce.Object);
                            };
                            access = writer =>
                            {
                                writer.Write($".{newName}(");

                                if (newName == "split")
                                {
                                    writer.Write("new RegExp(");
                                    if (mce.Arguments[0] is NewArrayExpression arrayExpression)
                                    {
                                        for (var i = 0; i < arrayExpression.Expressions.Count; i++)
                                        {
                                            if (i != 0)
                                            {
                                                writer.Write("+'|'+");
                                            }

                                            context.Visitor.Visit(arrayExpression.Expressions[i]);
                                        }
                                    }
                                    else if (mce.Arguments[0] is MethodCallExpression mce2)
                                    {
                                        var value = Expression.Lambda(mce2).Compile().DynamicInvoke();
                                        switch (value)
                                        {
                                            case string s:
                                                writer.WriteLiteral(s);
                                                break;

                                            case Array items:
                                                for (var i = 0; i < items.Length; i++)
                                                {
                                                    if (i != 0)
                                                    {
                                                        writer.Write("+'|'+");
                                                    }

                                                    var str = items.GetValue(i).ToInvariantString();

                                                    writer.WriteLiteral(str);
                                                }

                                                break;

                                            default:
                                                throw new InvalidOperationException("Unable to understand how to convert " + value + " to RQL (" + value?.GetType() ??
                                                                                    "null" + ")");
                                        }
                                    }
                                    else
                                    {
                                        context.Visitor.Visit(mce.Arguments[0]);
                                    }

                                    writer.Write(", 'g')");
                                }
                                else if (newName == "replace")
                                {
                                    writer.Write("new RegExp(");
                                    context.Visitor.Visit(mce.Arguments[0]);
                                    writer.Write(", 'g'), ");

                                    context.Visitor.Visit(mce.Arguments[1]);
                                }
                                else
                                {
                                    WriteArguments(context, mce.Arguments, writer);
                                }

                                writer.Write(")");
                            };
                            WriteObjectPropertyAccess(context, obj, access);

                            if (mce.Method.Name == "Contains")
                            {
                                writer.Write(" !== -1");
                            }
                            break;
                    }
                }
            }
        }

        public class MetadataSupport : JavascriptConversionExtension
        {
            public static readonly MetadataSupport Instance = new MetadataSupport();

            private MetadataSupport()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is MethodCallExpression methodCallExpression))
                    return;

                if (methodCallExpression.Method.DeclaringType != typeof(RavenQuery)
                    && methodCallExpression.Object?.Type != typeof(IAdvancedSessionOperations)
                    && methodCallExpression.Object?.Type != typeof(IAsyncAdvancedSessionOperations))
                    return;

                switch (methodCallExpression.Method.Name)
                {
                    case nameof(RavenQuery.LastModified):
                    case nameof(RavenQuery.Metadata):
                    case nameof(IAdvancedSessionOperations.GetMetadataFor):
                    {
                        var writer = context.GetWriter();
                        using (writer.Operation(methodCallExpression))
                        {
                            ContextAction obj = context =>
                            {
                                context.Visitor.Visit(methodCallExpression.Arguments[0]);
                            };

                            WrapperAction objWrapper = (context, obj) =>
                            {
                                writer.Write("getMetadata(");
                                obj(context);
                                writer.Write(")");
                            };

                            WriterAction access = methodCallExpression.Method.Name == nameof(RavenQuery.LastModified)
                                ? writer =>
                                    {
                                        writer.Write("['" + Constants.Documents.Metadata.LastModified + "']");
                                    }
                                : null;
                    
                            WriteObjectPropertyAccess(context, obj, access, objWrapper, addDot: true);
                        }
                        break;
                    }
                }
            }
        }

        public class IdentityPropertySupport : JavascriptConversionExtension
        {
            private readonly DocumentConventions _conventions;
            private readonly string _parameterName;

            public IdentityPropertySupport(DocumentConventions conventions, string parameterName = null)
            {
                _conventions = conventions;
                _parameterName = parameterName;
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (CanConvert(context.Node, _conventions, _parameterName, out var innerExpression) == false)
                    return;

                var writer = context.GetWriter();
                context.PreventDefault();

                using (writer.Operation(context.Node))
                {
                    writer.Write("id(");
                    if (innerExpression != null)
                    {
                        context.Visitor.Visit(innerExpression);
                    }

                    writer.Write(")");
                }
            }

            private static bool CanConvert(Expression expression, DocumentConventions conventions, string parameterName, out Expression innerExpression)
            {
                innerExpression = null;

                if (!(expression is MemberExpression member) ||
                    conventions.GetIdentityProperty(member.Member.DeclaringType) != member.Member)
                    return false;

                if (member.Expression is ParameterExpression parameter)
                {
                    innerExpression = parameter;
                    return true;
                }

                if (!(member.Expression is MemberExpression innerMember))
                    return false;

                innerExpression = innerMember;

                var p = GetParameter(innerMember)?.Name;
                return p != null && (p.StartsWith(TransparentIdentifier) || p == parameterName);
            }
        }

        public class MemberInit : JavascriptConversionExtension
        {
            public static MemberInit Instance { get; } = new MemberInit();

            private MemberInit()
            {
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (context.Node is MemberInitExpression == false)
                    return;

                MemberInitAsJson.ForAllTypes.ConvertToJavascript(context);
            }
        }

        internal class TimeSeriesSupport<T> : JavascriptConversionExtension
        {
            private readonly RavenQueryProviderProcessor<T> _queryProviderProcessor;

            public TimeSeriesSupport(RavenQueryProviderProcessor<T> queryProviderProcessor)
            {
                _queryProviderProcessor = queryProviderProcessor;
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is MethodCallExpression mce) ||
                    LinqPathProvider.IsTimeSeriesCall(mce) == false)
                    return;

                var script = _queryProviderProcessor.GenerateTimeSeriesScript(mce);
                var writer = context.GetWriter();
                context.PreventDefault();

                using (writer.Operation(mce))
                {
                    writer.Write(script);
                }
            }
        }

        public class TypedParameterSupport : JavascriptConversionExtension
        {
            public readonly string Name;

            public TypedParameterSupport(string name)
            {
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentNullException(nameof(name));

                Name = name;
            }

            public override void ConvertToJavascript(JavascriptConversionContext context)
            {
                if (!(context.Node is MemberExpression memberExpression) || 
                    !(memberExpression.Expression is ParameterExpression parameter) ||
                    parameter.Name != Name) 
                    return;
                
                context.PreventDefault();
                var writer = context.GetWriter();
                using (writer.Operation(memberExpression))
                {
                    writer.Write(memberExpression);
                }
            }
        }


        public static ParameterExpression GetParameter(MemberExpression expression)
        {
            return GetInnermostExpression(expression, out _, out _) as ParameterExpression;
        }

        private static ParameterExpression GetParameterAndCheckInternalMemberName(MemberExpression expression, out bool hasInternalKeyOrValue)
        {
            return GetInnermostExpression(expression, out _, out hasInternalKeyOrValue) as ParameterExpression;
        }

        public static Expression GetInnermostExpression(MemberExpression expression, out string path, out bool hasInternalKeyOrValue)
        {
            path = string.Empty;
            hasInternalKeyOrValue = false;
            while (expression.Expression is MemberExpression memberExpression)
            {
                if (expression.Member.Name.In("Value", "Key"))
                    hasInternalKeyOrValue = true;

                expression = memberExpression;
                path = path == string.Empty
                    ? expression.Member.Name
                    : $"{expression.Member.Name}.{path}";
            }

            return expression.Expression;
        }

        private static void WriteArguments(JavascriptConversionContext context, IReadOnlyList<Expression> arguments, JavascriptWriter writer, int start = 0)
        {
            for (var i = start; i < arguments.Count; i++)
            {
                if (i != start)
                {
                    writer.Write(", ");
                }

                context.Visitor.Visit(arguments[i]);
            }
        }

        internal static bool IsWrappedConstantExpression(Expression expression)
        {
            while (expression is MemberExpression memberExpression)
            {
                expression = memberExpression.Expression;
            }

            return expression is ConstantExpression;
        }
    }
}
