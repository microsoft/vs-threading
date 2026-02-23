// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text;
using Xunit;

namespace IsolatedTestHost;

internal class TestOutputHelper : ITestOutputHelper
{
    private readonly StringBuilder builder = new();

    public string Output => this.builder.ToString();

    public void Write(string message) => this.builder.Append(message);

    public void Write(string format, params object[] args) => this.builder.AppendFormat(format, args);

    public void WriteLine(string message) => this.builder.AppendLine(message);

    public void WriteLine(string format, params object[] args) => this.builder.AppendLine(string.Format(CultureInfo.CurrentCulture, format, args));
}
