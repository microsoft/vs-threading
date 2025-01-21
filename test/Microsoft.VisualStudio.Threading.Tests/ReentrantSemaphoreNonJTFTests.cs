﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

public class ReentrantSemaphoreNonJTFTests : ReentrantSemaphoreTestBase
{
    public ReentrantSemaphoreNonJTFTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void NoDeadlockOnSyncBlockingOnSemaphore_NoContention(ReentrantSemaphore.ReentrancyMode mode)
    {
        this.semaphore = this.CreateSemaphore(mode);
        this.ExecuteOnDispatcher(delegate
        {
            this.semaphore.ExecuteAsync(() => Task.CompletedTask).Wait(this.TimeoutToken);
            return Task.CompletedTask;
        });
    }
}
