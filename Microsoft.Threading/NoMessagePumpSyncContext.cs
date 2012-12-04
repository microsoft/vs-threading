//-----------------------------------------------------------------------
// <copyright file="NoMessagePumpSyncContext.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Threading {
	using System;
	using System.Threading;

	/// <summary>
	/// A SynchronizationContext whose synchronously blocking Wait method does not allow 
	/// any reentrancy via the message pump.
	/// </summary>
	public class NoMessagePumpSyncContext : SynchronizationContext {
		/// <summary>
		/// A shared singleton.
		/// </summary>
		private static SynchronizationContext DefaultInstance = new NoMessagePumpSyncContext();

		/// <summary>
		/// Initializes a new instance of the <see cref="NoMessagePumpSyncContext"/> class.
		/// </summary>
		public NoMessagePumpSyncContext() {
			// This is required so that our override of Wait is invoked.
			this.SetWaitNotificationRequired();
		}

		/// <summary>
		/// Gets a shared instance of this class.
		/// </summary>
		public static SynchronizationContext Default {
			get { return DefaultInstance; }
		}

		/// <summary>
		/// Synchronously blocks without a message pump.
		/// </summary>
		public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout) {
			return NativeMethods.WaitForMultipleObjects((uint)waitHandles.Length, waitHandles, waitAll, (uint)millisecondsTimeout);
		}
	}
}
