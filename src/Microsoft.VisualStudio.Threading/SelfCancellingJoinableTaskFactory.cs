// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A <see cref="JoinableTaskFactory"/> that exposes a <see cref="DisposalToken"/> which is canceled
/// when the underlying <see cref="JoinableTaskCollection"/> begins draining via
/// <see cref="JoinableTaskCollection.JoinTillEmptyAsync(CancellationToken)"/>, or when this factory is disposed.
/// </summary>
/// <remarks>
/// This allows code that has access to the factory to observe shutdown without
/// requiring a separate <see cref="CancellationToken"/> to be plumbed through call sites.
/// </remarks>
public class SelfCancellingJoinableTaskFactory : DelegatingJoinableTaskFactory, IDisposable
{
    /// <summary>
    /// The source for <see cref="DisposalToken"/>.
    /// </summary>
    private readonly CancellationTokenSource cancellationTokenSource;

    /// <summary>
    /// A value indicating whether this instance has been disposed.
    /// </summary>
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelfCancellingJoinableTaskFactory"/> class.
    /// </summary>
    /// <param name="innerFactory">The inner factory to delegate to. Must have a non-null <see cref="JoinableTaskFactory.Collection"/>.</param>
    public SelfCancellingJoinableTaskFactory(JoinableTaskFactory innerFactory)
        : base(Requires.NotNull(innerFactory, nameof(innerFactory)))
    {
        Assumes.NotNull(innerFactory.Collection);
        this.cancellationTokenSource = new CancellationTokenSource();
        this.DisposalToken = this.cancellationTokenSource.Token;
        innerFactory.Collection!.JoinTillEmptyAsyncRequested += this.OnJoinTillEmptyAsyncRequested;
    }

    /// <summary>
    /// Gets a <see cref="CancellationToken"/> that is canceled when the underlying collection
    /// begins draining or when this factory is disposed.
    /// </summary>
    public CancellationToken DisposalToken { get; private init; }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and unmanaged resources held by this instance.
    /// </summary>
    /// <param name="disposing"><see langword="true" /> if <see cref="Dispose()"/> was called; <see langword="false" /> if the object is being finalized.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.disposed = true;

            if (this.Collection is object)
            {
                this.Collection.JoinTillEmptyAsyncRequested -= this.OnJoinTillEmptyAsyncRequested;
            }

            this.cancellationTokenSource.Cancel();
            this.cancellationTokenSource.Dispose();
        }
    }

    private void OnJoinTillEmptyAsyncRequested(object? sender, EventArgs e)
    {
        this.cancellationTokenSource.Cancel();
    }
}
