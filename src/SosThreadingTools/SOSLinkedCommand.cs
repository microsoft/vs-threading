// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using DbgEngExtension;
using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;

namespace CpsDbg;

internal class SOSLinkedCommand : DbgEngCommand
{
    protected SOSLinkedCommand(nint pUnknown, bool redirectConsoleOutput)
        : base(pUnknown, redirectConsoleOutput)
    {
    }

    protected void WriteString(string message)
    {
        this.DebugControl.ControlledOutput(DEBUG_OUTCTL.ALL_CLIENTS, DEBUG_OUTPUT.NORMAL, message);
    }

    protected void WriteLine(string message)
    {
        this.WriteString(message + "\n");
    }

    protected void WriteObjectAddress(ulong address)
    {
        this.WriteDml($"<link cmd=\"!do {address:x}\">{address:x8}</link>");
    }

    protected void WriteThreadLink(uint threadId)
    {
        this.WriteDml($"<link cmd=\"~~[{threadId:x}]kp\">Thread TID:[{threadId:x}]</link>");
    }

    protected void WriteMethodInfo(string name, ulong address)
    {
        this.WriteDml($"<link cmd=\".open -a 0x{address:x}\" alt=\"Try to open source, you might need click more than once to get it work.\">{name}</link>");
    }

    protected void WriteStringWithLink(string message, string linkCommand)
    {
        this.WriteDml($"<link cmd=\"{linkCommand}\">{message}</link>");
    }

    protected void WriteDml(string dml)
    {
        this.DebugControl.ControlledOutput(DEBUG_OUTCTL.AMBIENT_DML, DEBUG_OUTPUT.NORMAL, dml);
    }
}
