﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

public class GenericParameterHelper
{
    public GenericParameterHelper()
    {
        this.Data = new Random().Next();
    }

    public GenericParameterHelper(int data)
    {
        this.Data = data;
    }

    public int Data { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is GenericParameterHelper other)
        {
            return this.Data == other.Data;
        }

        return false;
    }

    public override int GetHashCode()
    {
        return this.Data;
    }
}
