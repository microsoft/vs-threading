/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Threading.Tests;
using Xunit;
using Xunit.Abstractions;

public class SingleThreadedSynchronizationContextTests : TestBase
{
    public SingleThreadedSynchronizationContextTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void CreateCopy_ReturnsInstanceOfCorrectType()
    {
        var syncContext = new SingleThreadedSynchronizationContext();
        Assert.IsType<SingleThreadedSynchronizationContext>(syncContext.CreateCopy());
    }

    [Fact]
    public void CreateCopy_ReturnsNewInstance()
    {
        var syncContext = new SingleThreadedSynchronizationContext();
        var other = syncContext.CreateCopy();
        Assert.NotSame(syncContext, other);

        // Verify that posting to the copy effectively gets the work to run on the original thread.
        var frame = new SingleThreadedSynchronizationContext.Frame();
        int? observedThreadId = null;
        Task.Run(() =>
        {
            other.Post(
                s =>
                {
                    observedThreadId = Environment.CurrentManagedThreadId;
                    frame.Continue = false;
                },
                null);
        });

        syncContext.PushFrame(frame);
        Assert.Equal(Environment.CurrentManagedThreadId, observedThreadId.Value);
    }

    [Fact]
    public void Send_ExecutesDelegate_NoThrow()
    {
        var syncContext = new SingleThreadedSynchronizationContext();
        int? executingThread = null;
        syncContext.Send(s => executingThread = Environment.CurrentManagedThreadId, null);
        Assert.Equal(Environment.CurrentManagedThreadId, executingThread);
    }

    [Fact]
    public void Send_ExecutesDelegate_Throws()
    {
        var syncContext = new SingleThreadedSynchronizationContext();
        Exception expected = new InvalidOperationException();
        var actual = Assert.Throws<TargetInvocationException>(() => syncContext.Send(s => throw expected, null));
        Assert.Same(expected, actual.InnerException);
    }

    [Fact]
    public void Send_OnDifferentThread_ExecutesDelegateAndWaits()
    {
        int originalThreadId = Environment.CurrentManagedThreadId;
        var syncContext = new SingleThreadedSynchronizationContext();
        var frame = new SingleThreadedSynchronizationContext.Frame();

        var task = Task.Run(delegate
        {
            try
            {
                int? observedThreadId = null;
                syncContext.Send(s => observedThreadId = Environment.CurrentManagedThreadId, null);
                Assert.Equal(originalThreadId, observedThreadId);
            }
            finally
            {
                frame.Continue = false;
            }
        });

        syncContext.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Send_OnDifferentThread_ExecutesDelegateAndWaits_Throws()
    {
        int originalThreadId = Environment.CurrentManagedThreadId;
        var syncContext = new SingleThreadedSynchronizationContext();
        var frame = new SingleThreadedSynchronizationContext.Frame();

        var task = Task.Run(delegate
        {
            try
            {
                var expectedException = new InvalidOperationException();
                var actualException = Assert.Throws<TargetInvocationException>(() => syncContext.Send(s => throw expectedException, null));
                Assert.Same(expectedException, actualException.InnerException);
            }
            finally
            {
                frame.Continue = false;
            }
        });

        syncContext.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Post_DoesNotExecuteSynchronously()
    {
        var syncContext = new SingleThreadedSynchronizationContext();
        int? executingThread = null;
        syncContext.Post(s => executingThread = Environment.CurrentManagedThreadId, null);
        Assert.Null(executingThread);
    }

    [Fact]
    public void Post_PushFrame()
    {
        var originalThreadId = Environment.CurrentManagedThreadId;
        var syncContext = new SingleThreadedSynchronizationContext();
        var frame = new SingleThreadedSynchronizationContext.Frame();

        var postedMessageCompletionSource = new TaskCompletionSource<object>();
        syncContext.Post(
            async state =>
            {
                try
                {
                    Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);
                    await Task.Yield();
                    Assert.Equal(originalThreadId, Environment.CurrentManagedThreadId);

                    postedMessageCompletionSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    postedMessageCompletionSource.SetException(ex);
                }
                finally
                {
                    frame.Continue = false;
                }
            },
            null);

        syncContext.PushFrame(frame);
        Assert.True(postedMessageCompletionSource.Task.IsCompleted);

        // Rethrow any exception.
        postedMessageCompletionSource.Task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Post_PushFrame_Throws()
    {
        var originalThreadId = Environment.CurrentManagedThreadId;
        var syncContext = new SingleThreadedSynchronizationContext();
        var frame = new SingleThreadedSynchronizationContext.Frame();

        var expectedException = new InvalidOperationException();
        syncContext.Post(state => throw expectedException, null);
        var actualException = Assert.Throws<InvalidOperationException>(() => syncContext.PushFrame(frame));
        Assert.Same(expectedException, actualException);
    }

    [Fact]
    public void Post_CapturesExecutionContext()
    {
        var syncContext = new SingleThreadedSynchronizationContext();
        var frame = new SingleThreadedSynchronizationContext.Frame();

        var task = Task.Run(async delegate
        {
            try
            {
                var expectedValue = new object();
                var actualValue = new TaskCompletionSource<object>();

                var asyncLocal = new AsyncLocal<object>();
                asyncLocal.Value = expectedValue;
                syncContext.Post(s => actualValue.SetResult(asyncLocal.Value), null);
                Assert.Same(expectedValue, await actualValue.Task);
            }
            finally
            {
                frame.Continue = false;
            }
        });

        syncContext.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void OperationStarted_OperationCompleted()
    {
        var syncContext = new SingleThreadedSynchronizationContext();

        // Just make sure they don't throw when called properly.
        syncContext.OperationStarted();
        syncContext.OperationCompleted();
    }

    [Fact]
    public void OperationStarted_OperationCompleted_Nested()
    {
        var syncContext = new SingleThreadedSynchronizationContext();

        // Just make sure they don't throw when called properly.
        syncContext.OperationStarted();
        syncContext.OperationStarted();
        syncContext.OperationCompleted();
        syncContext.OperationCompleted();
    }
}
