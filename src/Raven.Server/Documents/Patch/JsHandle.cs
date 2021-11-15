using System;
using System.Runtime.InteropServices;  
using System.Collections.Generic;
using JetBrains.Annotations;
using Jint.Native;
using Jint.Native.Object;
using Raven.Client.ServerWide.JavaScript;
using V8.Net;

namespace Raven.Server.Documents.Patch
{
    public struct JsHandleJintError
    {
        public string Message;
        public JSValueType ErrorType;

        public JsHandleJintError(string message, JSValueType errorType)
        {
            Message = message;
            ErrorType = errorType;
        }

        public void Throw()
        {
            throw new Exception(Message);
        }

        public JsHandle Set(JsHandle value)
        {
            Message = value.JintError.Message;
            ErrorType = value.JintError.ErrorType;
            return new JsHandle(Message, ErrorType);
        }
    }
    
    
    [StructLayout(LayoutKind.Explicit)]
    public struct JsHandle : IJsHandle<JsHandle>
    {

        public static JsHandle Empty(JavaScriptEngineType engineType) => new JsHandle(engineType);
        
        [FieldOffset(0)]
        public JsHandleType Type;

        [FieldOffset(1)]
        public JsHandleV8 V8;

        [FieldOffset(1)]
        public JsHandleJint Jint;

        [FieldOffset(1)]
        public JsHandleJintError JintError;


        public JsHandle(JavaScriptEngineType engineType)
        {
            switch (engineType)
            {
                case JavaScriptEngineType.V8:
                    Type = JsHandleType.V8;
                    Jint = default;
                    JintError = default;
                    var h = InternalHandle.Empty;
                    V8 = new JsHandleV8(ref h);
                    break;
                case JavaScriptEngineType.Jint:
                    Type = JsHandleType.Jint;
                    V8 = default;
                    JintError = default;
                    Jint = new JsHandleJint(null);
                    break;
                default:
                    throw new NotSupportedException($"Not supported JavaScriptEngineType '{engineType}'.");
            }
        }
            
        public JsHandle(ref JsHandle value)
        {
            Type = value.Type;
            switch (Type)
            {
                case JsHandleType.V8:
                    Jint = default;
                    JintError = default;
                    var h = value.IsEmpty ? InternalHandle.Empty : value.V8.Item.Clone();
                    V8 = new JsHandleV8(ref h);
                    break;
                case JsHandleType.Jint:
                    V8 = default;
                    JintError = default;
                    Jint = new JsHandleJint(value.Jint.Item);
                    break;
                case JsHandleType.JintError:
                    V8 = default;
                    Jint = default;
                    var e = value.JintError;
                    JintError = new JsHandleJintError(e.Message, e.ErrorType);
                    break;
                default:
                    throw new NotSupportedException($"Not supported JsHandleType '{value.Type}'.");
            }    
        }

        public JsHandle(InternalHandle value)
        {
            Type = JsHandleType.V8;
            Jint = default;
            JintError = default;
            V8 = new JsHandleV8(ref value);
        }

        public JsHandle(JsValue value)
        {
            Type = JsHandleType.Jint;
            V8 = default;
            JintError = default;
            Jint = new JsHandleJint(value);
        }

        public JsHandle(ObjectInstance value)
        {
            Type = JsHandleType.Jint;
            V8 = default;
            JintError = default;
            Jint = new JsHandleJint(value);
        }

        public JsHandle(string message, JSValueType errorType)
        {
            Type = JsHandleType.JintError;
            V8 = default;
            Jint = default;
            JintError = new JsHandleJintError(message, errorType);
        }

        public void Dispose()
        {
            switch (Type)
            {
                case JsHandleType.V8:
                    V8.Dispose();
                    break;
                case JsHandleType.Jint:
                    Jint.Dispose();
                    break;
                case JsHandleType.JintError:
                    break;
                default:
                    throw new NotSupportedException($"Not supported JsHandleType '{Type}'.");
            }
        }
        
        public JsHandle Clone()
        {
            return new JsHandle(ref this);
        }

        public JsHandle Set(JsHandle value)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.Set(value),
                JsHandleType.Jint => Jint.Set(value),
                JsHandleType.JintError => JintError.Set(value),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }
        
        public static JsHandle[] FromArray(JsValue[] items)
        {
            int arrayLength = items.Length;
            var itemsJs = new JsHandle[arrayLength];
            for (var i = 0; i < items.Length; i++)
            {
                itemsJs[i] = new JsHandle(items[i]);
            }

            return itemsJs;
        }

        public static JsHandle[] FromArray(InternalHandle[] items)
        {
            int arrayLength = items.Length;
            var itemsJs = new JsHandle[arrayLength];
            for (var i = 0; i < items.Length; i++)
            {
                itemsJs[i] = new JsHandle(items[i]);
            }

            return itemsJs;
        }
        
        public JavaScriptEngineType EngineType
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => JavaScriptEngineType.V8,
                    JsHandleType.Jint => JavaScriptEngineType.Jint,
                    JsHandleType.JintError => JavaScriptEngineType.Jint,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }
        
        public IJsHandle<JsHandle> Handler
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8,
                    JsHandleType.Jint => Jint,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }
        
        public object Object
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.Object,
                    JsHandleType.Jint => Jint.Object,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool HasObject
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.HasObject,
                    JsHandleType.Jint => Jint.HasObject,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsEmpty
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsEmpty,
                    JsHandleType.Jint => Jint.IsEmpty,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsUndefined
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsUndefined,
                    JsHandleType.Jint => Jint.IsUndefined,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsNull
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsNull,
                    JsHandleType.Jint => Jint.IsNull,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }


        public bool IsNumberEx
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsNumberEx,
                    JsHandleType.Jint => Jint.IsNumberEx,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsNumberOrIntEx
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsNumberOrIntEx,
                    JsHandleType.Jint => Jint.IsNumberOrIntEx,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsStringEx
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsStringEx,
                    JsHandleType.Jint => Jint.IsStringEx,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsBoolean
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsBoolean,
                    JsHandleType.Jint => Jint.IsBoolean,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsInt32
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsInt32,
                    JsHandleType.Jint => Jint.IsInt32,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsNumber
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsNumber,
                    JsHandleType.Jint => Jint.IsNumber,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsString
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsString,
                    JsHandleType.Jint => Jint.IsString,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsObject
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsObject,
                    JsHandleType.Jint => Jint.IsObject,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsBinder
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsBinder,
                    JsHandleType.Jint => Jint.IsBinder,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsFunction
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsFunction,
                    JsHandleType.Jint => Jint.IsFunction,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsDate
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsDate,
                    JsHandleType.Jint => Jint.IsDate,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsArray
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsArray,
                    JsHandleType.Jint => Jint.IsArray,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsRegExp
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsRegExp,
                    JsHandleType.Jint => Jint.IsRegExp,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsObjectType
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsObjectType,
                    JsHandleType.Jint => Jint.IsObjectType,
                    JsHandleType.JintError => false,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool IsError
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.IsError,
                    JsHandleType.Jint => Jint.IsError,
                    JsHandleType.JintError => true,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public bool AsBoolean
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.AsBoolean,
                    JsHandleType.Jint => Jint.AsBoolean,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public int AsInt32
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.AsInt32,
                    JsHandleType.Jint => Jint.AsInt32,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public double AsDouble
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.AsDouble,
                    JsHandleType.Jint => Jint.AsDouble,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public string AsString
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.AsString,
                    JsHandleType.Jint => Jint.AsString,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public DateTime AsDate
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.AsDate,
                    JsHandleType.Jint => Jint.AsDate,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        
        public JSValueType ValueType
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.ValueType,
                    JsHandleType.Jint => Jint.ValueType,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public object BoundObject
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.BoundObject,
                    JsHandleType.Jint => Jint.BoundObject,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }

        public uint ArrayLength
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.ArrayLength,
                    JsHandleType.Jint => Jint.ArrayLength,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }
        
        public IJsEngineHandle Engine
        {
            get
            {
                return Type switch
                {
                    JsHandleType.V8 => V8.Engine,
                    JsHandleType.Jint => Jint.Engine,
                    _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
                };
            }
        }
        
        public void ThrowOnError()
        {
            switch (Type)
            {
                case JsHandleType.V8:
                    V8.ThrowOnError();
                    break;
                case JsHandleType.Jint:
                    Jint.ThrowOnError();
                    break;
                case JsHandleType.JintError:
                    JintError.Throw();
                    break;
                default:
                    throw new NotSupportedException($"Not supported JsHandleType '{Type}'.");
            }
        }

        public bool HasOwnProperty(string name)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.HasOwnProperty(name),
                JsHandleType.Jint => Jint.HasOwnProperty(name),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public bool HasProperty(string name)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.HasProperty(name),
                JsHandleType.Jint => Jint.HasProperty(name),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public void FastAddProperty(string name, JsHandle value, bool writable, bool enumerable, bool configurable)
        {
            switch (Type)
            {
                case JsHandleType.V8:
                    V8.FastAddProperty(name, value, writable, enumerable, configurable);
                    break;
                case JsHandleType.Jint:
                    Jint.FastAddProperty(name, value, writable, enumerable, configurable);
                    break;
                default:
                    throw new NotSupportedException($"Not supported JsHandleType '{Type}'.");
            }
        }       
        
        public bool SetProperty(string name, JsHandle value, bool throwOnError = false)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.SetProperty(name, value),
                JsHandleType.Jint => Jint.SetProperty(name, value),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public bool SetProperty(int index, JsHandle value, bool throwOnError = false)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.SetProperty(index, value, throwOnError),
                JsHandleType.Jint => Jint.SetProperty(index, value, throwOnError),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public bool TryGetValue(string propertyName, out JsHandle value)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.TryGetValue(propertyName, out value),
                JsHandleType.Jint => Jint.TryGetValue(propertyName, out value),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public JsHandle GetOwnProperty(string name)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.GetOwnProperty(name),
                JsHandleType.Jint => Jint.GetOwnProperty(name),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public JsHandle GetOwnProperty(Int32 index)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.GetOwnProperty(index),
                JsHandleType.Jint => Jint.GetOwnProperty(index),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public JsHandle GetProperty(string name)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.GetProperty(name),
                JsHandleType.Jint => Jint.GetProperty(name),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public JsHandle GetProperty(int index)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.GetProperty(index),
                JsHandleType.Jint => Jint.GetProperty(index),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public bool DeleteProperty(string name, bool throwOnError = false)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.DeleteProperty(name, throwOnError),
                JsHandleType.Jint => Jint.DeleteProperty(name, throwOnError),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public bool DeleteProperty(int index, bool throwOnError = false)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.DeleteProperty(index, throwOnError),
                JsHandleType.Jint => Jint.DeleteProperty(index, throwOnError),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public string[] GetPropertyNames()
        {
            return Type switch
            {
                JsHandleType.V8 => V8.GetPropertyNames(),
                JsHandleType.Jint => Jint.GetPropertyNames(),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public string[] GetOwnPropertyNames()
        {
            return Type switch
            {
                JsHandleType.V8 => V8.GetPropertyNames(),
                JsHandleType.Jint => Jint.GetPropertyNames(),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public IEnumerable<KeyValuePair<string, JsHandle>> GetOwnProperties()
        {
            return Type switch
            {
                JsHandleType.V8 => V8.GetOwnProperties(),
                JsHandleType.Jint => Jint.GetOwnProperties(),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }
        
        public IEnumerable<KeyValuePair<string, JsHandle>> GetProperties()
        {
            return Type switch
            {
                JsHandleType.V8 => V8.GetProperties(),
                JsHandleType.Jint => Jint.GetProperties(),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public JsHandle Call(string functionName, JsHandle _this, params JsHandle[] args)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.Call(functionName, _this, args),
                JsHandleType.Jint => Jint.Call(functionName, _this, args),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public JsHandle StaticCall(string functionName, params JsHandle[] args)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.StaticCall(functionName, args),
                JsHandleType.Jint => Jint.StaticCall(functionName, args),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        public JsHandle Call(JsHandle _this, params JsHandle[] args)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.Call(_this, args),
                JsHandleType.Jint => Jint.Call(_this, args),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }

        [Pure]
        public JsHandle StaticCall(params JsHandle[] args)
        {
            return Type switch
            {
                JsHandleType.V8 => V8.StaticCall(args),
                JsHandleType.Jint => Jint.StaticCall(args),
                _ => throw new NotSupportedException($"Not supported JsHandleType '{Type}'.")
            };
        }
    }
}
