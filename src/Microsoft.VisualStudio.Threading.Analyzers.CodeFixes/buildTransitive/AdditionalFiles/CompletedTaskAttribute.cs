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
/// Apply this attribute to properties, methods, or fields that return cached, pre-completed tasks
/// such as singleton instances with well-known immutable values.
/// The VSTHRD003 analyzer will not report warnings when these members are awaited,
/// as awaiting an already-completed task does not pose a risk of deadlock.
/// </remarks>
[System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Method | System.AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
#pragma warning disable SA1649 // File name should match first type name
internal sealed class CompletedTaskAttribute : System.Attribute
{
}
#pragma warning restore SA1649 // File name should match first type name

#endif
