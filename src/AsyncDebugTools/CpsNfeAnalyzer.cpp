#include "stdafx.h"
#include "dbgexts.h"
#include "Helpers.h"
#include "ThreadPoolExhausted.h"

bool DetectHangFailureID(
    PDEBUG_CLIENT pDebugClient,
    const std::string &strSOS,
    std::string &strFailureID);

HRESULT CALLBACK
cpsnfeanalyze(PDEBUG_CLIENT pDebugClient, PCSTR args)
{
    UNREFERENCED_PARAMETER(args);

    CComQIPtr<IDebugControl> srpControl(pDebugClient);

    std::string strSOS;
    std::string strOutput;
    if (!EnsureLoadSOS(pDebugClient, strSOS, strOutput))
    {
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to load SOS.dll: %s\n", strOutput.c_str());
        return E_FAIL;
    }

    if (IsThreadPoolExhausted(pDebugClient, strSOS))
    {
        return OnThreadPoolExhausted(pDebugClient, strSOS);
    }

    // !Threads
    HRESULT hr = Execute(pDebugClient, GetFullCommand(strSOS, "Threads"), strOutput);
    if (FAILED(hr))
    {
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to run !Threads: %s\n", strOutput.c_str());
        return hr;
    }

    std::vector<ExceptionInfo> exceptionInfos;
    ExtractExceptionInfosFromThreadsOutput(strOutput, exceptionInfos);
    if (exceptionInfos.empty())
    {
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Not found any exception objects.\n");
        return E_FAIL;
    }

    ExceptionInfo blamedException;
    std::string strBlamedThreadClrStack;
    for (const ExceptionInfo &ex : exceptionInfos)
    {
        // It is not common but possible to find more than one exceptions.
        // To blame the correct exception, we will dump the CLR stacks on each thread and find 'WatsonErrorReport.Submit'.
        if (SUCCEEDED(Execute(pDebugClient, "~" + ex.m_strThreadId + "e " + GetFullCommand(strSOS, "ClrStack"), strOutput)))
        {
            if (strOutput.find("WatsonErrorReport.Submit") != std::string::npos)
            {
                blamedException = ex;
                strBlamedThreadClrStack = strOutput;
                break;
            }
        }
    }

    if (blamedException.m_strThreadId.empty())
    {
        // Fallback to the first exception which is usually correct.
        blamedException = exceptionInfos.front();
        Execute(pDebugClient, "~" + blamedException.m_strThreadId + "e " + GetFullCommand(strSOS, "ClrStack"), strBlamedThreadClrStack);
    }

    std::string strBlamedThreadMixedStack;
    Execute(pDebugClient, "~" + blamedException.m_strThreadId + "k", strBlamedThreadMixedStack);

    std::vector<ExceptionDetail> exceptionDetails;
    CollectExceptionDetails(pDebugClient, strSOS, blamedException.m_strExceptionObject, exceptionDetails);
    if (exceptionDetails.empty())
    {
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to parse the details of exception: %s\n", blamedException.m_strExceptionObject.c_str());
        return E_FAIL;
    }

    std::string strFailureID;

    // Special logic: if the exception was thrown by OnHangDetected, then it is a hang, so that switch to a different code path to generate the failure id.
    if (exceptionDetails.size() == 1 && IsDueToHangDetected(exceptionDetails.front()))
    {
        if (!DetectHangFailureID(pDebugClient, strSOS, strFailureID))
        {
            srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to detect the frame that is blocking the main thread.");
            return E_FAIL;
        }

        // Always blame main thread.
        Execute(pDebugClient, "~0e " + GetFullCommand(strSOS, "ClrStack"), strBlamedThreadClrStack);
        Execute(pDebugClient, "~0k", strBlamedThreadMixedStack);
    }
    else
    {
        const ExceptionDetail &finalInnerException = exceptionDetails.back();
        std::string strBlamedSymbol = GuessBlamedSymbol(finalInnerException);
        strFailureID = finalInnerException.m_strExceptionType + "_" + finalInnerException.m_strHResult + "_" + strBlamedSymbol;
    }

    // Print Failure ID
    srpControl->Output(DEBUG_OUTPUT_NORMAL, "FAILURE_BUCKET_ID:  %s\n\n", strFailureID.c_str());

    // Print the exceptions from the inner to outer
    for (auto riter = exceptionDetails.crbegin(); riter != exceptionDetails.crend(); ++riter)
    {
        // Print long string similar to the style in Perl.
        //
        //  <<Exception:16466b00
        //  ......
        //  Exception:16466b00
        std::string strAnchor = "Exception:" + riter->m_strExceptionObject;
        srpControl->Output(DEBUG_OUTPUT_NORMAL, "<<%s\n%s%s\n\n", strAnchor.c_str(), riter->m_strRawText.c_str(), strAnchor.c_str());
    }

    // Print CLR stack
    {
        std::string strAnchor = "ClrStack";
        srpControl->Output(DEBUG_OUTPUT_NORMAL, "<<%s\n%s%s\n\n", strAnchor.c_str(), strBlamedThreadClrStack.c_str(), strAnchor.c_str());
    }

    // Print Mixed stack
    {
        std::string strAnchor = "MixedStack";
        srpControl->Output(DEBUG_OUTPUT_NORMAL, "<<%s\n%s%s\n\n", strAnchor.c_str(), strBlamedThreadMixedStack.c_str(), strAnchor.c_str());
    }

    return S_OK;
}

bool DetectHangFailureID(
    PDEBUG_CLIENT pDebugClient,
    const std::string &strSOS,
    std::string &strFailureID)
{
    strFailureID.clear();

    std::string strMainThreadClrStack;
    Execute(pDebugClient, "~0e " + GetFullCommand(strSOS, "ClrStack"), strMainThreadClrStack);

    std::string strLine;
    std::stringstream ss(strMainThreadClrStack);
    bool foundOnHangDetected = false;
    while (std::getline(ss, strLine))
    {
        if (!foundOnHangDetected)
        {
            if (strLine.find("OnHangDetected") != std::string::npos)
            {
                foundOnHangDetected = true;
            }

            continue;
        }

        std::string strFunc;
        if (ParseClrStackFrame(strLine, strFunc) && !IsImmunized(strFunc))
        {
            strFailureID = GetSymbolFromFrame(strFunc);
            break;
        }
    }

    return !strFailureID.empty();
}
