﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft;
using Microsoft.VisualStudio.Threading;

public abstract class JoinableTaskTestBase : TestBase
{
    protected const string DgmlNamespace = "http://schemas.microsoft.com/vs/2009/dgml";

    protected JoinableTaskContext context;
    protected JoinableTaskFactory asyncPump;
    protected JoinableTaskCollection? joinableCollection;

    protected int originalThreadManagedId;
    protected SynchronizationContext dispatcherContext;
    protected SingleThreadedTestSynchronizationContext.IFrame testFrame;

    protected JoinableTaskTestBase(ITestOutputHelper logger)
        : base(logger)
    {
        this.dispatcherContext = SingleThreadedTestSynchronizationContext.New();
        SynchronizationContext.SetSynchronizationContext(this.dispatcherContext);
#pragma warning disable CA2214 // Do not call overridable methods in constructors
        this.context = this.CreateJoinableTaskContext();
#pragma warning restore CA2214 // Do not call overridable methods in constructors
        this.joinableCollection = this.context.CreateCollection();
        this.asyncPump = this.context.CreateFactory(this.joinableCollection);
        this.originalThreadManagedId = Environment.CurrentManagedThreadId;
        this.testFrame = SingleThreadedTestSynchronizationContext.NewFrame();

        // Suppress the assert dialog that appears and causes test runs to hang.
        if (Trace.Listeners.OfType<DefaultTraceListener>().FirstOrDefault() is { } listener)
        {
            listener.AssertUiEnabled = false;
        }
    }

    protected virtual JoinableTaskContext CreateJoinableTaskContext()
    {
        return new JoinableTaskContext();
    }

    protected int GetPendingTasksCount()
    {
        IHangReportContributor hangContributor = this.context;
        HangReportContribution? contribution = hangContributor.GetHangReport();
        var dgml = XDocument.Parse(contribution.Content);
        return dgml.Descendants(XName.Get("Node", DgmlNamespace)).Count(n => n.Attributes("Category").Any(c => c.Value == "Task"));
    }

    protected void SimulateUIThread(Func<Task> testMethod)
    {
        Verify.Operation(this.originalThreadManagedId == Environment.CurrentManagedThreadId, "We can only simulate the UI thread if you're already on it (the starting thread for the test).");

        Exception? failure = null;
        this.dispatcherContext.Post(
            async delegate
            {
                try
                {
                    await testMethod();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    this.testFrame.Continue = false;
                }
            },
            null);

        SingleThreadedTestSynchronizationContext.PushFrame(this.dispatcherContext, this.testFrame);
        if (failure is object)
        {
            // Rethrow original exception without rewriting the callstack.
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    protected void PushFrame()
    {
        SingleThreadedTestSynchronizationContext.PushFrame(this.dispatcherContext, this.testFrame);
    }

    protected void PushFrameTillQueueIsEmpty()
    {
        this.dispatcherContext.Post(s => this.testFrame.Continue = false, null);
        this.PushFrame();
    }
}
