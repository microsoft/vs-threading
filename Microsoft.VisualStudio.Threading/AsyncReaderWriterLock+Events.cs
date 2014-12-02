/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Diagnostics.Tracing;

	/// <content>
	/// The <see cref="AsyncReaderWriterLock.Events"/> class.
	/// </content>
	partial class AsyncReaderWriterLock {
		/// <summary>
		/// The instance of the ETW logger to use.
		/// </summary>
		private EventsHelper Etw;

		internal class EventsHelper {
			private readonly AsyncReaderWriterLock lck;

			internal EventsHelper(AsyncReaderWriterLock lck) {
				Requires.NotNull(lck, "lck");
				this.lck = lck;
			}

			public void Issued(Awaiter lckAwaiter) {
				if (Events.Log.IsEnabled()) {
					Events.Log.Issued(lckAwaiter.GetHashCode(), lckAwaiter.Kind, this.lck.issuedUpgradeableReadLocks.Count, this.lck.issuedReadLocks.Count);
				}
			}

			public void WaitStart(Awaiter lckAwaiter) {
				if (Events.Log.IsEnabled()) {
					Events.Log.WaitStart(lckAwaiter.GetHashCode(), lckAwaiter.Kind, this.lck.issuedWriteLocks.Count, this.lck.issuedUpgradeableReadLocks.Count, this.lck.issuedReadLocks.Count);
				}
			}

			public void WaitStop(Awaiter lckAwaiter) {
				if (Events.Log.IsEnabled()) {
					Events.Log.WaitStop(lckAwaiter.GetHashCode(), lckAwaiter.Kind);
				}
			}
		}

		/// <summary>
		/// The ETW source for logging events for the <see cref="AsyncReaderWriterLock"/>.
		/// </summary>
		/// <remarks>
		/// We use a fully-descriptive type name because the type name becomes the name
		/// of the ETW Provider.
		/// </remarks>
		[EventSource(Name = "AsyncReaderWriterLock")]
		internal sealed class Events : EventSource {
			/// <summary>
			/// The event ID for the <see cref="Issued(int, LockKind, int, int)"/> event.
			/// </summary>
			private const int IssuedLockCountsEvent = 1;

			/// <summary>
			/// The event ID for the <see cref="WaitStart(int, LockKind, int, int, int)"/> event.
			/// </summary>
			private const int WaitStartEvent = 2;

			/// <summary>
			/// The event ID for the <see cref="WaitStop(int, LockKind)"/> event.
			/// </summary>
			private const int WaitStopEvent = 3;

			/// <summary>
			/// The singleton instance used for logging.
			/// </summary>
			internal static readonly Events Log = new Events();

			/// <summary>
			/// Initializes a new instance of the <see cref="Events"/> class.
			/// </summary>
			protected Events() : base(false) {
			}

			/// <summary>
			/// Logs an issued lock.
			/// </summary>
			[Event(IssuedLockCountsEvent, Task = Tasks.LockRequest, Opcode = Opcodes.Issued)]
			public void Issued(int lockId, LockKind kind, int issuedUpgradeableReadCount, int issuedReadCount) {
				WriteEvent(IssuedLockCountsEvent, (int)kind, issuedUpgradeableReadCount, issuedReadCount);
			}

			/// <summary>
			/// Logs a wait for a lock.
			/// </summary>
			[Event(WaitStartEvent, Task = Tasks.LockRequestContention, Opcode = EventOpcode.Start)]
			public void WaitStart(int lockId, LockKind kind, int issuedWriteCount, int issuedUpgradeableReadCount, int issuedReadCount) {
				unsafe
				{
					// WriteEvent overloads only allow up to 3 args. To get more into it
					// we have to use an n-arg overload (via an array).
					// We use stackalloc to prepare an array without introducing any GC pressure
					// from the array itself or boxing integers into object to fit an object[].
					EventData* eventPayload = stackalloc EventData[5];
					eventPayload[0].Size = 4;
					eventPayload[0].DataPointer = (IntPtr)(&lockId);
					eventPayload[1].Size = 4;
					eventPayload[1].DataPointer = (IntPtr)(&kind);
					eventPayload[2].Size = 4;
					eventPayload[2].DataPointer = (IntPtr)(&issuedWriteCount);
					eventPayload[3].Size = 4;
					eventPayload[3].DataPointer = (IntPtr)(&issuedUpgradeableReadCount);
					eventPayload[4].Size = 4;
					eventPayload[4].DataPointer = (IntPtr)(&issuedReadCount);
					WriteEventCore(WaitStartEvent, 4, eventPayload);
				}
			}

			/// <summary>
			/// Logs a lock that was issued after a contending lock was released.
			/// </summary>
			[Event(WaitStopEvent, Task = Tasks.LockRequestContention, Opcode = EventOpcode.Stop)]
			public void WaitStop(int lockId, LockKind kind) {
				WriteEvent(WaitStopEvent, lockId, (int)kind);
			}

			/// <summary>
			/// The names of constants in this class make up the middle term in 
			/// the AsyncReaderWriterLock/LockRequest/Issued event name.
			/// </summary>
			/// <remarks>The name of this class is important for EventSource.</remarks>
			public class Tasks {
				public const EventTask LockRequest = (EventTask)1;
				public const EventTask LockRequestContention = (EventTask)2;
			}

			/// <summary>
			/// The names of constants in this class make up the last term in 
			/// the AsyncReaderWriterLock/LockRequest/Issued event name.
			/// </summary>
			/// <remarks>The name of this class is important for EventSource.</remarks>
			public class Opcodes {
				// Custom opcodes should range 11 - 239; see http://msdn.microsoft.com/en-us/library/windows/desktop/dd996918(v=vs.85).aspx
				public const EventOpcode Waiting = (EventOpcode)100;
				public const EventOpcode Issued = (EventOpcode)101;
				public const EventOpcode IssuedAfterContention = (EventOpcode)102;
			}
		}
	}
}