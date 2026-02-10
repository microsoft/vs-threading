// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A variant on <see cref="JoinableTaskFactory"/> that tracks pending tasks and blocks disposal until all tasks have completed.
/// A cancellation token is provided so pending tasks can cooperatively cancel when disposal is requested.
/// </summary>
/// <remarks>
/// <para>
/// Cancellation of pending tasks is cooperative.
/// If a pending task does not observe <see cref="DisposalToken" />, then disposal may take longer to complete,
/// or even never complete if a pending task never completes.
/// </para>
/// <para>
/// Creating tasks after disposal has been requested is not prevented by this class.
/// </para>
/// </remarks>
public class DisposableJoinableTaskFactory : DelegatingJoinableTaskFactory, IDisposable, System.IAsyncDisposable
{
    private readonly CancellationTokenSource disposalTokenSource = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DisposableJoinableTaskFactory"/> class.
    /// </summary>
    /// <param name="innerFactory">The factory instance to be wrapped. Must have an associated collection.</param>
    public DisposableJoinableTaskFactory(JoinableTaskFactory innerFactory)
        : base(innerFactory)
    {
        Requires.Argument(this.Collection is not null, nameof(innerFactory), "A collection must be associated with the factory.");

        // Get it now, since after the CTS is disposed, it throws when we try to access the token.
        this.DisposalToken = this.disposalTokenSource.Token;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DisposableJoinableTaskFactory"/> class.
    /// </summary>
    /// <param name="joinableTaskContext">The <see cref="JoinableTaskContext"/> used to construct the <see cref="JoinableTaskFactory"/>.</param>
    /// <remarks>
    /// This constructor creates a <see cref="JoinableTaskFactory"/> using <see cref="JoinableTaskContext.CreateFactory(JoinableTaskCollection)"/>.
    /// </remarks>
    public DisposableJoinableTaskFactory(JoinableTaskContext joinableTaskContext)
        : this(Requires.NotNull(joinableTaskContext).CreateFactory(Requires.NotNull(joinableTaskContext).CreateCollection()))
    {
    }

    /// <summary>
    /// Gets a disposal token that <em>should</em> be used by tasks created by this factory to know when they should stop doing work.
    /// </summary>
    /// <remarks>
    /// This token is canceled when the factory is disposed.
    /// </remarks>
    public CancellationToken DisposalToken { get; }

    /// <summary>
    /// Gets the collection to which created tasks belong until they complete.
    /// </summary>
    protected new JoinableTaskCollection Collection => base.Collection!;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this.disposalTokenSource.IsCancellationRequested)
        {
            this.disposalTokenSource.Cancel();
            this.disposalTokenSource.Dispose();
        }

        this.Context.Factory.Run(() => this.Collection.JoinTillEmptyAsync());
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!this.disposalTokenSource.IsCancellationRequested)
        {
            this.disposalTokenSource.Cancel();
            this.disposalTokenSource.Dispose();
        }

        await this.Collection.JoinTillEmptyAsync();
    }
}
