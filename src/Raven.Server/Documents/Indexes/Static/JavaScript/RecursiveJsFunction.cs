using System;
using System.Collections.Generic;
using V8.Net;

namespace Raven.Server.Documents.Indexes.Static.JavaScript
{
    public class RecursiveJsFunction : IDisposable
    {
        private bool _disposed = false;

        private InternalHandle _result;
        private readonly V8Engine _engine;
        private readonly InternalHandle _item;
        private readonly InternalHandle _func;
        private readonly HashSet<InternalHandle> _results = new HashSet<InternalHandle>();
        private readonly Queue<object> _queue = new Queue<object>();

        public RecursiveJsFunction(V8Engine engine, ref InternalHandle item, ref InternalHandle func)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _item = item;

            if (func.IsUndefined || func.IsNull)
                throw new ArgumentNullException(nameof(func));
            _func = func;
        }

        ~RecursiveJsFunction()
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

            if (disposing) {
                GC.SuppressFinalize(this);
            }

            foreach (var res in _results) {
                res.Dispose();
            }
            _results.Clear();

            _disposed = true;
        }


        public InternalHandle Execute()
        {
            var jsArrEmpty = _engine.CreateArray(Array.Empty<InternalHandle>());
            _result.Set(ref jsArrEmpty);

            if (_item.IsUndefined)
                return _result;

            using (var jsRes = _func.StaticCall(_item))
            {
                jsRes.ThrowOnError(); // TODO check if is needed here
                var jsResAux = jsRes;
                var current = NullIfEmptyEnumerable(ref jsResAux);
                if (current == null)
                {
                    using (var jsResPush = _result.StaticCall("push", _item))
                        jsResPush.ThrowOnError(); // TODO check if is needed here
                    return _result;
                }

                _queue.Enqueue(_item);
                while (_queue.Count > 0)
                {
                    current = _queue.Dequeue();

                    var list = current as IEnumerable<InternalHandle>;
                    if (list != null)
                    {
                        foreach (InternalHandle jsItem in list) {
                            var jsItemAux = jsItem;
                            AddItem(ref jsItemAux);
                        }
                    }
                    else if (current is InternalHandle currentJs)
                        AddItem(ref currentJs);
                }
            }

            return _result;
        }

        private void AddItem(ref InternalHandle current)
        {
            if (_results.Add(current) == false)
                return;

            using (var jsResPush = _result.StaticCall("push", current))
                jsResPush.ThrowOnError(); // TODO check if is needed here

            using (var jsRes = _func.StaticCall(current))
            {
                jsRes.ThrowOnError(); // TODO check if is needed here
                var jsResAux = jsRes;
                var result = NullIfEmptyEnumerable(ref jsResAux);
                if (result != null)
                    _queue.Enqueue(result);
            }
        }

        private static object NullIfEmptyEnumerable(ref InternalHandle item)
        {
            if (item.IsArray == false) {
                return /*new InternalHandle(ref */item; //, true);
            }

            //using (item)
            {
                if (item.ArrayLength == 0)
                    return null;

                return Yield(item);
            }
        }

        private static IEnumerable<InternalHandle> Yield(InternalHandle jsArray)
        {
            int arrayLength =  jsArray.ArrayLength;
            for (int i = 0; i < arrayLength; ++i)
                yield return jsArray.GetProperty(i);
        }
    }
}
