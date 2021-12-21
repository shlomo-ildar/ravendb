using JetBrains.Annotations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Raven.Client.Extensions
{
  internal static class TypeHelpers
  {
      public static bool TestAttribute<T>(this Type type, Func<T, bool> predicate) where T : Attribute
    {
      if (predicate == null)
        throw new ArgumentNullException(nameof (predicate));
      T obj = CustomAttributeExtensions.GetCustomAttributes(type.GetTypeInfo(), typeof (T), true).Cast<T>().SingleOrDefault<T>();
      return (object) obj != null && predicate(obj);
    }

    public static bool IsClosureRootType(this Type type) => type.Name.StartsWith("<>") && type.TestAttribute<CompilerGeneratedAttribute>((Func<CompilerGeneratedAttribute, bool>) (a => true));
  }
}
