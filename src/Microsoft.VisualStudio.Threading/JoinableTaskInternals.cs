// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading;

using System.ComponentModel;

#pragma warning disable RS0016 // Add public types and members to the declared API
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

/// <summary>
/// A helper class for integration with Visual Studio.
/// APIs in this file are intended for Microsoft internal use only
/// and are subject to change without notice.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class JoinableTaskInternals
{
    public static bool IsMainThreadBlockedByAnyJoinableTask(JoinableTaskContext? joinableTaskContext)
    {
        return joinableTaskContext?.IsMainThreadBlockedByAnyJoinableTask == true;
    }
}
