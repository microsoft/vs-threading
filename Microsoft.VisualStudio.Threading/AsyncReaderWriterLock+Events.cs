/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading {
	using System.Diagnostics.Tracing;

	/// <content>
	/// The <see cref="AsyncReaderWriterLock.Etw"/> class.
	/// </content>
	partial class AsyncReaderWriterLock {
		/// <summary>
		/// The instance of the ETW logger to use.
		/// </summary>
		private static AsyncReaderWriterLockEvents Etw = AsyncReaderWriterLockEvents.Log;

		/// <summary>
		/// The ETW source for logging events for the <see cref="AsyncReaderWriterLock"/>.
		/// </summary>
		/// <remarks>
		/// We use a fully-descriptive type name because the type name becomes the name
		/// of the ETW Provider.
		/// </remarks>
		internal sealed class AsyncReaderWriterLockEvents : EventSource {
			/// <summary>
			/// The event ID for the <see cref="Issued(int, int, int)"/> event.
			/// </summary>
			private const int IssuedLockCountsEvent = 1;

			/// <summary>
			/// The event ID for the <see cref="Waiting(int, int, int)"/> event.
			/// </summary>
			private const int WaitingLockCountsEvent = 2;

			/// <summary>
			/// The singleton instance used for logging.
			/// </summary>
			internal static readonly AsyncReaderWriterLockEvents Log = new AsyncReaderWriterLockEvents();

			/// <summary>
			/// Initializes a new instance of the <see cref="AsyncReaderWriterLockEvents"/> class.
			/// </summary>
			protected AsyncReaderWriterLockEvents() : base(false) {
			}

			/// <summary>
			/// Logs the number of issued locks of each type.
			/// </summary>
			public void Issued(int issuedWriteCount, int issuedUpgradeableReadCount, int issuedReadCount) {
				WriteEvent(IssuedLockCountsEvent, issuedWriteCount, issuedUpgradeableReadCount, issuedReadCount);
			}

			/// <summary>
			/// Logs the number of waiting locks of each type.
			/// </summary>
			public void Waiting(int waitingWriteCount, int waitingUpgradeableReadCount, int waitingReadCount) {
				WriteEvent(WaitingLockCountsEvent, waitingWriteCount, waitingUpgradeableReadCount, waitingReadCount);
			}
		}
	}
}