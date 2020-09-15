// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Xunit;

// Some of our tests measure stress, GC pressure, etc.
// It messes with reliable test results when other threads are doing random stuff.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
