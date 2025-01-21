// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Text;
using Microsoft.SqlServer.Server;
using Xunit;

namespace IsolatedTestHost;

internal class TestOutputHelper : ITestOutputHelper
{
    private readonly StringBuilder builder = new();

    public string Output => this.builder.ToString();

    public void Write(string message)
    {
        Console.Write(message);
        this.builder.Append(message);
    }

    public void Write(string format, params object[] args)
    {
        Console.Write(format, args);
        this.builder.AppendFormat(format, args);
    }

    public void WriteLine(string format, params object[] args)
    {
        Console.WriteLine(format, args);
        this.builder.AppendFormat(format, args);
        this.builder.AppendLine();
    }

    public void WriteLine(string message)
    {
        Console.WriteLine(message);
        this.builder.AppendLine(message);
    }
}
