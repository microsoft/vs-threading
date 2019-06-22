// dbgexts.cpp

#include "stdafx.h"
#include "dbgexts.h"

extern "C" HRESULT CALLBACK
DebugExtensionInitialize(PULONG Version, PULONG Flags)
{
    *Version = DEBUG_EXTENSION_VERSION(EXT_MAJOR_VER, EXT_MINOR_VER);
    *Flags = 0;  // Reserved for future use.
    return S_OK;
}

extern "C" void CALLBACK
DebugExtensionNotify(ULONG Notify, ULONG64 Argument)
{
    UNREFERENCED_PARAMETER(Argument);
    switch (Notify)
    {
        // A debugging session is active. The session may not necessarily be suspended.
    case DEBUG_NOTIFY_SESSION_ACTIVE:
        break;
        // No debugging session is active.
    case DEBUG_NOTIFY_SESSION_INACTIVE:
        break;
        // The debugging session has suspended and is now accessible.
    case DEBUG_NOTIFY_SESSION_ACCESSIBLE:
        break;
        // The debugging session has started running and is now inaccessible.
    case DEBUG_NOTIFY_SESSION_INACCESSIBLE:
        break;
    }
    return;
}

extern "C" void CALLBACK
DebugExtensionUninitialize(void)
{
    return;
}
