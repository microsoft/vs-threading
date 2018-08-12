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
        public void CtorNullAction()
        {
            Assert.Throws<ArgumentNullException>(() => new ProgressWithCompletion<GenericParameterHelper>((Action<GenericParameterHelper>)null));
        }

        [Fact]
        public void CtorNullFuncOfTask()
        {
            Assert.Throws<ArgumentNullException>(() => new ProgressWithCompletion<GenericParameterHelper>((Func<GenericParameterHelper, Task>)null));
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
        public void SynchronizationContextCaptured()
        {
            var syncContext = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(syncContext);
            Exception callbackError = null;
            var callback = new Action<GenericParameterHelper>(
                p =>
                {
                    try
                    {
                        Assert.Same(syncContext, SynchronizationContext.Current);
                    }
                    catch (Exception e)
                    {
                        callbackError = e;
                    }
                });
            var progress = new ProgressWithCompletion<GenericParameterHelper>(callback);
            IProgress<GenericParameterHelper> reporter = progress;

            Task.Run(delegate
            {
                reporter.Report(new GenericParameterHelper(1));
            });

            if (callbackError != null)
            {
                throw callbackError;
            }
        }
    }
}
