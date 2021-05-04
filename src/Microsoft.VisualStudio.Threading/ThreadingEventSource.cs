// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Diagnostics.Tracing;

    /// <summary>
    /// The ETW source for logging events for this library.
    /// </summary>
    /// <remarks>
    /// We use a fully-descriptive type name because the type name becomes the name
    /// of the ETW Provider.
    /// </remarks>
    [EventSource(Name = "Microsoft-VisualStudio-Threading")]
    internal sealed partial class ThreadingEventSource : EventSource
    {
        /// <summary>
        /// The singleton instance used for logging.
        /// </summary>
        internal static readonly ThreadingEventSource Instance = new ThreadingEventSource();

        /// <summary>
        /// The event ID for the <see cref="ReaderWriterLockIssued(int, AsyncReaderWriterLock.LockKind, int, int)"/> event.
        /// </summary>
        private const int ReaderWriterLockIssuedLockCountsEvent = 1;

        /// <summary>
        /// The event ID for the <see cref="WaitReaderWriterLockStart(int, AsyncReaderWriterLock.LockKind, int, int, int)"/> event.
        /// </summary>
        private const int WaitReaderWriterLockStartEvent = 2;

        /// <summary>
        /// The event ID for the <see cref="WaitReaderWriterLockStop(int, AsyncReaderWriterLock.LockKind)"/> event.
        /// </summary>
        private const int WaitReaderWriterLockStopEvent = 3;

        /// <summary>
        /// The event ID for the <see cref="CompleteOnCurrentThreadStart(int, bool)"/>.
        /// </summary>
        private const int CompleteOnCurrentThreadStartEvent = 11;

        /// <summary>
        /// The event ID for the <see cref="CompleteOnCurrentThreadStop(int)"/>.
        /// </summary>
        private const int CompleteOnCurrentThreadStopEvent = 12;

        /// <summary>
        /// The event ID for the <see cref="WaitSynchronouslyStart()"/>.
        /// </summary>
        private const int WaitSynchronouslyStartEvent = 13;

        /// <summary>
        /// The event ID for the <see cref="WaitSynchronouslyStop()"/>.
        /// </summary>
        private const int WaitSynchronouslyStopEvent = 14;

        /// <summary>
        /// The event ID for the <see cref="PostExecutionStart(int, bool)"/>.
        /// </summary>
        private const int PostExecutionStartEvent = 15;

        /// <summary>
        /// The event ID for the <see cref="PostExecutionStop(int)"/>.
        /// </summary>
        private const int PostExecutionStopEvent = 16;

        /// <summary>
        /// The event ID for the <see cref="CircularJoinableTaskDependencyDetected(int, int)"/>.
        /// </summary>
        private const int CircularJoinableTaskDependencyDetectedEvent = 17;

        /// <summary>
        /// Logs an issued lock.
        /// </summary>
        [Event(ReaderWriterLockIssuedLockCountsEvent, Task = Tasks.LockRequest, Opcode = Opcodes.ReaderWriterLockIssued)]
        public void ReaderWriterLockIssued(int lockId, AsyncReaderWriterLock.LockKind kind, int issuedUpgradeableReadCount, int issuedReadCount)
        {
            this.WriteEvent(ReaderWriterLockIssuedLockCountsEvent, lockId, kind, issuedUpgradeableReadCount, issuedReadCount);
        }

        /// <summary>
        /// Logs a wait for a lock.
        /// </summary>
        [Event(WaitReaderWriterLockStartEvent, Task = Tasks.LockRequestContention, Opcode = EventOpcode.Start)]
        public void WaitReaderWriterLockStart(int lockId, AsyncReaderWriterLock.LockKind kind, int issuedWriteCount, int issuedUpgradeableReadCount, int issuedReadCount)
        {
            this.WriteEvent(WaitReaderWriterLockStartEvent, lockId, kind, issuedWriteCount, issuedUpgradeableReadCount, issuedReadCount);
        }

        /// <summary>
        /// Logs a lock that was issued after a contending lock was released.
        /// </summary>
        [Event(WaitReaderWriterLockStopEvent, Task = Tasks.LockRequestContention, Opcode = EventOpcode.Stop)]
        public void WaitReaderWriterLockStop(int lockId, AsyncReaderWriterLock.LockKind kind)
        {
            this.WriteEvent(WaitReaderWriterLockStopEvent, lockId, kind);
        }

        /// <summary>
        /// Enters a synchronously task.
        /// </summary>
        /// <param name="taskId">Hash code of the task.</param>
        /// <param name="isOnMainThread">Whether the task is on the main thread.</param>
        [Event(CompleteOnCurrentThreadStartEvent)]
        public void CompleteOnCurrentThreadStart(int taskId, bool isOnMainThread)
        {
            this.WriteEvent(CompleteOnCurrentThreadStartEvent, taskId, isOnMainThread);
        }

        /// <summary>
        /// Exits a synchronously task.
        /// </summary>
        /// <param name="taskId">Hash code of the task.</param>
        [Event(CompleteOnCurrentThreadStopEvent)]
        public void CompleteOnCurrentThreadStop(int taskId)
        {
            this.WriteEvent(CompleteOnCurrentThreadStopEvent, taskId);
        }

        /// <summary>
        /// The current thread starts to wait on execution requests.
        /// </summary>
        [Event(WaitSynchronouslyStartEvent, Level = EventLevel.Verbose)]
        public void WaitSynchronouslyStart()
        {
            this.WriteEvent(WaitSynchronouslyStartEvent);
        }

        /// <summary>
        /// The current thread gets an execution request.
        /// </summary>
        [Event(WaitSynchronouslyStopEvent, Level = EventLevel.Verbose)]
        public void WaitSynchronouslyStop()
        {
            this.WriteEvent(WaitSynchronouslyStopEvent);
        }

        /// <summary>
        /// Post a execution request to the queue.
        /// </summary>
        /// <param name="requestId">The request id.</param>
        /// <param name="mainThreadAffinitized">The execution need happen on the main thread.</param>
        [Event(PostExecutionStartEvent, Level = EventLevel.Verbose)]
        public void PostExecutionStart(int requestId, bool mainThreadAffinitized)
        {
            this.WriteEvent(PostExecutionStartEvent, requestId, mainThreadAffinitized);
        }

        /// <summary>
        /// An execution request is processed.
        /// </summary>
        /// <param name="requestId">The request id.</param>
        [Event(PostExecutionStopEvent, Level = EventLevel.Verbose)]
        public void PostExecutionStop(int requestId)
        {
            this.WriteEvent(PostExecutionStopEvent, requestId);
        }

        /// <summary>
        /// Circular JoinableTask dependency detected.
        /// </summary>
        /// <param name="initUnreachableCount">Initial count of unreachable nodes.</param>
        /// <param name="reachableCount">The size of the connected dependency graph.</param>
        [Event(CircularJoinableTaskDependencyDetectedEvent, Level = EventLevel.Informational)]
        public void CircularJoinableTaskDependencyDetected(int initUnreachableCount, int reachableCount)
        {
            this.WriteEvent(CircularJoinableTaskDependencyDetectedEvent, initUnreachableCount, reachableCount);
        }

        /// <summary>
        /// The names of constants in this class make up the middle term in
        /// the AsyncReaderWriterLock/LockRequest/Issued event name.
        /// </summary>
        /// <remarks>The name of this class is important for EventSource.</remarks>
        public static class Tasks
        {
            public const EventTask LockRequest = (EventTask)1;
            public const EventTask LockRequestContention = (EventTask)2;
        }

        /// <summary>
        /// The names of constants in this class make up the last term in
        /// the AsyncReaderWriterLock/LockRequest/Issued event name.
        /// </summary>
        /// <remarks>The name of this class is important for EventSource.</remarks>
        public static class Opcodes
        {
            // Custom opcodes should range 11 - 239; see http://msdn.microsoft.com/en-us/library/windows/desktop/dd996918(v=vs.85).aspx
            public const EventOpcode ReaderWriterLockWaiting = (EventOpcode)100;
            public const EventOpcode ReaderWriterLockIssued = (EventOpcode)101;
            public const EventOpcode ReaderWriterLockIssuedAfterContention = (EventOpcode)102;
        }
    }
}
