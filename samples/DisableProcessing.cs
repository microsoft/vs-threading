// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable VSTHRD103 // Call async methods when in an async method

using System.IO;
using Microsoft.VisualStudio.Threading;

internal class DisableProcessing
{
    private readonly JoinableTaskFactory joinableTaskFactory = null!;

    private void Simple()
    {
        #region Simple
        this.joinableTaskFactory.Run(async delegate
        {
            this.joinableTaskFactory.DisableProcessing();

            // Synchronous I/O and lock contentions will NOT result in any reentrancy within this JoinableTask.
        });
        #endregion
    }

    private void Exhaustive()
    {
        #region Exhaustive
        this.joinableTaskFactory.Run(async delegate
        {
            // Async I/O isn't expected to synchronously block, and thus would never allow unwanted reentrancy.
            string content = await File.ReadAllTextAsync(@"somefile.txt");

            // Here, synchronous I/O and lock contentions MAY allow certain reentrancy (e.g. COM RPC messages).
            content = File.ReadAllText(@"somefile.txt");

            using (this.joinableTaskFactory.DisableProcessing())
            {
                // Within this block, synchronous I/O and lock contentions will NOT result in any reentrancy.
                content = File.ReadAllText(@"somefile.txt");
            }

            // Just disable the synchronous wait message pump for the rest of this JoinableTask.
            this.joinableTaskFactory.DisableProcessing();

            // Sync I/O and lock contentions will NOT result in any reentrancy here.
            content = File.ReadAllText(@"somefile.txt");
        });
        #endregion
    }
}
