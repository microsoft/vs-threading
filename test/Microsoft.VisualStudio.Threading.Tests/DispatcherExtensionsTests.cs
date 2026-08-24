// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETFRAMEWORK || WINDOWS

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft;
using Microsoft.VisualStudio.Threading;

public class DispatcherExtensionsTests : JoinableTaskTestBase
{
    public DispatcherExtensionsTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void WithPriority_ThrowsOnInvalidInputs()
    {
        Assert.Throws<ArgumentNullException>(() => this.asyncPump.WithPriority(null!, DispatcherPriority.Normal));
    }

    [Fact]
    public void WithPriority_IdleHappensAfterNormalPriority()
    {
        this.SimulateUIThread(async delegate
        {
            JoinableTaskFactory? idlePriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.ApplicationIdle);
            JoinableTaskFactory? normalPriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal);
            JoinableTask? idleTask = idlePriorityJtf.RunAsync(async delegate
            {
                await Task.Yield();
            });
            JoinableTask? normalTask = normalPriorityJtf.RunAsync(async delegate
            {
                await Task.Yield();
                Assert.False(idleTask.IsCompleted);
            });
            await Task.WhenAll(idleTask.Task, normalTask.Task).WithCancellation(this.TimeoutToken);
        });
    }

    [Fact]
    public void WithPriority_LowPriorityCanBlockOnHighPriorityWork()
    {
        this.SimulateUIThread(async delegate
        {
            JoinableTaskFactory? idlePriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.ApplicationIdle);
            JoinableTaskFactory? normalPriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal);
            JoinableTask? normalTask = null;
            var unblockNormalPriorityWork = new AsyncManualResetEvent();
            JoinableTask? idleTask = idlePriorityJtf.RunAsync(async delegate
            {
                await Task.Yield();
                unblockNormalPriorityWork.Set();
                normalTask!.Join();
            });
            normalTask = normalPriorityJtf.RunAsync(async delegate
            {
                await unblockNormalPriorityWork;
                Assert.False(idleTask.IsCompleted);
            });
            await Task.WhenAll(idleTask.Task, normalTask.Task).WithCancellation(this.TimeoutToken);
        });
    }

    [Fact]
    public void WithPriority_PreservesDispatcherCultureSemantics()
    {
        this.SimulateUIThread(async delegate
        {
            var dispatcherCulture = CultureInfo.GetCultureInfo("en-US");
            var postingCulture = CultureInfo.GetCultureInfo("fr-FR");
            var callbackCulture = CultureInfo.GetCultureInfo("de-DE");
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            JoinableTaskFactory factory = this.asyncPump.WithPriority(dispatcher, DispatcherPriority.Normal);
            var dispatcherCultureEstablished = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CultureInfo? originalDispatcherCulture = null;
            CultureInfo? originalDispatcherUICulture = null;
            _ = dispatcher.BeginInvoke(new Action(delegate
            {
                originalDispatcherCulture = CultureInfo.CurrentCulture;
                originalDispatcherUICulture = CultureInfo.CurrentUICulture;
                CultureInfo.CurrentCulture = dispatcherCulture;
                CultureInfo.CurrentUICulture = dispatcherCulture;
                dispatcherCultureEstablished.SetResult(null);
            }));
            await dispatcherCultureEstablished.Task.WithCancellation(this.TimeoutToken);

            try
            {
                CultureInfo? observedCulture = null;
                CultureInfo? observedUICulture = null;
                CultureInfo? cultureAfterCallback = null;
                var callbackCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var observedAfterCallback = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

                await Task.Run(delegate
                {
                    CultureInfo.CurrentCulture = postingCulture;
                    CultureInfo.CurrentUICulture = postingCulture;
                    JoinableTaskFactory.MainThreadAwaiter awaiter = factory.SwitchToMainThreadAsync().GetAwaiter();
                    awaiter.OnCompleted(delegate
                    {
                        try
                        {
                            awaiter.GetResult();
                            observedCulture = CultureInfo.CurrentCulture;
                            observedUICulture = CultureInfo.CurrentUICulture;
                            CultureInfo.CurrentCulture = callbackCulture;
                            CultureInfo.CurrentUICulture = callbackCulture;
                            _ = dispatcher.BeginInvoke(new Action(delegate
                            {
                                cultureAfterCallback = CultureInfo.CurrentCulture;
                                observedAfterCallback.SetResult(null);
                            }));
                            callbackCompleted.SetResult(null);
                        }
                        catch (Exception ex)
                        {
                            callbackCompleted.SetException(ex);
                        }
                    });
                });

                await callbackCompleted.Task.WithCancellation(this.TimeoutToken);
                await observedAfterCallback.Task.WithCancellation(this.TimeoutToken);
                Assert.Equal(dispatcherCulture, observedCulture);
                Assert.Equal(dispatcherCulture, observedUICulture);
                Assert.Equal(callbackCulture, cultureAfterCallback);
            }
            finally
            {
                var dispatcherCultureRestored = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = dispatcher.BeginInvoke(new Action(delegate
                {
                    CultureInfo.CurrentCulture = originalDispatcherCulture!;
                    CultureInfo.CurrentUICulture = originalDispatcherUICulture!;
                    dispatcherCultureRestored.SetResult(null);
                }));
                await dispatcherCultureRestored.Task.WithCancellation(this.TimeoutToken);
            }
        });
    }

#if NETFRAMEWORK
    [StaFact]
    public void WithPriority_MatchesDisableProcessingWithinDelegate()
    {
        this.SimulateUIThread(delegate
        {
            JoinableTaskFactory? normalPriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal);
            normalPriorityJtf.Run(delegate
            {
                this.AssertProcessingAllowed();

                using (Dispatcher.CurrentDispatcher.DisableProcessing())
                {
                    this.AssertProcessingDisabled();
                }

                return Task.CompletedTask;
            });

            return Task.CompletedTask;
        });
    }

    [StaFact]
    public void WithPriority_MatchesDisableProcessingOutsideDelegate()
    {
        this.SimulateUIThread(delegate
        {
            JoinableTaskFactory? normalPriorityJtf = this.asyncPump.WithPriority(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal);
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                normalPriorityJtf.Run(delegate
                {
                    this.AssertProcessingDisabled();

                    return Task.CompletedTask;
                });
            }

            return Task.CompletedTask;
        });
    }
#endif
}


#endif
