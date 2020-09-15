// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Threading;

    /// <summary>
    /// A structure that applies and reverts changes to the <see cref="SynchronizationContext"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
    public readonly struct SpecializedSyncContext : IDisposable
    {
        /// <summary>
        /// A flag indicating whether the non-default constructor was invoked.
        /// </summary>
        private readonly bool initialized;

        /// <summary>
        /// The SynchronizationContext to restore when <see cref="Dispose"/> is invoked.
        /// </summary>
        private readonly SynchronizationContext? prior;

        /// <summary>
        /// The SynchronizationContext applied when this struct was constructed.
        /// </summary>
        private readonly SynchronizationContext? appliedContext;

        /// <summary>
        /// A value indicating whether to check that the applied SyncContext is still the current one when the original is restored.
        /// </summary>
        private readonly bool checkForChangesOnRevert;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpecializedSyncContext"/> struct.
        /// </summary>
        private SpecializedSyncContext(SynchronizationContext? syncContext, bool checkForChangesOnRevert)
        {
            this.initialized = true;
            this.prior = SynchronizationContext.Current;
            this.appliedContext = syncContext;
            this.checkForChangesOnRevert = checkForChangesOnRevert;
            SynchronizationContext.SetSynchronizationContext(syncContext);
        }

        /// <summary>
        /// Applies the specified <see cref="SynchronizationContext"/> to the caller's context.
        /// </summary>
        /// <param name="syncContext">The synchronization context to apply.</param>
        /// <param name="checkForChangesOnRevert">A value indicating whether to check that the applied SyncContext is still the current one when the original is restored.</param>
        public static SpecializedSyncContext Apply(SynchronizationContext? syncContext, bool checkForChangesOnRevert = true)
        {
            return new SpecializedSyncContext(syncContext, checkForChangesOnRevert);
        }

        /// <summary>
        /// Reverts the SynchronizationContext to its previous instance.
        /// </summary>
        public void Dispose()
        {
            if (this.initialized)
            {
                Report.If(this.checkForChangesOnRevert && SynchronizationContext.Current != this.appliedContext);
                SynchronizationContext.SetSynchronizationContext(this.prior);
            }
        }
    }
}
