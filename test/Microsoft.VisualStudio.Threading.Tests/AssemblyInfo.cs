﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Some of our tests measure stress, GC pressure, etc.
// It messes with reliable test results when other threads are doing random stuff.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
