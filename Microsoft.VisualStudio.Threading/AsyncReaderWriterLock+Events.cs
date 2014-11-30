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
		private static Events Etw = Events.Log;

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
			/// The event ID for the <see cref="Issued(LockKind, int, int)"/> event.
			/// </summary>
			private const int IssuedLockCountsEvent = 1;

			/// <summary>
			/// The event ID for the <see cref="Waiting(LockKind, int, int, int)"/> event.
			/// </summary>
			private const int WaitingLockCountsEvent = 2;

			/// <summary>
			/// The event ID for the <see cref="IssuedAfterContention(LockKind)"/> event.
			/// </summary>
			private const int IssuedAfterContentionEvent = 3;

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
			[Event(IssuedLockCountsEvent, Version = 1, Task = Tasks.LockRequest, Opcode = Opcodes.Issued,
				Level = EventLevel.Informational, Keywords = Keywords.SomeKeyword)]
			public void Issued(LockKind kind, int issuedUpgradeableReadCount, int issuedReadCount) {
				WriteEvent(IssuedLockCountsEvent, (int)kind, issuedUpgradeableReadCount, issuedReadCount);
			}

			/// <summary>
			/// Logs a lock that was issued after a contending lock was released.
			/// </summary>
			/// <param name="kind">The kind of lock that was issued.</param>
			[Event(IssuedAfterContentionEvent, Version = 1, Task = Tasks.LockRequest, Opcode = Opcodes.IssuedAfterContention,
				Level = EventLevel.Informational, Keywords = Keywords.SomeKeyword)]
			public void IssuedAfterContention(LockKind kind) {
				WriteEvent(IssuedAfterContentionEvent, (int)kind);
			}

			/// <summary>
			/// Logs a wait for a lock.
			/// </summary>
			[Event(WaitingLockCountsEvent, Version = 1, Task = Tasks.LockRequest, Opcode = Opcodes.Waiting,
				Level = EventLevel.Informational, Keywords = Keywords.SomeKeyword)]
			public void Waiting(LockKind kind, int issuedWriteCount, int issuedUpgradeableReadCount, int issuedReadCount) {
				unsafe
				{
					// WriteEvent overloads only allow up to 3 args. To get more into it
					// we have to use an n-arg overload (via an array).
					// We use stackalloc to prepare an array without introducing any GC pressure
					// from the array itself or boxing integers into object to fit an object[].
					EventData* eventPayload = stackalloc EventData[4];
					eventPayload[0].Size = 4;
					eventPayload[0].DataPointer = (IntPtr)(&kind);
					eventPayload[1].Size = 4;
					eventPayload[1].DataPointer = (IntPtr)(&issuedWriteCount);
					eventPayload[2].Size = 4;
					eventPayload[2].DataPointer = (IntPtr)(&issuedUpgradeableReadCount);
					eventPayload[3].Size = 4;
					eventPayload[3].DataPointer = (IntPtr)(&issuedReadCount);
					WriteEventCore(WaitingLockCountsEvent, 4, eventPayload);
				}
			}

			/// <summary>
			/// The names of constants in this class make up the middle term in 
			/// the AsyncReaderWriterLock/LockRequest/Issued event name.
			/// </summary>
			/// <remarks>The name of this class is important for EventSource.</remarks>
			public class Tasks {
				public const EventTask LockRequest = (EventTask)1;
			}

			/// <summary>I don't know what this is for yet.</summary>
			/// <remarks>The name of this class is important for EventSource.</remarks>
			public class Keywords {
				public const EventKeywords SomeKeyword = (EventKeywords)1;
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