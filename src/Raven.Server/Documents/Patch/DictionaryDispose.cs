using System;
using System.Collections.Generic;
using V8.Net;

namespace Raven.Server.Documents.Patch
{
    public class DictionaryDisposeKeyJH<TValue> : DictionaryDisposeKeyHandle<JsHandle, TValue>
    {
    }

    public class DictionaryDisposeValueJH<TKey> : DictionaryDisposeValueHandle<TKey, JsHandle>
    {
    }

    public class DictionaryDisposeKeyHandle<THandle, TValue> : DictionaryDisposeKey<THandle, TValue>
    where THandle: IDisposable, IClonable<THandle>
    {
        public DictionaryDisposeKeyHandle() : base()
        {
        }

        public void Add(ref THandle jsKey, TValue value)
        {
            var jsKeyNew = jsKey.Clone();
            try
            {
                base.Add(jsKeyNew, value);
            }
            catch 
            {
                jsKeyNew.Dispose();
                throw;
            }
        }

        public bool TryAdd(ref THandle jsKey, TValue value)
        {
            var jsKeyNew = jsKey.Clone();
            var res = base.TryAdd(jsKeyNew, value);
            if (!res)
            {
                jsKeyNew.Dispose();
            }
            return res;
        }

    }

    public class DictionaryDisposeValueHandle<TKey, THandle> : DictionaryDisposeValue<TKey, THandle>
        where THandle: IDisposable, IClonable<THandle>
    {
        public DictionaryDisposeValueHandle() : base()
        {
        }

        public void Add(TKey key, ref THandle jsValue)
        {
            var jsValueNew = jsValue.Clone();
            try
            {
                base.Add(key, jsValueNew);
            }
            catch 
            {
                jsValueNew.Dispose();
                throw;
            }
        }

        public bool TryAdd(TKey key, ref THandle jsValue)
        {
            var jsValueNew = jsValue.Clone();
            var res = base.TryAdd(key, jsValueNew);
            if (!res)
            {
                jsValueNew.Dispose();
            }
            return res;
        }

        new public bool TryGetValue(TKey key, out THandle jsValue)
        {
            var res = base.TryGetValue(key, out jsValue);
            if (res)
                jsValue = jsValue.Clone();
            return res;
        }

    }

    public class DictionaryDisposeKey<TKey, TValue> : Dictionary<TKey, TValue>, IDisposable
    where TKey : IDisposable
    {
        private bool _disposed = false;

        public DictionaryDisposeKey() : base()
        {
        }

        ~DictionaryDisposeKey()
        {
            Dispose(false);
        }

        public void Dispose()
        {  
            Dispose(true);
        }

        protected void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            Clear();

            _disposed = true;
        }

        new public void Clear()
        {
            foreach (var kvp in this)
                kvp.Key.Dispose();
            base.Clear();
        }


        new public bool Remove(TKey key)
        {
            var res = base.Remove(key);
            if (res)
                key.Dispose();
            return res;
        }
        new public bool Remove(TKey key, out TValue value)
        {
            var res = base.Remove(key, out value);
            if (res)
                key.Dispose();
            return res;
        }
    }

    public class DictionaryDisposeValue<TKey, TValue> : Dictionary<TKey, TValue>, IDisposable
    where TValue : IDisposable
    {
        private bool _disposed = false;

        public DictionaryDisposeValue() : base()
        {
        }

        ~DictionaryDisposeValue()
        {
            Dispose(false);
        }

        public void Dispose()
        {  
            Dispose(true);
        }

        protected void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            Clear();

            _disposed = true;
        }

        new public void Clear()
        {
            foreach (var kvp in this)
                kvp.Value.Dispose();
            base.Clear();
        }

        new public bool Remove(TKey key)
        {
            var res = base.Remove(key, out TValue value);
            if (res)
                value.Dispose();
            return res;
        }
    }

}
