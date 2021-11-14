using System;
using System.Collections.Generic;
using V8.Net;

namespace Raven.Server.Documents.Patch.V8
{
    public class DictionaryDisposeKeyIHV8<TValue> : DictionaryDisposeKeyHandle<InternalHandle, TValue>
    {
    }

    public class DictionaryDisposeValueIHV8<TKey> : DictionaryDisposeValueHandle<TKey, InternalHandle>
    {
    }
}
