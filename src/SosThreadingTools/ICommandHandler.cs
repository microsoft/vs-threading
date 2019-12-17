// Copyright (c) Microsoft Corporation. All rights reserved.

namespace CpsDbg
{
    internal interface ICommandHandler
    {
        void Execute(DebuggerContext context, string args);
    }
}
