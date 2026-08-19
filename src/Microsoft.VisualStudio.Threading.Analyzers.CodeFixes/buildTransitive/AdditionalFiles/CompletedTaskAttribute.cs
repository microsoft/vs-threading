// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if !COMPLETEDTASKATTRIBUTE_INCLUDED
#define COMPLETEDTASKATTRIBUTE_INCLUDED

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// Indicates that a property, method, or field returns a task that is already completed.
/// This suppresses VSTHRD003 warnings when awaiting the returned task.
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to properties, methods, or fields that return cached, pre-completed tasks
/// such as singleton instances with well-known immutable values.
/// The VSTHRD003 analyzer will not report warnings when these members are awaited,
/// as awaiting an already-completed task does not pose a risk of deadlock.
/// </para>
/// <para>
/// This attribute can also be applied at the assembly level to mark members in external types
/// that you don't control:
/// <code>
/// [assembly: CompletedTask(Member = "System.Threading.Tasks.TplExtensions.TrueTask")]
/// </code>
/// </para>
/// </remarks>
[System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field | System.AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
#pragma warning disable SA1649 // File name should match first type name
internal sealed class CompletedTaskAttribute : System.Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CompletedTaskAttribute"/> class.
    /// </summary>
    public CompletedTaskAttribute()
    {
    }

    /// <summary>
    /// Gets or sets the fully qualified name of the member that returns a completed task.
    /// This is only used when the attribute is applied at the assembly level.
    /// </summary>
    /// <remarks>
    /// The format should be: "Namespace.TypeName.MemberName".
    /// For example: "System.Threading.Tasks.TplExtensions.TrueTask".
    /// </remarks>
    public string? Member { get; set; }
}
#pragma warning restore SA1649 // File name should match first type name

#endif
