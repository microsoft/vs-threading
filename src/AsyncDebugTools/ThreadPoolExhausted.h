#pragma once

#include "stdafx.h"

bool IsThreadPoolExhausted(
    PDEBUG_CLIENT pDebugClient,
    const std::string &strSOS);

HRESULT OnThreadPoolExhausted(
    PDEBUG_CLIENT pDebugClient,
    const std::string &strSOS);
