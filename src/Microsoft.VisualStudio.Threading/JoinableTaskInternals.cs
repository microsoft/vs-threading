// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading;

using System;
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

    public static JoinableTaskToken GetJoinableTaskToken(JoinableTaskContext? joinableTaskContext)
    {
        return new JoinableTaskToken() { JoinableTaskReference = joinableTaskContext?.AmbientTask?.WeakSelf };
    }

    public static bool IsMainThreadMaybeBlocked(JoinableTaskToken joinableTaskToken)
    {
        if (joinableTaskToken.JoinableTaskReference is not null && joinableTaskToken.JoinableTaskReference.TryGetTarget(out JoinableTask? joinableTask))
        {
            if (joinableTask is object)
            {
                if ((joinableTask.State & JoinableTask.JoinableTaskFlags.CompleteFinalized) == JoinableTask.JoinableTaskFlags.CompleteFinalized)
                {
                    return false;
                }

                if ((joinableTask.State & JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread) == JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread)
                {
                    return true;
                }

                return JoinableTaskDependencyGraph.MaybeHasMainThreadSynchronousTaskWaiting(joinableTask);
            }
        }

        return false;
    }

    public struct JoinableTaskToken
    {
        internal WeakReference<JoinableTask>? JoinableTaskReference;
    }
}
