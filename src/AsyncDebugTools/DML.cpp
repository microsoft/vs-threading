#include "stdafx.h"

BOOL PreferDML(PDEBUG_CLIENT pDebugClient)
{
    BOOL bPreferDML = FALSE;
    IDebugControl* pDebugControl;
    if (SUCCEEDED(pDebugClient->QueryInterface(__uuidof(IDebugControl),
        (void **)& pDebugControl)))
    {
        ULONG ulOptions = 0;
        if (SUCCEEDED(pDebugControl->GetEngineOptions(&ulOptions)))
        {
            bPreferDML = (ulOptions & DEBUG_ENGOPT_PREFER_DML);
        }
        pDebugControl->Release();
    }
    return bPreferDML;
}

BOOL AbilityDML(PDEBUG_CLIENT pDebugClient)
{
    BOOL bAbilityDML = FALSE;
    IDebugAdvanced2* pDebugAdvanced2;
    if (SUCCEEDED(pDebugClient->QueryInterface(__uuidof(IDebugAdvanced2),
        (void **)& pDebugAdvanced2)))
    {
        HRESULT hr = 0;
        if (SUCCEEDED(hr = pDebugAdvanced2->Request(
            DEBUG_REQUEST_CURRENT_OUTPUT_CALLBACKS_ARE_DML_AWARE,
            NULL, 0, NULL, 0, NULL)))
        {
            if (hr == S_OK) bAbilityDML = TRUE;
        }
        pDebugAdvanced2->Release();
    }
    return bAbilityDML;
}
