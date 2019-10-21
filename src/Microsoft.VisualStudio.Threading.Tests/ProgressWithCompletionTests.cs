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

    public class ProgressWithCompletionTests : TestBase
    {
        public ProgressWithCompletionTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Fact]
        public void Ctor_Nulls()
        {
            Assert.Throws<ArgumentNullException>(() => new ProgressWithCompletion<GenericParameterHelper>((Action<GenericParameterHelper>?)null));
            Assert.Throws<ArgumentNullException>(() => new ProgressWithCompletion<GenericParameterHelper>((Func<GenericParameterHelper, Task>?)null));
        }

        [Fact]
        public void Ctor_NullJtf()
        {
            var progress = new ProgressWithCompletion<GenericParameterHelper>(v => { }, joinableTaskFactory: null);
            progress = new ProgressWithCompletion<GenericParameterHelper>(v => TplExtensions.CompletedTask, joinableTaskFactory: null);
        }

        [Fact]
        public void NoWorkAction()
        {
            var callback = new Action<GenericParameterHelper>(p => { });
            var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
            Assert.True(progress.WaitAsync().IsCompleted);
        }

        [Fact]
        public void NoWorkFuncOfTask()
        {
            var callback = new Func<GenericParameterHelper, Task>(p => { return TplExtensions.CompletedTask; });
            var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
            Assert.True(progress.WaitAsync().IsCompleted);
        }

        [Fact]
        public async Task WaitAsync()
        {
            var handlerMayComplete = new AsyncManualResetEvent();
            var callback = new Func<GenericParameterHelper, Task>(
                async p =>
                {
                    Assert.Equal(1, p.Data);
                    await handlerMayComplete;
                });
            var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
            IProgress<GenericParameterHelper> reporter = progress;
            reporter.Report(new GenericParameterHelper(1));

            var progressAwaitable = progress.WaitAsync();
            Assert.False(progressAwaitable.GetAwaiter().IsCompleted);
            await Task.Delay(AsyncDelay);
            Assert.False(progressAwaitable.GetAwaiter().IsCompleted);
            handlerMayComplete.Set();
            await progressAwaitable;
        }

        [Fact]
        public async Task WaitAsync_CancellationToken()
        {
            var handlerMayComplete = new AsyncManualResetEvent();
            var callback = new Func<GenericParameterHelper, Task>(
                async p =>
                {
                    Assert.Equal(1, p.Data);
                    await handlerMayComplete;
                });
            var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
            IProgress<GenericParameterHelper> reporter = progress;
            reporter.Report(new GenericParameterHelper(1));

            var cts = new CancellationTokenSource();
            var progressAwaitable = progress.WaitAsync(cts.Token);
            await Task.Delay(AsyncDelay);
            Assert.False(progressAwaitable.GetAwaiter().IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => progressAwaitable).WithCancellation(this.TimeoutToken);

            // clean up
            handlerMayComplete.Set();
        }

        [Fact]
        public void SynchronizationContextCaptured()
        {
            var syncContext = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(syncContext);
            TaskCompletionSource<object> callbackResult = new TaskCompletionSource<object>();
            var frame = SingleThreadedTestSynchronizationContext.NewFrame();
            var callback = new Action<GenericParameterHelper>(
                p =>
                {
                    try
                    {
                        Assert.NotNull(SynchronizationContext.Current);
                        callbackResult.SetResult(null);
                    }
                    catch (Exception e)
                    {
                        callbackResult.SetException(e);
                    }

                    frame.Continue = false;
                });
            var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
            IProgress<GenericParameterHelper> reporter = progress;

            Task.Run(delegate
            {
                reporter.Report(new GenericParameterHelper(1));
            });

            SingleThreadedTestSynchronizationContext.PushFrame(syncContext, frame);
            callbackResult.Task.GetAwaiter().GetResult();
        }

        [Theory]
        [PairwiseData]
        public void DoesNotDeadlockWhenCallbackCapturesSyncContext(bool captureMainThreadContext)
        {
            var syncContext = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(syncContext);
            var jtc = new JoinableTaskContext();

            const int expectedCallbackValue = 1;
            var actualCallbackValue = new TaskCompletionSource<int>();
            Func<ProgressWithCompletion<GenericParameterHelper>> progressFactory = () => new ProgressWithCompletion<GenericParameterHelper>(async arg =>
            {
                try
                {
                    Assert.Equal(captureMainThreadContext, jtc.IsOnMainThread);

                    // Ensure we have a main thread dependency even if we started on a threadpool thread.
                    await jtc.Factory.SwitchToMainThreadAsync(this.TimeoutToken);
                    actualCallbackValue.SetResult(arg.Data);
                }
                catch (Exception ex)
                {
                    actualCallbackValue.SetException(ex);
                }
            }, jtc.Factory);

            ProgressWithCompletion<GenericParameterHelper> progress;
            if (captureMainThreadContext)
            {
                progress = progressFactory();
            }
            else
            {
                var progressTask = Task.Run(progressFactory);
                progressTask.WaitWithoutInlining();
                progress = progressTask.Result;
            }

            IProgress<GenericParameterHelper> progressReporter = progress;
            progressReporter.Report(new GenericParameterHelper(expectedCallbackValue));
            Assert.False(actualCallbackValue.Task.IsCompleted);

            // Block the "main thread" while waiting for the reported progress to be executed.
            // Since the callback must execute on the main thread, this will deadlock unless
            // the underlying code is JTF aware, which is the whole point of this test to confirm.
            jtc.Factory.Run(async delegate
            {
                await progress.WaitAsync(this.TimeoutToken);
                Assert.True(actualCallbackValue.Task.IsCompleted);
                Assert.Equal(expectedCallbackValue, await actualCallbackValue.Task);
            });
        }
    }
}
