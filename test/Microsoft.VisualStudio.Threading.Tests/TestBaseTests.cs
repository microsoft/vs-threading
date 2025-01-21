﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Xunit.Sdk;

public class TestBaseTests : TestBase
{
    public TestBaseTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Fact]
    public void ExecuteOnSTA_ExecutesDelegateOnSTA()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");
        bool executed = false;
        this.ExecuteOnSTA(delegate
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.Null(SynchronizationContext.Current);
            executed = true;
        });
        Assert.True(executed);
    }

    [Fact]
    public void ExecuteOnSTA_PropagatesExceptions()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");
        Assert.Throws<ApplicationException>(() => this.ExecuteOnSTA(() =>
        {
            throw new ApplicationException();
        }));
    }

    [StaFact]
    public void ExecuteOnDispatcher_ExecutesDelegateOnSTA()
    {
        bool executed = false;
        this.ExecuteOnDispatcher(delegate
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            }

            Assert.NotNull(SynchronizationContext.Current);
            executed = true;
        });
        Assert.True(executed);
    }

    [Fact]
    public void ExecuteOnDispatcher_ExecutesDelegateOnMTA()
    {
        // Wrap the whole thing in Task.Run to force it to an MTA thread,
        // since xunit uses STA when tests run in batches.
        Task.Run(delegate
        {
            bool executed = false;
            this.ExecuteOnDispatcher(delegate
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
                }

                Assert.NotNull(SynchronizationContext.Current);
                executed = true;
            });
            Assert.True(executed);
        }).WaitWithoutInlining(throwOriginalException: true);
    }

    [Fact]
    public void ExecuteOnDispatcher_PropagatesExceptions()
    {
        Assert.Throws<ApplicationException>(() => this.ExecuteOnDispatcher(() =>
        {
            throw new ApplicationException();
        }));
    }

    [Fact]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task ExecuteInIsolation_PassingTest()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            this.Logger.WriteLine("Some output from isolated process.");
        }
    }

    [Fact]
    public async Task ExecuteInIsolation_FailingTest()
    {
        bool executeHere;
        try
        {
            executeHere = await this.ExecuteInIsolationAsync();
        }
        catch (XunitException ex)
        {
            // This is the outer invocation and it failed as expected.
            this.Logger.WriteLine(ex.ToString());
            return;
        }

        Assumes.True(executeHere);
        throw new Exception("Intentional test failure");
    }

#if NETFRAMEWORK
    [StaFact]
    [Trait("TestCategory", "FailsInCloudTest")]
    public async Task ExecuteInIsolation_PassingOnSTA()
    {
        if (await this.ExecuteInIsolationAsync())
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        }
    }

    [StaFact]
    public async Task ExecuteInIsolation_FailingOnSTA()
    {
        bool executeHere;
        try
        {
            executeHere = await this.ExecuteInIsolationAsync();
        }
        catch (XunitException ex)
        {
            // This is the outer invocation and it failed as expected.
            this.Logger.WriteLine(ex.ToString());
            return;
        }

        Assumes.True(executeHere);
        throw new Exception("Intentional test failure");
    }
#endif
}
