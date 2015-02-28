/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Diagnostics.Tracing;

	/// <summary>
	/// The ETW source for logging events for the <see cref="AsyncReaderWriterLock"/>.
	/// </summary>
	/// <remarks>
	/// We use a fully-descriptive type name because the type name becomes the name
	/// of the ETW Provider.
	/// </remarks>
	[EventSource(Name = "Microsoft-VisualStudio-Threading")]
	internal sealed class ThreadingEventSource : EventSource {
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
		/// The singleton instance used for logging.
		/// </summary>
		internal static readonly ThreadingEventSource Instance = new ThreadingEventSource();

		/// <summary>
		/// Logs an issued lock.
		/// </summary>
		[Event(ReaderWriterLockIssuedLockCountsEvent, Task = Tasks.LockRequest, Opcode = Opcodes.ReaderWriterLockIssued)]
		public void ReaderWriterLockIssued(int lockId, AsyncReaderWriterLock.LockKind kind, int issuedUpgradeableReadCount, int issuedReadCount) {
            this.WriteEvent(ReaderWriterLockIssuedLockCountsEvent, lockId, (int)kind, issuedUpgradeableReadCount, issuedReadCount);
		}

		/// <summary>
		/// Logs a wait for a lock.
		/// </summary>
		[Event(WaitReaderWriterLockStartEvent, Task = Tasks.LockRequestContention, Opcode = EventOpcode.Start)]
		public void WaitReaderWriterLockStart(int lockId, AsyncReaderWriterLock.LockKind kind, int issuedWriteCount, int issuedUpgradeableReadCount, int issuedReadCount) {
            this.WriteEvent(WaitReaderWriterLockStartEvent, lockId, kind, issuedWriteCount, issuedUpgradeableReadCount, issuedReadCount);
		}

		/// <summary>
		/// Logs a lock that was issued after a contending lock was released.
		/// </summary>
		[Event(WaitReaderWriterLockStopEvent, Task = Tasks.LockRequestContention, Opcode = EventOpcode.Stop)]
		public void WaitReaderWriterLockStop(int lockId, AsyncReaderWriterLock.LockKind kind) {
            this.WriteEvent(WaitReaderWriterLockStopEvent, lockId, (int)kind);
		}

		/// <summary>
		/// The names of constants in this class make up the middle term in 
		/// the AsyncReaderWriterLock/LockRequest/Issued event name.
		/// </summary>
		/// <remarks>The name of this class is important for EventSource.</remarks>
		public static class Tasks {
			public const EventTask LockRequest = (EventTask)1;
			public const EventTask LockRequestContention = (EventTask)2;
		}

		/// <summary>
		/// The names of constants in this class make up the last term in 
		/// the AsyncReaderWriterLock/LockRequest/Issued event name.
		/// </summary>
		/// <remarks>The name of this class is important for EventSource.</remarks>
		public static class Opcodes {
			// Custom opcodes should range 11 - 239; see http://msdn.microsoft.com/en-us/library/windows/desktop/dd996918(v=vs.85).aspx
			public const EventOpcode ReaderWriterLockWaiting = (EventOpcode)100;
			public const EventOpcode ReaderWriterLockIssued = (EventOpcode)101;
			public const EventOpcode ReaderWriterLockIssuedAfterContention = (EventOpcode)102;
		}
	}
}