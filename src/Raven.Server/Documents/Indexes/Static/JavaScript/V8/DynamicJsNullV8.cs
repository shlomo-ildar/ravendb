using System;
using V8.Net;

namespace Raven.Server.Documents.Indexes.Static.JavaScript.V8
{
    public sealed class DynamicJsNullV8 : IEquatable<InternalHandle>, IEquatable<DynamicJsNullV8>
    {
        public readonly bool IsExplicitNull;
        private InternalHandle _handle;

        public DynamicJsNullV8(V8Engine engine, bool isExplicitNull) : base()
        {
            IsExplicitNull = isExplicitNull;
            _handle = engine.CreateNullValue();
        }

        ~DynamicJsNullV8()
        {
            _handle.Dispose();
        }

        public override string ToString()
        {
            return "null";
        }

        public InternalHandle CreateHandle()
        {
            return new InternalHandle(ref _handle, true);
        }

        public bool Equals(InternalHandle jsOther)
        {
            if (jsOther.IsNull)
                return _handle.Equals(jsOther);

            return false;
        }

        public bool Equals(DynamicJsNullV8 other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            return true; // isExplicitNull == other.isExplicitNull
        }
    }
}
