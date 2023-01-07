﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace CpsDbg;

internal interface ICommandHandler
{
    void Execute(string args);
}
