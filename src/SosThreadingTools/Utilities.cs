// Copyright (c) Microsoft Corporation. All rights reserved.

namespace CpsDbg
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Diagnostics.Runtime;

    internal static class Utilities
    {
        internal static ClrObject TryGetObjectField(this ClrObject clrObject, string fieldName)
        {
            if (!clrObject.IsNull)
            {
                var field = clrObject.Type.GetFieldByName(fieldName);
                if (field != null && field.IsObjectReference)
                {
                    ulong address = field.GetAddress(clrObject.Address);
                    ulong reference;
                    if (clrObject.Type.Heap.ReadPointer(address, out reference) && reference != 0)
                    {
                        return new ClrObject(reference, clrObject.Type.Heap.GetObjectType(reference));
                    }
                }
            }

            return default(ClrObject);
        }

        internal static ClrValueClass? TryGetValueClassField(this ClrObject clrObject, string fieldName)
        {
            if (!clrObject.IsNull)
            {
                var field = clrObject.Type.GetFieldByName(fieldName);
                if (field != null && field.Type.IsValueClass)
                {
                    // System.Console.WriteLine("{0} {1:x} Field {2} {3} {4} {5}", clrObject.Type.Name, clrObject.Address, fieldName, field.Type.Name, field.Type.IsValueClass, field.Type.IsRuntimeType);
                    return clrObject.GetValueClassField(fieldName);
                }
            }

            return null;
        }

        internal static ClrObject TryGetObjectField(this ClrValueClass? clrObject, string fieldName)
        {
            if (clrObject != null)
            {
                var field = clrObject.Value.Type.GetFieldByName(fieldName);
                if (field != null && field.IsObjectReference)
                {
                    return clrObject.Value.GetObjectField(fieldName);
                }
            }

            return default(ClrObject);
        }

        internal static ClrValueClass? TryGetValueClassField(this ClrValueClass? clrObject, string fieldName)
        {
            if (clrObject.HasValue)
            {
                var field = clrObject.Value.Type.GetFieldByName(fieldName);
                if (field != null && field.IsValueClass)
                {
                    return clrObject.Value.GetValueClassField(fieldName);
                }
            }

            return null;
        }

        internal static IEnumerable<ClrObject> GetObjectsOfType(this ClrHeap heap, string typeName)
        {
            return heap.EnumerateObjects().Where(obj => string.Equals(obj.Type?.Name, typeName, StringComparison.Ordinal));
        }
    }
}
