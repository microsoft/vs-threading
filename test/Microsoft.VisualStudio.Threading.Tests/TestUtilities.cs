// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Xunit;
using Xunit.Abstractions;

internal static class TestUtilities
{
    internal static Task SetAsync(this TaskCompletionSource<object?> tcs)
    {
        return Task.Run(() => tcs.TrySetResult(null));
    }

    /// <summary>
    /// Runs an asynchronous task synchronously, using just the current thread to execute continuations.
    /// </summary>
    internal static void Run(Func<Task> func)
    {
        if (func is null)
        {
            throw new ArgumentNullException(nameof(func));
        }

        SynchronizationContext? prevCtx = SynchronizationContext.Current;
        try
        {
            SynchronizationContext? syncCtx = SingleThreadedTestSynchronizationContext.New();
            SynchronizationContext.SetSynchronizationContext(syncCtx);

            Task? t = func();
            if (t is null)
            {
                throw new InvalidOperationException();
            }

            SingleThreadedTestSynchronizationContext.IFrame? frame = SingleThreadedTestSynchronizationContext.NewFrame();
            t.ContinueWith(_ => { frame.Continue = false; }, TaskScheduler.Default);
            SingleThreadedTestSynchronizationContext.PushFrame(syncCtx, frame);

            t.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevCtx);
        }
    }

    /// <summary>
    /// Executes the specified function on multiple threads simultaneously.
    /// </summary>
    /// <typeparam name="T">The type of the value returned by the specified function.</typeparam>
    /// <param name="action">The function to invoke concurrently.</param>
    /// <param name="concurrency">The level of concurrency.</param>
    internal static T[] ConcurrencyTest<T>(Func<T> action, int concurrency = -1)
    {
        Requires.NotNull(action, nameof(action));
        if (concurrency == -1)
        {
            concurrency = Environment.ProcessorCount;
        }

        Skip.If(Environment.ProcessorCount < concurrency, $"The test machine does not have enough CPU cores to exercise a concurrency level of {concurrency}");

        // We use a barrier to guarantee that all threads are fully ready to
        // execute the provided function at precisely the same time.
        // The barrier will unblock all of them together.
        using (var barrier = new Barrier(concurrency))
        {
            var tasks = new Task<T>[concurrency];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(delegate
                {
                    barrier.SignalAndWait();
                    return action();
                });
            }

            Task.WaitAll(tasks);
            return tasks.Select(t => t.Result).ToArray();
        }
    }

    internal static DebugAssertionRevert DisableAssertionDialog()
    {
#if NETFRAMEWORK
        DefaultTraceListener? listener = Debug.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
        if (listener is object)
        {
            listener.AssertUiEnabled = false;
        }
#elif NET5_0
        Trace.Listeners.Clear();
#endif

        return default(DebugAssertionRevert);
    }

    internal static void CompleteSynchronously(this JoinableTaskFactory factory, JoinableTaskCollection collection, Task task)
    {
        Requires.NotNull(factory, nameof(factory));
        Requires.NotNull(collection, nameof(collection));
        Requires.NotNull(task, nameof(task));

        factory.Run(async delegate
        {
            using (collection.Join())
            {
                await task;
            }
        });
    }

    /// <summary>
    /// Forces an awaitable to yield, setting signals after the continuation has been pended and when the continuation has begun execution.
    /// </summary>
    /// <param name="baseAwaiter">The awaiter to extend.</param>
    /// <param name="yieldingSignal">The signal to set after the continuation has been pended.</param>
    /// <param name="resumingSignal">The signal to set when the continuation has been invoked.</param>
    /// <returns>A new awaitable.</returns>
    internal static YieldAndNotifyAwaitable YieldAndNotify(this INotifyCompletion baseAwaiter, AsyncManualResetEvent? yieldingSignal = null, AsyncManualResetEvent? resumingSignal = null)
    {
        Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

        return new YieldAndNotifyAwaitable(baseAwaiter, yieldingSignal, resumingSignal);
    }

    /// <summary>
    /// Flood the threadpool with requests that will just block the threads
    /// until the returned value is disposed of.
    /// </summary>
    /// <returns>A value to dispose of to unblock the threadpool.</returns>
    /// <remarks>
    /// This can provide a unique technique for influencing execution order
    /// of synchronous code vs. async code.
    /// </remarks>
    internal static IDisposable StarveThreadpool()
    {
        ThreadPool.GetMaxThreads(out int workerThreads, out int completionPortThreads);
        var disposalTokenSource = new CancellationTokenSource();
        var unblockThreadpool = new ManualResetEventSlim();
        for (int i = 0; i < workerThreads; i++)
        {
            Task.Run(
                () => unblockThreadpool.Wait(disposalTokenSource.Token),
                disposalTokenSource.Token);
        }

        return new DisposalAction(disposalTokenSource.Cancel);
    }

    /// <summary>
    /// Executes the specified test method in its own process, offering maximum isolation from ambient noise from other threads
    /// and GC.
    /// </summary>
    /// <param name="testClass">The instance of the test class containing the method to be run in isolation.</param>
    /// <param name="testMethodName">The name of the test method.</param>
    /// <param name="logger">An optional logger to forward any <see cref="ITestOutputHelper"/> output to from the isolated test runner.</param>
    /// <returns>
    /// A task whose result is <c>true</c> if test execution is already isolated and should therefore proceed with the body of the test,
    /// or <c>false</c> after the isolated instance of the test has completed execution.
    /// </returns>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown if the isolated test result is a Failure.</exception>
    /// <exception cref="SkipException">Thrown if on a platform that we do not yet support test isolation on.</exception>
    internal static Task<bool> ExecuteInIsolationAsync(object testClass, string testMethodName, ITestOutputHelper logger)
    {
        Requires.NotNull(testClass, nameof(testClass));
        return ExecuteInIsolationAsync(testClass.GetType().FullName!, testMethodName, logger);
    }

    /// <summary>
    /// Executes the specified test method in its own process, offering maximum isolation from ambient noise from other threads
    /// and GC.
    /// </summary>
    /// <param name="testClassName">The full name of the test class.</param>
    /// <param name="testMethodName">The name of the test method.</param>
    /// <param name="logger">An optional logger to forward any <see cref="ITestOutputHelper"/> output to from the isolated test runner.</param>
    /// <returns>
    /// A task whose result is <c>true</c> if test execution is already isolated and should therefore proceed with the body of the test,
    /// or <c>false</c> after the isolated instance of the test has completed execution.
    /// </returns>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown if the isolated test result is a Failure.</exception>
    /// <exception cref="SkipException">Thrown if on a platform that we do not yet support test isolation on.</exception>
#pragma warning disable CA1801 // Review unused parameters
    internal static Task<bool> ExecuteInIsolationAsync(string testClassName, string testMethodName, ITestOutputHelper logger)
#pragma warning restore CA1801 // Review unused parameters
    {
        Requires.NotNullOrEmpty(testClassName, nameof(testClassName));
        Requires.NotNullOrEmpty(testMethodName, nameof(testMethodName));

#if NETFRAMEWORK
        const string testHostProcessName = "IsolatedTestHost.exe";
        if (Process.GetCurrentProcess().ProcessName == Path.GetFileNameWithoutExtension(testHostProcessName))
        {
            return TplExtensions.TrueTask;
        }

        var startInfo = new ProcessStartInfo(
            testHostProcessName,
            AssemblyCommandLineArguments(
                Assembly.GetExecutingAssembly().Location,
                testClassName,
                testMethodName))
        {
            RedirectStandardError = logger is object,
            RedirectStandardOutput = logger is object,
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        Process isolatedTestProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        var processExitCode = new TaskCompletionSource<IsolatedTestHost.ExitCode>();
        isolatedTestProcess.Exited += (s, e) =>
        {
            processExitCode.SetResult((IsolatedTestHost.ExitCode)isolatedTestProcess.ExitCode);
        };
        if (logger is object)
        {
            isolatedTestProcess.OutputDataReceived += (s, e) => logger.WriteLine(e.Data ?? string.Empty);
            isolatedTestProcess.ErrorDataReceived += (s, e) => logger.WriteLine(e.Data ?? string.Empty);
        }

        Assert.True(isolatedTestProcess.Start());

        if (logger is object)
        {
            isolatedTestProcess.BeginOutputReadLine();
            isolatedTestProcess.BeginErrorReadLine();
        }

        return processExitCode.Task.ContinueWith(
            t =>
            {
                switch (t.Result)
                {
                    case IsolatedTestHost.ExitCode.TestSkipped:
                        throw new SkipException("Test skipped. See output of isolated task for details.");
                    case IsolatedTestHost.ExitCode.TestPassed:
                    default:
                        Assert.Equal(IsolatedTestHost.ExitCode.TestPassed, t.Result);
                        break;
                }

                return false;
            },
            TaskScheduler.Default);
#else
        return Task.FromException<bool>(new SkipException("Test isolation is not yet supported on this platform."));
#endif
    }

    /// <summary>
    /// Wait on a task without possibly inlining it to the current thread.
    /// </summary>
    /// <param name="task">The task to wait on.</param>
    /// <param name="throwOriginalException"><c>true</c> to throw the original (inner) exception when the <paramref name="task"/> faults; <c>false</c> to throw <see cref="AggregateException"/>.</param>
    /// <exception cref="AggregateException">Thrown if <paramref name="task"/> completes in a faulted state if <paramref name="throwOriginalException"/> is <c>false</c>.</exception>
    internal static void WaitWithoutInlining(this Task task, bool throwOriginalException)
    {
        Requires.NotNull(task, nameof(task));
        if (!task.IsCompleted)
        {
            // Waiting on a continuation of a task won't ever inline the predecessor (in .NET 4.x anyway).
            Task? continuation = task.ContinueWith(t => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            continuation.Wait();
        }

        // Rethrow the exception if the task faulted.
        if (throwOriginalException)
        {
            task.GetAwaiter().GetResult();
        }
        else
        {
            task.Wait();
        }
    }

    /// <summary>
    /// Wait on a task without possibly inlining it to the current thread and returns its result.
    /// </summary>
    /// <typeparam name="T">The type of result returned from the <paramref name="task"/>.</typeparam>
    /// <param name="task">The task to wait on.</param>
    /// <param name="throwOriginalException"><c>true</c> to throw the original (inner) exception when the <paramref name="task"/> faults; <c>false</c> to throw <see cref="AggregateException"/>.</param>
    /// <returns>The result of the <see cref="Task{T}"/>.</returns>
    /// <exception cref="AggregateException">Thrown if <paramref name="task"/> completes in a faulted state if <paramref name="throwOriginalException"/> is <c>false</c>.</exception>
    internal static T GetResultWithoutInlining<T>(this Task<T> task, bool throwOriginalException = true)
    {
        WaitWithoutInlining(task, throwOriginalException);
        return task.Result;
    }

    private static string AssemblyCommandLineArguments(params string[] args) => string.Join(" ", args.Select(a => $"\"{a}\""));

    internal readonly struct YieldAndNotifyAwaitable
    {
        private readonly INotifyCompletion baseAwaiter;
        private readonly AsyncManualResetEvent? yieldingSignal;
        private readonly AsyncManualResetEvent? resumingSignal;

        internal YieldAndNotifyAwaitable(INotifyCompletion baseAwaiter, AsyncManualResetEvent? yieldingSignal, AsyncManualResetEvent? resumingSignal)
        {
            Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

            this.baseAwaiter = baseAwaiter;
            this.yieldingSignal = yieldingSignal;
            this.resumingSignal = resumingSignal;
        }

        public YieldAndNotifyAwaiter GetAwaiter()
        {
            return new YieldAndNotifyAwaiter(this.baseAwaiter, this.yieldingSignal, this.resumingSignal);
        }
    }

    internal readonly struct YieldAndNotifyAwaiter : INotifyCompletion
    {
        private readonly INotifyCompletion baseAwaiter;
        private readonly AsyncManualResetEvent? yieldingSignal;
        private readonly AsyncManualResetEvent? resumingSignal;

        internal YieldAndNotifyAwaiter(INotifyCompletion baseAwaiter, AsyncManualResetEvent? yieldingSignal, AsyncManualResetEvent? resumingSignal)
        {
            Requires.NotNull(baseAwaiter, nameof(baseAwaiter));

            this.baseAwaiter = baseAwaiter;
            this.yieldingSignal = yieldingSignal;
            this.resumingSignal = resumingSignal;
        }

        public bool IsCompleted
        {
            get { return false; }
        }

        public void OnCompleted(Action continuation)
        {
            YieldAndNotifyAwaiter that = this;
            this.baseAwaiter.OnCompleted(delegate
            {
                if (that.resumingSignal is object)
                {
                    that.resumingSignal.Set();
                }

                continuation();
            });
            if (this.yieldingSignal is object)
            {
                this.yieldingSignal.Set();
            }
        }

        public void GetResult()
        {
        }
    }

    internal readonly struct DebugAssertionRevert : IDisposable
    {
        public void Dispose()
        {
#if NETFRAMEWORK
            DefaultTraceListener? listener = Debug.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
            if (listener is object)
            {
                listener.AssertUiEnabled = true;
            }
#endif
        }
    }

    private class DisposalAction : IDisposable
    {
        private readonly Action disposeAction;

        internal DisposalAction(Action disposeAction)
        {
            this.disposeAction = disposeAction;
        }

        public void Dispose() => this.disposeAction();
    }
}
