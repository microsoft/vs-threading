// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Diagnostics.Runtime.Interop;

namespace CpsDbg;

internal class DebuggerOutput
{
    private IDebugClient client;
    private IDebugControl control;

    internal DebuggerOutput(IDebugClient client)
    {
        this.client = client;
        this.control = (IDebugControl)client;
    }

    internal void WriteString(string message)
    {
        this.control.ControlledOutput(DEBUG_OUTCTL.ALL_CLIENTS, DEBUG_OUTPUT.NORMAL, message);
    }

    internal void WriteLine(string message)
    {
        this.WriteString(message + "\n");
    }

    internal void WriteObjectAddress(ulong address)
    {
        this.WriteDml($"<link cmd=\"!do {address:x}\">{address:x8}</link>");
    }

    internal void WriteThreadLink(uint threadId)
    {
        this.WriteDml($"<link cmd=\"~~[{threadId:x}]kp\">Thread TID:[{threadId:x}]</link>");
    }

    internal void WriteMethodInfo(string name, ulong address)
    {
        this.WriteDml($"<link cmd=\".open -a 0x{address:x}\" alt=\"Try to open source, you might need click more than once to get it work.\">{name}</link>");
    }

    internal void WriteStringWithLink(string message, string linkCommand)
    {
        this.WriteDml($"<link cmd=\"{linkCommand}\">{message}</link>");
    }

    private void WriteDml(string dml)
    {
        this.control.ControlledOutput(DEBUG_OUTCTL.AMBIENT_DML, DEBUG_OUTPUT.NORMAL, dml);
    }
}
