// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Net;
using System.Xml.Linq;
using DbgEngExtension;
using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;

namespace CpsDbg;

internal class SOSLinkedCommand : DbgEngCommand
{
    private readonly bool isRunningAsExtension;

    protected SOSLinkedCommand(nint pUnknown, bool isRunningAsExtension)
        : base(pUnknown, redirectConsoleOutput: isRunningAsExtension)
    {
        this.isRunningAsExtension = isRunningAsExtension;
    }

    protected SOSLinkedCommand(IDisposable dbgEng, bool isRunningAsExtension)
        : base(dbgEng, redirectConsoleOutput: isRunningAsExtension)
    {
        this.isRunningAsExtension = isRunningAsExtension;
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
        if (this.isRunningAsExtension)
        {
            this.WriteDml($"<link cmd=\"!do {address:x}\">{address:x8}</link>");
        }
        else
        {
            Console.WriteLine(address.ToString("x"));
        }
    }

    protected void WriteThreadLink(uint threadId)
    {
        if (this.isRunningAsExtension)
        {
            this.WriteDml($"<link cmd=\"~~[{threadId:x}]kp\">Thread TID:[{threadId:x}]</link>");
        }
        else
        {
            Console.WriteLine($"Thread TID:[{threadId:x}]");
        }
    }

    protected void WriteMethodInfo(string name, ulong address)
    {
        if (this.isRunningAsExtension)
        {
            this.WriteDml($"<link cmd=\".open -a 0x{address:x}\" alt=\"Try to open source, you might need click more than once to get it work.\">{name}</link>");
        }
        else
        {
            Console.WriteLine(name);
        }
    }

    protected void WriteStringWithLink(string message, string linkCommand)
    {
        if (this.isRunningAsExtension)
        {
            this.WriteDml($"<link cmd=\"{linkCommand}\">{message}</link>");
        }
        else
        {
            Console.WriteLine(message);
        }
    }

    protected void WriteDml(string dml)
    {
        this.DebugControl.ControlledOutput(DEBUG_OUTCTL.AMBIENT_DML, DEBUG_OUTPUT.NORMAL, dml);
    }
}
