namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class TplExtensionsTests : TestBase
    {
        public TplExtensionsTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Fact]
        public void CompletedTask()
        {
            Assert.True(TplExtensions.CompletedTask.IsCompleted);
        }

        [Fact]
        public void AppendActionTest()
        {
            var evt = new ManualResetEventSlim();
            Action a = () => evt.Set();
            var cts = new CancellationTokenSource();
            var result = TplExtensions.CompletedTask.AppendAction(a, TaskContinuationOptions.DenyChildAttach, cts.Token);
            Assert.NotNull(result);
            Assert.Equal(TaskContinuationOptions.DenyChildAttach, (TaskContinuationOptions)result.CreationOptions);
            Assert.True(evt.Wait(TestTimeout));
        }

        [Fact]
        public void ApplyResultToNullTask()
        {
            Assert.Throws<ArgumentNullException>(() => TplExtensions.ApplyResultTo(null, new TaskCompletionSource<object>()));
        }

        [Fact]
        public void ApplyResultToNullTaskSource()
        {
            var tcs = new TaskCompletionSource<object>();
            Assert.Throws<ArgumentNullException>(() => TplExtensions.ApplyResultTo(tcs.Task, null));
        }

        [Fact]
        public void ApplyResultTo()
        {
            var tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            var tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.Task.ApplyResultTo(tcs2);
            tcs1.SetResult(new GenericParameterHelper(2));
            Assert.Equal(2, tcs2.Task.Result.Data);

            tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.Task.ApplyResultTo(tcs2);
            tcs1.SetCanceled();
            Assert.True(tcs2.Task.IsCanceled);

            tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.Task.ApplyResultTo(tcs2);
            tcs1.SetException(new ApplicationException());
            Assert.Same(tcs1.Task.Exception.InnerException, tcs2.Task.Exception.InnerException);
        }

        [Fact]
        public void ApplyResultToPreCompleted()
        {
            var tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            var tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.SetResult(new GenericParameterHelper(2));
            tcs1.Task.ApplyResultTo(tcs2);
            Assert.Equal(2, tcs2.Task.Result.Data);

            tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.SetCanceled();
            tcs1.Task.ApplyResultTo(tcs2);
            Assert.True(tcs2.Task.IsCanceled);

            tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.SetException(new ApplicationException());
            tcs1.Task.ApplyResultTo(tcs2);
            Assert.Same(tcs1.Task.Exception.InnerException, tcs2.Task.Exception.InnerException);
        }

        [Fact]
        public void ApplyResultToNullTaskNonGeneric()
        {
            Assert.Throws<ArgumentNullException>(() => TplExtensions.ApplyResultTo((Task)null, new TaskCompletionSource<object>()));
        }

        [Fact]
        public void ApplyResultToNullTaskSourceNonGeneric()
        {
            var tcs = new TaskCompletionSource<object>();
            Assert.Throws<ArgumentNullException>(() => TplExtensions.ApplyResultTo((Task)tcs.Task, (TaskCompletionSource<object>)null));
        }

        [Fact]
        public void ApplyResultToNonGeneric()
        {
            var tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            var tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            ((Task)tcs1.Task).ApplyResultTo(tcs2);
            tcs1.SetResult(null);
            Assert.Equal(TaskStatus.RanToCompletion, tcs2.Task.Status);

            tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            ((Task)tcs1.Task).ApplyResultTo(tcs2);
            tcs1.SetCanceled();
            Assert.True(tcs2.Task.IsCanceled);

            tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            ((Task)tcs1.Task).ApplyResultTo(tcs2);
            tcs1.SetException(new ApplicationException());
            Assert.Same(tcs1.Task.Exception.InnerException, tcs2.Task.Exception.InnerException);
        }

        [Fact]
        public void ApplyResultToPreCompletedNonGeneric()
        {
            var tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            var tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.SetResult(null);
            ((Task)tcs1.Task).ApplyResultTo(tcs2);
            Assert.Equal(TaskStatus.RanToCompletion, tcs2.Task.Status);

            tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.SetCanceled();
            ((Task)tcs1.Task).ApplyResultTo(tcs2);
            Assert.True(tcs2.Task.IsCanceled);

            tcs1 = new TaskCompletionSource<GenericParameterHelper>();
            tcs2 = new TaskCompletionSource<GenericParameterHelper>();
            tcs1.SetException(new ApplicationException());
            ((Task)tcs1.Task).ApplyResultTo(tcs2);
            Assert.Same(tcs1.Task.Exception.InnerException, tcs2.Task.Exception.InnerException);
        }

        [Fact]
        public void WaitWithoutInlining()
        {
            var sluggishScheduler = new SluggishInliningTaskScheduler();
            var originalThread = Thread.CurrentThread;
            var task = Task.Factory.StartNew(
                delegate
                {
                    Assert.NotSame(originalThread, Thread.CurrentThread);
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                sluggishScheduler);

            // Schedule the task such that we'll be very likely to call WaitWithoutInlining
            // *before* the task is scheduled to run on its own.
            sluggishScheduler.ScheduleTasksLater();

            task.WaitWithoutInlining();
        }

        [Fact]
        public void WaitWithoutInlining_DoesNotWaitForOtherInlinedContinuations()
        {
            while (true)
            {
                var sluggishScheduler = new SluggishInliningTaskScheduler();

                var task = Task.Delay(200); // This must not complete before we call WaitWithoutInlining.
                var continuationUnblocked = new ManualResetEventSlim();
                var continuationTask = task.ContinueWith(
                    delegate
                    {
                        Assert.True(continuationUnblocked.Wait(UnexpectedTimeout));
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    sluggishScheduler); // ensures the continuation never runs unless inlined
                if (continuationTask.IsCompleted)
                {
                    // Evidently our Delay task completed too soon (and our continuationTask likely faulted due to timeout).
                    // Start over.
                    continue;
                }

                task.WaitWithoutInlining();
                continuationUnblocked.Set();
                continuationTask.GetAwaiter().GetResult();
                break;
            }
        }

        [Fact]
        public void WaitWithoutInlining_Faulted()
        {
            var tcs = new TaskCompletionSource<int>();
            tcs.SetException(new InvalidOperationException());
            try
            {
                tcs.Task.WaitWithoutInlining();
                Assert.False(true, "Expected exception not thrown.");
            }
            catch (AggregateException ex)
            {
                ex.Handle(x => x is InvalidOperationException);
            }
        }

        [Fact]
        public void WaitWithoutInlining_Canceled()
        {
            var tcs = new TaskCompletionSource<int>();
            tcs.SetCanceled();
            try
            {
                tcs.Task.WaitWithoutInlining();
                Assert.False(true, "Expected exception not thrown.");
            }
            catch (AggregateException ex)
            {
                ex.Handle(x => x is TaskCanceledException);
            }
        }

        [Fact]
        public void WaitWithoutInlining_AttachToParent()
        {
            Task attachedTask = null;
            int originalThreadId = Environment.CurrentManagedThreadId;
            var task = Task.Factory.StartNew(
                delegate
                {
                    attachedTask = Task.Factory.StartNew(
                        delegate
                        {
                            Assert.NotEqual(originalThreadId, Environment.CurrentManagedThreadId);
                        },
                        CancellationToken.None,
                        TaskCreationOptions.AttachedToParent,
                        TaskScheduler.Default);
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);
            task.WaitWithoutInlining();
            attachedTask.GetAwaiter().GetResult(); // rethrow any exceptions
        }

        [Fact]
        public async Task NoThrowAwaitable()
        {
            var tcs = new TaskCompletionSource<object>();
            var nothrowTask = tcs.Task.NoThrowAwaitable();
            Assert.False(nothrowTask.GetAwaiter().IsCompleted);
            tcs.SetException(new InvalidOperationException());
            await nothrowTask;

            tcs = new TaskCompletionSource<object>();
            nothrowTask = tcs.Task.NoThrowAwaitable();
            Assert.False(nothrowTask.GetAwaiter().IsCompleted);
            tcs.SetCanceled();
            await nothrowTask;
        }

        /// <summary>
        /// Verifies that independent of whether the <see cref="SynchronizationContext" /> or <see cref="TaskScheduler"/>
        /// is captured and used to schedule the continuation, the <see cref="ExecutionContext"/> is always captured and applied.
        /// </summary>
        [Theory]
        [CombinatorialData]
        public async Task NoThrowAwaitable_Await_CapturesExecutionContext(bool captureContext)
        {
            var awaitableTcs = new TaskCompletionSource<object>();
            var asyncLocal = new AsyncLocal<object>();
            asyncLocal.Value = "expected";
            var testResult = Task.Run(async delegate
            {
                await awaitableTcs.Task.NoThrowAwaitable(captureContext); // uses UnsafeOnCompleted
                Assert.Equal("expected", asyncLocal.Value);
            });
            asyncLocal.Value = null;
            await Task.Delay(AsyncDelay); // Make sure the delegate above has time to yield
            awaitableTcs.SetResult(null);

            await testResult.WithTimeout(UnexpectedTimeout);
        }

        /// <summary>
        /// Verifies that independent of whether the <see cref="SynchronizationContext" /> or <see cref="TaskScheduler"/>
        /// is captured and used to schedule the continuation, the <see cref="ExecutionContext"/> is always captured and applied.
        /// </summary>
        [Theory]
        [CombinatorialData]
        public async Task NoThrowAwaitable_OnCompleted_CapturesExecutionContext(bool captureContext)
        {
            var testResultTcs = new TaskCompletionSource<object>();
            var awaitableTcs = new TaskCompletionSource<object>();
            var asyncLocal = new AsyncLocal<object>();
            asyncLocal.Value = "expected";
            var awaiter = awaitableTcs.Task.NoThrowAwaitable(captureContext).GetAwaiter();
            awaiter.OnCompleted(delegate
            {
                try
                {
                    Assert.Equal("expected", asyncLocal.Value);
                    testResultTcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    testResultTcs.SetException(ex);
                }
            });
            asyncLocal.Value = null;
            await Task.Yield();
            awaitableTcs.SetResult(null);

            await testResultTcs.Task.WithTimeout(UnexpectedTimeout);
        }

        [Theory]
        [CombinatorialData]
        public async Task NoThrowAwaitable_UnsafeOnCompleted_DoesNotCaptureExecutionContext(bool captureContext)
        {
            var testResultTcs = new TaskCompletionSource<object>();
            var awaitableTcs = new TaskCompletionSource<object>();
            var asyncLocal = new AsyncLocal<object>();
            asyncLocal.Value = "expected";
            var awaiter = awaitableTcs.Task.NoThrowAwaitable(captureContext).GetAwaiter();
            awaiter.UnsafeOnCompleted(delegate
            {
                try
                {
                    Assert.Null(asyncLocal.Value);
                    testResultTcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    testResultTcs.SetException(ex);
                }
            });
            asyncLocal.Value = null;
            await Task.Yield();
            awaitableTcs.SetResult(null);

            await testResultTcs.Task.WithTimeout(UnexpectedTimeout);
        }

        [Fact]
        public void InvokeAsyncNullEverything()
        {
            AsyncEventHandler handler = null;
            var task = handler.InvokeAsync(null, null);
            Assert.True(task.IsCompleted);
        }

        [Fact]
        public void InvokeAsyncParametersCarry()
        {
            InvokeAsyncHelper(null, null);
            InvokeAsyncHelper(new object(), new EventArgs());
        }

        [Fact]
        public void InvokeAsyncOfTNullEverything()
        {
            AsyncEventHandler<EventArgs> handler = null;
            var task = handler.InvokeAsync(null, null);
            Assert.True(task.IsCompleted);
        }

        [Fact]
        public void InvokeAsyncOfTParametersCarry()
        {
            InvokeAsyncOfTHelper(null, null);
            InvokeAsyncHelper(new object(), new EventArgs());
        }

        [Fact]
        public void InvokeAsyncExecutesEachHandlerSequentially()
        {
            AsyncEventHandler handlers = null;
            int counter = 0;
            handlers += async (sender, args) =>
            {
                Assert.Equal(1, ++counter);
                await Task.Yield();
                Assert.Equal(2, ++counter);
            };
            handlers += async (sender, args) =>
            {
                Assert.Equal(3, ++counter);
                await Task.Yield();
                Assert.Equal(4, ++counter);
            };
            var task = handlers.InvokeAsync(null, null);
            task.GetAwaiter().GetResult();
        }

        [Fact]
        public void InvokeAsyncOfTExecutesEachHandlerSequentially()
        {
            AsyncEventHandler<EventArgs> handlers = null;
            int counter = 0;
            handlers += async (sender, args) =>
            {
                Assert.Equal(1, ++counter);
                await Task.Yield();
                Assert.Equal(2, ++counter);
            };
            handlers += async (sender, args) =>
            {
                Assert.Equal(3, ++counter);
                await Task.Yield();
                Assert.Equal(4, ++counter);
            };
            var task = handlers.InvokeAsync(null, null);
            task.GetAwaiter().GetResult();
        }

        [Fact]
        public void InvokeAsyncAggregatesExceptions()
        {
            AsyncEventHandler handlers = null;
            handlers += (sender, args) =>
            {
                throw new ApplicationException("a");
            };
            handlers += async (sender, args) =>
            {
                await Task.Yield();
                throw new ApplicationException("b");
            };
            var task = handlers.InvokeAsync(null, null);
            try
            {
                task.GetAwaiter().GetResult();
                Assert.True(false, "Expected AggregateException not thrown.");
            }
            catch (AggregateException ex)
            {
                Assert.Equal(2, ex.InnerExceptions.Count);
                Assert.Equal("a", ex.InnerExceptions[0].Message);
                Assert.Equal("b", ex.InnerExceptions[1].Message);
            }
        }

        [Fact]
        public void InvokeAsyncOfTAggregatesExceptions()
        {
            AsyncEventHandler<EventArgs> handlers = null;
            handlers += (sender, args) =>
            {
                throw new ApplicationException("a");
            };
            handlers += async (sender, args) =>
            {
                await Task.Yield();
                throw new ApplicationException("b");
            };
            var task = handlers.InvokeAsync(null, null);
            try
            {
                task.GetAwaiter().GetResult();
                Assert.True(false, "Expected AggregateException not thrown.");
            }
            catch (AggregateException ex)
            {
                Assert.Equal(2, ex.InnerExceptions.Count);
                Assert.Equal("a", ex.InnerExceptions[0].Message);
                Assert.Equal("b", ex.InnerExceptions[1].Message);
            }
        }

        [Fact]
        public void FollowCancelableTaskToCompletionEndsInCompletion()
        {
            var currentTCS = new TaskCompletionSource<int>();
            Task<int> latestTask = currentTCS.Task;
            var followingTask = TplExtensions.FollowCancelableTaskToCompletion(() => latestTask, CancellationToken.None);

            for (int i = 0; i < 3; i++)
            {
                var oldTCS = currentTCS;
                currentTCS = new TaskCompletionSource<int>();
                latestTask = currentTCS.Task;
                oldTCS.SetCanceled();
            }

            currentTCS.SetResult(3);
            Assert.Equal(3, followingTask.Result);
        }

        [Fact]
        public void FollowCancelableTaskToCompletionEndsInCompletionWithSpecifiedTaskSource()
        {
            var specifiedTaskSource = new TaskCompletionSource<int>();
            var currentTCS = new TaskCompletionSource<int>();
            Task<int> latestTask = currentTCS.Task;
            var followingTask = TplExtensions.FollowCancelableTaskToCompletion(() => latestTask, CancellationToken.None, specifiedTaskSource);
            Assert.Same(specifiedTaskSource.Task, followingTask);

            for (int i = 0; i < 3; i++)
            {
                var oldTCS = currentTCS;
                currentTCS = new TaskCompletionSource<int>();
                latestTask = currentTCS.Task;
                oldTCS.SetCanceled();
            }

            currentTCS.SetResult(3);
            Assert.Equal(3, followingTask.Result);
        }

        [Fact]
        public void FollowCancelableTaskToCompletionEndsInUltimateCancellation()
        {
            var currentTCS = new TaskCompletionSource<int>();
            Task<int> latestTask = currentTCS.Task;
            var cts = new CancellationTokenSource();
            var followingTask = TplExtensions.FollowCancelableTaskToCompletion(() => latestTask, cts.Token);

            for (int i = 0; i < 3; i++)
            {
                var oldTCS = currentTCS;
                currentTCS = new TaskCompletionSource<int>();
                latestTask = currentTCS.Task;
                oldTCS.SetCanceled();
            }

            cts.Cancel();
            Assert.True(followingTask.IsCanceled);
        }

        [Fact]
        public void FollowCancelableTaskToCompletionEndsInFault()
        {
            var currentTCS = new TaskCompletionSource<int>();
            Task<int> latestTask = currentTCS.Task;
            var followingTask = TplExtensions.FollowCancelableTaskToCompletion(() => latestTask, CancellationToken.None);

            for (int i = 0; i < 3; i++)
            {
                var oldTCS = currentTCS;
                currentTCS = new TaskCompletionSource<int>();
                latestTask = currentTCS.Task;
                oldTCS.SetCanceled();
            }

            currentTCS.SetException(new InvalidOperationException());
            Assert.IsType(typeof(InvalidOperationException), followingTask.Exception.InnerException);
        }

        [Fact]
        public async Task ToApmOfTWithNoTaskState()
        {
            var state = new object();
            var tcs = new TaskCompletionSource<int>();
            IAsyncResult beginResult = null;

            var callbackResult = new TaskCompletionSource<object>();
            AsyncCallback callback = ar =>
            {
                try
                {
                    Assert.Same(beginResult, ar);
                    Assert.Equal(5, EndTestOperation<int>(ar));
                    callbackResult.SetResult(null);
                }
                catch (Exception ex)
                {
                    callbackResult.SetException(ex);
                }
            };
            beginResult = BeginTestOperation(callback, state, tcs.Task);
            Assert.Same(state, beginResult.AsyncState);
            tcs.SetResult(5);
            await callbackResult.Task;
        }

        [Fact]
        public async Task ToApmOfTWithMatchingTaskState()
        {
            var state = new object();
            var tcs = new TaskCompletionSource<int>(state);
            IAsyncResult beginResult = null;

            var callbackResult = new TaskCompletionSource<object>();
            AsyncCallback callback = ar =>
            {
                try
                {
                    Assert.Same(beginResult, ar);
                    Assert.Equal(5, EndTestOperation<int>(ar));
                    callbackResult.SetResult(null);
                }
                catch (Exception ex)
                {
                    callbackResult.SetException(ex);
                }
            };
            beginResult = BeginTestOperation(callback, state, tcs.Task);
            Assert.Same(state, beginResult.AsyncState);
            tcs.SetResult(5);
            await callbackResult.Task;
        }

        [Fact]
        public async Task ToApmWithNoTaskState()
        {
            var state = new object();
            var tcs = new TaskCompletionSource<object>();
            IAsyncResult beginResult = null;

            var callbackResult = new TaskCompletionSource<object>();
            AsyncCallback callback = ar =>
            {
                try
                {
                    Assert.Same(beginResult, ar);
                    EndTestOperation(ar);
                    callbackResult.SetResult(null);
                }
                catch (Exception ex)
                {
                    callbackResult.SetException(ex);
                }
            };
            beginResult = BeginTestOperation(callback, state, (Task)tcs.Task);
            Assert.Same(state, beginResult.AsyncState);
            tcs.SetResult(null);
            await callbackResult.Task;
        }

        [Fact]
        public async Task ToApmWithMatchingTaskState()
        {
            var state = new object();
            var tcs = new TaskCompletionSource<object>(state);
            IAsyncResult beginResult = null;

            var callbackResult = new TaskCompletionSource<object>();
            AsyncCallback callback = ar =>
            {
                try
                {
                    Assert.Same(beginResult, ar);
                    EndTestOperation(ar);
                    callbackResult.SetResult(null);
                }
                catch (Exception ex)
                {
                    callbackResult.SetException(ex);
                }
            };
            beginResult = BeginTestOperation(callback, state, (Task)tcs.Task);
            Assert.Same(state, beginResult.AsyncState);
            tcs.SetResult(null);
            await callbackResult.Task;
        }

#if DESKTOP || NETCOREAPP2_0

        [Fact]
        public void ToTaskReturnsCompletedTaskPreSignaled()
        {
            var handle = new ManualResetEvent(initialState: true);
            Task<bool> actual = TplExtensions.ToTask(handle);
            Assert.Same(TplExtensions.TrueTask, actual);
        }

        [Fact]
        public async Task ToTaskOnHandleSignaledLater()
        {
            var handle = new ManualResetEvent(initialState: false);
            Task<bool> actual = TplExtensions.ToTask(handle);
            Assert.False(actual.IsCompleted);
            handle.Set();
            bool result = await actual;
            Assert.True(result);
        }

        [Fact]
        public void ToTaskUnsignaledHandleWithZeroTimeout()
        {
            var handle = new ManualResetEvent(initialState: false);
            Task<bool> actual = TplExtensions.ToTask(handle, timeout: 0);
            Assert.Same(TplExtensions.FalseTask, actual);
        }

        [Fact]
        public void ToTaskSignaledHandleWithZeroTimeout()
        {
            var handle = new ManualResetEvent(initialState: true);
            Task<bool> actual = TplExtensions.ToTask(handle, timeout: 0);
            Assert.Same(TplExtensions.TrueTask, actual);
        }

        [Fact]
        public async Task ToTaskOnHandleSignaledAfterNonZeroTimeout()
        {
            using (var handle = new ManualResetEvent(initialState: false))
            {
                Task<bool> actual = TplExtensions.ToTask(handle, timeout: 1);
                bool result = await actual.WithTimeout(TimeSpan.FromMilliseconds(AsyncDelay));
                Assert.False(result);
            }
        }

        [Fact]
        public void ToTaskOnHandleSignaledAfterCancellation()
        {
            var handle = new ManualResetEvent(initialState: false);
            var cts = new CancellationTokenSource();
            Task<bool> actual = TplExtensions.ToTask(handle, cancellationToken: cts.Token);
            cts.Cancel();
            Assert.True(actual.IsCanceled);
            handle.Set();
        }

        [Fact]
        public async Task ToTaskOnDisposedHandle()
        {
            var handle = new ManualResetEvent(false);
            handle.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => TplExtensions.ToTask(handle));
        }

#endif

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WithTimeout_NullTask(bool generic)
        {
            // Verify that a faulted task is returned instead of throwing.
            Task timeoutTask = generic
                ? TplExtensions.WithTimeout<int>(null, TimeSpan.FromSeconds(1))
                : TplExtensions.WithTimeout(null, TimeSpan.FromSeconds(1));
            Assert.Throws<ArgumentNullException>(() => timeoutTask.GetAwaiter().GetResult());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WithTimeout_MinusOneMeansInfiniteTimeout(bool generic)
        {
            this.ExecuteOnDispatcher(async delegate
            {
                var tcs = new TaskCompletionSource<object>();
                var timeoutTask = generic
                    ? TplExtensions.WithTimeout<object>(tcs.Task, TimeSpan.FromMilliseconds(-1))
                    : TplExtensions.WithTimeout((Task)tcs.Task, TimeSpan.FromMilliseconds(-1));
                Assert.False(timeoutTask.IsCompleted);
                await Task.Delay(AsyncDelay / 2);
                Assert.False(timeoutTask.IsCompleted);
                tcs.SetResult(null);
                timeoutTask.GetAwaiter().GetResult();
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WithTimeout_TimesOut(bool generic)
        {
            // Use a SynchronizationContext to ensure that we never deadlock even when synchronously blocking.
            this.ExecuteOnDispatcher(delegate
            {
                var tcs = new TaskCompletionSource<object>();
                Task timeoutTask = generic
                    ? tcs.Task.WithTimeout(TimeSpan.FromMilliseconds(1))
                    : ((Task)tcs.Task).WithTimeout(TimeSpan.FromMilliseconds(1));
                Assert.Throws<TimeoutException>(() => timeoutTask.GetAwaiter().GetResult()); // sync block to ensure no deadlock occurs
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WithTimeout_CompletesFirst(bool generic)
        {
            // Use a SynchronizationContext to ensure that we never deadlock even when synchronously blocking.
            this.ExecuteOnDispatcher(delegate
            {
                var tcs = new TaskCompletionSource<object>();
                Task timeoutTask = generic
                    ? tcs.Task.WithTimeout(TimeSpan.FromDays(1))
                    : ((Task)tcs.Task).WithTimeout(TimeSpan.FromDays(1));
                Assert.False(timeoutTask.IsCompleted);
                tcs.SetResult(null);
                timeoutTask.GetAwaiter().GetResult();
            });
        }

        [Fact]
        public void WithTimeout_CompletesFirstWithResult()
        {
            // Use a SynchronizationContext to ensure that we never deadlock even when synchronously blocking.
            this.ExecuteOnDispatcher(delegate
            {
                var tcs = new TaskCompletionSource<object>();
                var timeoutTask = tcs.Task.WithTimeout(TimeSpan.FromDays(1));
                Assert.False(timeoutTask.IsCompleted);
                tcs.SetResult("success");
                Assert.Same(tcs.Task.Result, timeoutTask.Result);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WithTimeout_CompletesFirstAndThrows(bool generic)
        {
            // Use a SynchronizationContext to ensure that we never deadlock even when synchronously blocking.
            this.ExecuteOnDispatcher(async delegate
            {
                var tcs = new TaskCompletionSource<object>();
                Task timeoutTask = generic
                    ? tcs.Task.WithTimeout(TimeSpan.FromDays(1))
                    : ((Task)tcs.Task).WithTimeout(TimeSpan.FromDays(1));
                Assert.False(timeoutTask.IsCompleted);
                tcs.SetException(new ApplicationException());
                await Assert.ThrowsAsync<ApplicationException>(() => timeoutTask);
                Assert.Same(tcs.Task.Exception.InnerException, timeoutTask.Exception.InnerException);
            });
        }

        private static void InvokeAsyncHelper(object sender, EventArgs args)
        {
            int invoked = 0;
            AsyncEventHandler handler = (s, a) =>
            {
                Assert.Same(sender, s);
                Assert.Same(args, a);
                invoked++;
                return TplExtensions.CompletedTask;
            };
            var task = handler.InvokeAsync(sender, args);
            Assert.True(task.IsCompleted);
            Assert.Equal(1, invoked);
        }

        private static void InvokeAsyncOfTHelper(object sender, EventArgs args)
        {
            int invoked = 0;
            AsyncEventHandler<EventArgs> handler = (s, a) =>
            {
                Assert.Same(sender, s);
                Assert.Same(args, a);
                invoked++;
                return TplExtensions.CompletedTask;
            };
            var task = handler.InvokeAsync(sender, args);
            Assert.True(task.IsCompleted);
            Assert.Equal(1, invoked);
        }

        private static IAsyncResult BeginTestOperation<T>(AsyncCallback callback, object state, Task<T> asyncTask)
        {
            return asyncTask.ToApm(callback, state);
        }

        private static IAsyncResult BeginTestOperation(AsyncCallback callback, object state, Task asyncTask)
        {
            return asyncTask.ToApm(callback, state);
        }

        private static T EndTestOperation<T>(IAsyncResult asyncResult)
        {
            return ((Task<T>)asyncResult).Result;
        }

        private static void EndTestOperation(IAsyncResult asyncResult)
        {
            ((Task)asyncResult).Wait(); // rethrow exceptions
        }

        /// <summary>
        /// A TaskScheduler that doesn't schedule tasks right away,
        /// allowing inlining tests to deterministically pass or fail.
        /// </summary>
        private class SluggishInliningTaskScheduler : TaskScheduler
        {
            private readonly Queue<Task> tasks = new Queue<Task>();

            internal void ScheduleTasksLater(int delay = AsyncDelay)
            {
                Task.Delay(delay).ContinueWith(
                    _ => this.ScheduleTasksNow(),
                    TaskScheduler.Default);
            }

            internal void ScheduleTasksNow()
            {
                lock (this.tasks)
                {
                    while (this.tasks.Count > 0)
                    {
                        Task.Run(() => this.TryExecuteTask(this.tasks.Dequeue()));
                    }
                }
            }

            protected override IEnumerable<Task> GetScheduledTasks()
            {
                throw new NotImplementedException();
            }

            protected override void QueueTask(Task task)
            {
                lock (this.tasks)
                {
                    this.tasks.Enqueue(task);
                }
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return this.TryExecuteTask(task);
            }
        }
    }
}
