﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using V8.Net;
using Microsoft.CSharp.RuntimeBinder;
using Raven.Server.Extensions;
using Sparrow.Json;
using Raven.Server.Documents.Indexes.Static;

namespace Raven.Server.Documents.Indexes.Persistence.Lucene.Documents
{
    public delegate object DynamicGetter(object target);

    public class PropertyAccessor : IPropertyAccessor
    {
        private readonly Dictionary<string, Accessor> Properties = new Dictionary<string, Accessor>();

        private readonly List<KeyValuePair<string, Accessor>> _propertiesInOrder =
            new List<KeyValuePair<string, Accessor>>();

        public IEnumerable<(string Key, object Value, CompiledIndexField GroupByField, bool IsGroupByField)> GetPropertiesInOrder(object target)
        {
            foreach ((var key, var value) in _propertiesInOrder)
            {
                yield return (key, value.GetValue(target), value.GroupByField, value.IsGroupByField);
            }
        }

        public static IPropertyAccessor Create(Type type, object instance)
        {
            if (type == typeof(InternalHandle))
                return new JsPropertyAccessor(null);

            if (instance is Dictionary<string, object> dict)
                return DictionaryAccessor.Create(dict);

            return new PropertyAccessor(type);
        }

        public object GetValue(string name, object target)
        {
            if (Properties.TryGetValue(name, out Accessor accessor))
                return accessor.GetValue(target);

            throw new InvalidOperationException(string.Format("The {0} property was not found", name));
        }

        protected PropertyAccessor(Type type, Dictionary<string, CompiledIndexField> groupByFields = null)
        {
            var isValueType = type.IsValueType;
            foreach (var prop in type.GetProperties())
            {
                var getMethod = isValueType
                    ? (Accessor)CreateGetMethodForValueType(prop, type)
                    : CreateGetMethodForClass(prop, type);

                if (groupByFields != null)
                {
                    foreach (var groupByField in groupByFields.Values)
                    {
                        if (groupByField.IsMatch(prop.Name))
                        {
                            getMethod.GroupByField = groupByField;
                            getMethod.IsGroupByField = true;
                            break;
                        }
                    }
                }

                Properties.Add(prop.Name, getMethod);
                _propertiesInOrder.Add(new KeyValuePair<string, Accessor>(prop.Name, getMethod));
            }
        }

        private static ValueTypeAccessor CreateGetMethodForValueType(PropertyInfo prop, Type type)
        {
            var binder = Microsoft.CSharp.RuntimeBinder.Binder.GetMember(CSharpBinderFlags.None, prop.Name, type, new[] { CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null) });
            return new ValueTypeAccessor(CallSite<Func<CallSite, object, object>>.Create(binder));
        }

        private static ClassAccessor CreateGetMethodForClass(PropertyInfo propertyInfo, Type type)
        {
            var getMethod = propertyInfo.GetGetMethod();

            if (getMethod == null)
                throw new InvalidOperationException(string.Format("Could not retrieve GetMethod for the {0} property of {1} type", propertyInfo.Name, type.FullName));

            var arguments = new[]
            {
                typeof (object)
            };

            var getterMethod = new DynamicMethod(string.Concat("_Get", propertyInfo.Name, "_"), typeof(object), arguments, propertyInfo.DeclaringType);
            var generator = getterMethod.GetILGenerator();

            generator.DeclareLocal(typeof(object));
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Castclass, propertyInfo.DeclaringType);
            generator.EmitCall(OpCodes.Callvirt, getMethod, null);

            if (propertyInfo.PropertyType.IsClass == false)
                generator.Emit(OpCodes.Box, propertyInfo.PropertyType);

            generator.Emit(OpCodes.Ret);

            return new ClassAccessor((DynamicGetter)getterMethod.CreateDelegate(typeof(DynamicGetter)));
        }

        private class ValueTypeAccessor : Accessor
        {
            private readonly CallSite<Func<CallSite, object, object>> _callSite;

            public ValueTypeAccessor(CallSite<Func<CallSite, object, object>> callSite)
            {
                _callSite = callSite;
            }

            public override object GetValue(object target)
            {
                return _callSite.Target(_callSite, target);
            }
        }

        private class ClassAccessor : Accessor
        {
            private readonly DynamicGetter _dynamicGetter;

            public ClassAccessor(DynamicGetter dynamicGetter)
            {
                _dynamicGetter = dynamicGetter;
            }

            public override object GetValue(object target)
            {
                return _dynamicGetter(target);
            }
        }

        public abstract class Accessor
        {
            public abstract object GetValue(object target);

            public bool IsGroupByField;

            public CompiledIndexField GroupByField;
        }

        internal static IPropertyAccessor CreateMapReduceOutputAccessor(Type type, object instance, Dictionary<string, CompiledIndexField> groupByFields, bool isObjectInstance = false)
        {
            if (isObjectInstance || (type == typeof(InternalHandle) && instance is InternalHandle jsInstance && jsInstance.IsObject))
                return new JsPropertyAccessor(groupByFields);

            if (instance is Dictionary<string, object> dict)
                return DictionaryAccessor.Create(dict, groupByFields);

            return new PropertyAccessor(type, groupByFields);
        }
    }

    internal class JsPropertyAccessor : IPropertyAccessor
    {
        private readonly Dictionary<string, CompiledIndexField> _groupByFields;

        public JsPropertyAccessor(Dictionary<string, CompiledIndexField> groupByFields)
        {
            _groupByFields = groupByFields;
        }

        public IEnumerable<(string Key, object Value, CompiledIndexField GroupByField, bool IsGroupByField)> GetPropertiesInOrder(object target)
        {
            if (!(target is InternalHandle jsValue && jsValue.IsObject))
                throw new ArgumentException($"JsPropertyAccessor.GetPropertiesInOrder is expecting a target of type V8NativeObject but got one of type {target.GetType().Name}.");
            foreach (var (propertyName, jsPropertyValue) in jsValue.GetOwnProperties())
            {
                using (jsPropertyValue)
                {
                    //using (var jsStrRes = jsPropertyValue.Engine.JsonStringify.StaticCall(jsPropertyValue)) var strRes = jsStrRes.AsString;
                    CompiledIndexField field = null;
                    var isGroupByField = _groupByFields?.TryGetValue(propertyName, out field) ?? false;
                    var jsPropertyValueAux = jsPropertyValue;
                    yield return (propertyName, GetValue(ref jsPropertyValueAux), field, isGroupByField);
                }
            }
        }

        public object GetValue(string name, object target)
        {
            if (!(target is InternalHandle oi))
                throw new ArgumentException($"JsPropertyAccessor.GetValue is expecting a target of type InternalHandle but got one of type {target.GetType().Name}.");
            if (oi.HasOwnProperty(name))
                throw new MissingFieldException($"The target for 'JsPropertyAccessor.GetValue' doesn't contain the property {name}.");
            using (var jsValue = oi.GetProperty(name))
            {
                var jsValueAux = jsValue;
                return GetValue(ref jsValueAux);
            }
        }

        private static object GetValue(ref InternalHandle jsValue)
        {
            //using (var jsStrRes = jsValue.Engine.JsonStringify.StaticCall(jsValue)) var strRes = jsStrRes.AsString;
            if (jsValue.IsNull) {
                return null;
            }
            if (jsValue.IsStringEx())
                return jsValue.AsString;
            if (jsValue.IsBoolean)
                return jsValue.AsBoolean;
            if (jsValue.IsInt32)
                return jsValue.AsInt32;
            if (jsValue.IsNumberEx())
                return jsValue.AsDouble;
            if (jsValue.IsDate)
                return jsValue.AsDate;

            if (jsValue.IsArray)
            {
                int arrayLength =  jsValue.ArrayLength;
                var array = new object[arrayLength];
                for (int i = 0; i < arrayLength; i++)
                {
                    using (var jsItem = jsValue.GetProperty(i)) {
                        var jsItemAux = jsItem;
                        array[i] = GetValue(ref jsItemAux);
                    }
                }
                return array;
            }

            if (jsValue.IsObject)
            {
                var boundObject = jsValue.BoundObject;
                if (boundObject != null)
                {
                    switch (boundObject)
                    {
                        case LazyStringValue lsv:
                            return lsv;

                        case LazyCompressedStringValue lcsv:
                            return lcsv;

                        case LazyNumberValue lnv:
                            return lnv; //should be already blittable supported type.
                    }
                    //ThrowInvalidObject(ref jsValue);
                }
                return new InternalHandle(ref jsValue, true);
            }

            if (jsValue.IsUndefined)
            {
                return null;
            }

            ThrowInvalidObject(ref jsValue);
            return null;
        }

        private static void ThrowInvalidObject(ref InternalHandle jsValue)
        {
            throw new NotSupportedException($"Was requested to extract the value out of a InternalHandle object but could not figure its type, value={jsValue}");
        }
    }
}
