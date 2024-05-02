// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A mutex that can be entered asynchronously.
/// </summary>
/// <remarks>
/// <para>
/// This class utilizes the OS mutex synchronization primitive, which is fundamentally thread-affinitized and requires synchronously blocking the thread that will own the mutex.
/// This makes a native mutex unsuitable for use in async methods, where the thread that enters the mutex may not be the same thread that exits it.
/// This class solves that problem by using a private dedicated thread to enter and release the mutex, but otherwise allows its owner to execute async code, switch threads, etc.
/// </para>
/// </remarks>
/// <example><![CDATA[
/// using AsyncCrossProcessMutex mutex = new("Some-Unique Name");
/// using (await mutex.EnterAsync())
/// {
///     // Code that must not execute in parallel with any other thread or process protected by the same named mutex.
/// }
/// ]]></example>
public class AsyncCrossProcessMutex
    : IDisposable
{
#pragma warning disable SA1310 // Field names should not contain underscore
    private const int STATE_READY = 0;
    private const int STATE_HELD_OR_WAITING = 1;
    private const int STATE_DISPOSED = 2;
#pragma warning restore SA1310 // Field names should not contain underscore

    private static readonly Action ExitSentinel = new Action(() => { });
    private readonly Thread namedMutexOwner;
    private readonly BlockingCollection<Action> mutexWorkQueue = new();
    private readonly Mutex mutex;
    private int state;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncCrossProcessMutex"/> class.
    /// </summary>
    /// <param name="name">
    /// A non-empty name for the mutex, which follows standard mutex naming rules.
    /// This name will share a namespace with other processes in the system and collisions will result in the processes sharing a single mutex across processes.
    /// </param>
    /// <remarks>
    /// See the help docs on the underlying <see cref="Mutex"/> class for more information on the <paramref name="name"/> parameter.
    /// Consider when reading that the <c>initiallyOwned</c> parameter for that constructor is always <see langword="false"/> for this class.
    /// </remarks>
    public AsyncCrossProcessMutex(string name)
    {
        Requires.NotNullOrEmpty(name);
        this.namedMutexOwner = new Thread(this.MutexOwnerThread, 256 * 1024)
        {
            Name = $"{nameof(AsyncCrossProcessMutex)}-{name}",
        };
        this.mutex = new Mutex(false, name);
        this.namedMutexOwner.Start();
        this.Name = name;
    }

    /// <summary>
    /// Gets the name of the mutex.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Disposes of the underlying native objects.
    /// </summary>
    public void Dispose()
    {
        int priorState = Interlocked.Exchange(ref this.state, STATE_DISPOSED);
        if (priorState != STATE_DISPOSED)
        {
            this.mutexWorkQueue.Add(ExitSentinel);
            this.mutexWorkQueue.CompleteAdding();
        }
    }

    /// <inheritdoc cref="EnterAsync(TimeSpan)"/>
    public Task<LockReleaser> EnterAsync() => this.EnterAsync(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Acquires the mutex asynchronously.
    /// </summary>
    /// <param name="timeout">The maximum time to wait before timing out. Use <see cref="Timeout.InfiniteTimeSpan"/> for no timeout, or <see cref="TimeSpan.Zero"/> to acquire the mutex only if it is immediately available.</param>
    /// <returns>A value whose disposal will release the mutex.</returns>
    /// <exception cref="TimeoutException">Thrown from the awaited result if the mutex could not be acquired within the specified timeout.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown from the awaited result if the <paramref name="timeout"/> is a negative number other than -1 milliseconds, which represents an infinite timeout.</exception>
    /// <exception cref="InvalidOperationException">Thrown if called before a prior call to this method has completed, with its releaser disposed if the mutex was entered.</exception>
    public async Task<LockReleaser> EnterAsync(TimeSpan timeout) => await this.TryEnterAsync(timeout) ?? throw new TimeoutException();

    /// <summary>
    /// Acquires the mutex asynchronously, allowing for timeouts without throwing exceptions.
    /// </summary>
    /// <param name="timeout">The maximum time to wait before timing out. Use <see cref="Timeout.InfiniteTimeSpan"/> for no timeout, or <see cref="TimeSpan.Zero"/> to acquire the mutex only if it is immediately available.</param>
    /// <returns>
    /// If the mutex was acquired, the result is a value whose disposal will release the mutex.
    /// In the event of a timeout, the result in a <see langword="null" /> value.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown from the awaited result if the <paramref name="timeout"/> is a negative number other than -1 milliseconds, which represents an infinite timeout.</exception>
    /// <exception cref="InvalidOperationException">Thrown if called before a prior call to this method has completed, with its releaser disposed if the mutex was entered.</exception>
    public Task<LockReleaser?> TryEnterAsync(TimeSpan timeout)
    {
        int priorState = Interlocked.CompareExchange(ref this.state, STATE_HELD_OR_WAITING, STATE_READY);
        switch (priorState)
        {
            case STATE_HELD_OR_WAITING:
                throw new InvalidOperationException();
            case STATE_DISPOSED:
                throw new ObjectDisposedException(this.GetType().FullName);
        }

        // Pass `this` as the state simply to assist in debugging dumps.
        TaskCompletionSource<LockReleaser?> tcs = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        this.mutexWorkQueue.Add(delegate
        {
            try
            {
                if (this.mutex.WaitOne(timeout))
                {
                    tcs.SetResult(new LockReleaser(this));
                }
                else
                {
                    Assumes.True(Interlocked.CompareExchange(ref this.state, STATE_READY, STATE_HELD_OR_WAITING) == STATE_HELD_OR_WAITING);
                    tcs.SetResult(null);
                }
            }
            catch (AbandonedMutexException)
            {
                tcs.SetResult(new LockReleaser(this, abandoned: true));
            }
            catch (Exception ex)
            {
                Assumes.True(Interlocked.CompareExchange(ref this.state, STATE_READY, STATE_HELD_OR_WAITING) == STATE_HELD_OR_WAITING);
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private void Release()
    {
        Assumes.True(Interlocked.CompareExchange(ref this.state, STATE_READY, STATE_HELD_OR_WAITING) == STATE_HELD_OR_WAITING);
        this.mutexWorkQueue.Add(this.mutex.ReleaseMutex);
    }

    private void MutexOwnerThread()
    {
        try
        {
            while (!this.mutexWorkQueue.IsCompleted)
            {
                Action work = this.mutexWorkQueue.Take();
                if (work == ExitSentinel)
                {
                    // We use an exit sentinel to avoid an exception having to be thrown and caught on disposal when we call Take() and CompleteAdding() is called.
                    break;
                }

                work();
            }
        }
        finally
        {
            this.mutex.Dispose();
        }
    }

    /// <summary>
    /// The value returned from <see cref="EnterAsync(TimeSpan)"/> that must be disposed to release the mutex.
    /// </summary>
    public struct LockReleaser : IDisposable
    {
        private AsyncCrossProcessMutex? owner;

        internal LockReleaser(AsyncCrossProcessMutex mutex, bool abandoned = false)
        {
            this.owner = mutex;
            this.IsAbandoned = abandoned;
        }

        /// <summary>
        /// Gets a value indicating whether the mutex was abandoned by its previous owner.
        /// </summary>
        public bool IsAbandoned { get; }

        /// <summary>
        /// Releases the named mutex.
        /// </summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref this.owner, null)?.Release();
        }
    }
}
