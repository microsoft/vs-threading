// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class SuppressRelevanceSample
{
    private readonly ReentrantSemaphore semaphore = ReentrantSemaphore.Create(mode: ReentrantSemaphore.ReentrancyMode.NotAllowed);

    #region SuppressRelevance
    public async Task DoSomethingAsync()
    {
        await this.semaphore.ExecuteAsync(async delegate
        {
            // field access under the semaphore
            // ...
            await Task.Yield(); // represents some async work

            // Fire and forget code that uses the semaphore, but should *not*
            // inherit our own posession of the semaphore.
            using (this.semaphore.SuppressRelevance())
            {
                this.DoSomethingLaterAsync().Forget(); // Don't await this, or a deadlock will occur.
            }
        });
    }

    private async Task DoSomethingLaterAsync()
    {
        // This semaphore use will not be seen as nested because of our caller's wrapping
        // the call in SuppressRelevance.
        // So instead of throwing, it will block till its caller releases the semaphore.
        await this.semaphore.ExecuteAsync(async delegate
        {
            // Whatever
            await Task.Yield(); // represents some async work
        });
    }
    #endregion
}
