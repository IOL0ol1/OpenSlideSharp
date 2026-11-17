using System;
using System.Diagnostics;

namespace OpenSlideSharp.Native;

/// <summary>Records the native C type name of a generated binding member.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = true)]
[Conditional("DEBUG")]
internal sealed class NativeTypeNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
