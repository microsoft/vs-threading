#include "stdafx.h"
#include "Helpers.h"
#include "outputcallbacks.h"

bool TryFindSOS(
    _In_ IDebugClient* pDebugClient,
    _Out_ std::string &strSOS)
{
    strSOS.clear();

    std::string strOutput;
    if (SUCCEEDED(Execute(pDebugClient, ".extmatch /e *\\sos*.dll Threads", strOutput))
        && !strOutput.empty())
    {
        size_t pos = strOutput.find_first_of("\r\n");
        if (pos != std::string::npos)
        {
            strOutput.erase(pos);
        }

        size_t dot = strOutput.rfind('.');
        if (dot != std::string::npos)
        {
            strSOS = strOutput.substr(1, dot - 1);
        }
    }

    return !strSOS.empty();
}

bool EnsureLoadSOS(
    _In_ IDebugClient* pDebugClient,
    _Out_ std::string &strSOS,
    _Out_ std::string &strOutput)
{
    strSOS.clear();
    strOutput.clear();

    if (TryFindSOS(pDebugClient, strSOS))
    {
        return true;
    }

    if (SUCCEEDED(Execute(pDebugClient, ".loadby sos.dll clr", strOutput)))
    {
        return TryFindSOS(pDebugClient, strSOS);
    }

    return false;
}

std::string GetFullCommand(
    _In_ const std::string &strSOS,
    _In_ const std::string &strCommand)
{
    return "!" + strSOS + "." + strCommand;
}

HRESULT Execute(
    _In_ IDebugClient* pDebugClient,
    _In_ const std::string &strCommand,
    _Out_ std::string &strOutput)
{
    HRESULT hr = E_FAIL;
    strOutput.clear();

    CComPtr<IDebugClient> srpNewClient;
    if (SUCCEEDED(pDebugClient->CreateClient(&srpNewClient)))
    {
        COutputCallbacks *pOutputCallbacks = new COutputCallbacks();
        if (SUCCEEDED(srpNewClient->SetOutputMask(pOutputCallbacks->SupportedMask()))
            && SUCCEEDED(srpNewClient->SetOutputCallbacks(pOutputCallbacks)))
        {
            CComQIPtr<IDebugControl> srpNewControl(srpNewClient);
            if (srpNewControl)
            {
                hr = srpNewControl->Execute(DEBUG_OUTCTL_THIS_CLIENT | DEBUG_OUTCTL_NOT_LOGGED, strCommand.c_str(), DEBUG_EXECUTE_NOT_LOGGED | DEBUG_EXECUTE_NO_REPEAT);
                if (pOutputCallbacks->BufferError())
                {
                    strOutput = pOutputCallbacks->BufferError();
                    if (SUCCEEDED(hr))
                    {
                        hr = E_FAIL; // Ensure returning failure when there is error message.
                    }
                }
                else if (pOutputCallbacks->BufferNormal())
                {
                    strOutput = pOutputCallbacks->BufferNormal();
                }

                // Print the command outputs if enable verbose mode.
                {
                    CComQIPtr<IDebugControl> srpControl(pDebugClient);
                    srpControl->Output(DEBUG_OUTPUT_VERBOSE, "Execute command: %s\n%s\n", strCommand.c_str(), strOutput.c_str());
                }
            }

            srpNewClient->SetOutputCallbacks(nullptr);
        }

        pOutputCallbacks->Release();
    }

    return hr;
}

void SplitString(
    _In_ const std::string &strText,
    _In_ const std::string &strDelim,
    _Out_ std::vector<std::string> &tokens)
{
    tokens.clear();
    std::string strCopyText(strText);
    char *pszContext = nullptr;
    char *pszToken = strtok_s(const_cast<char *>(strCopyText.c_str()), strDelim.c_str(), &pszContext);
    while (pszToken != nullptr)
    {
        tokens.push_back(pszToken);
        pszToken = strtok_s(nullptr, strDelim.c_str(), &pszContext);
    }
}

static std::regex g_addressPattern("[0-9a-fA-F]{8}");

void ExtractExceptionInfosFromThreadsOutput(
    _In_ const std::string &strOutput,
    _Out_ std::vector<ExceptionInfo> &exceptionInfos)
{
    static std::string strDelim = " ()";

    exceptionInfos.clear();

    std::istringstream iss(strOutput);
    std::string strLine;
    std::vector<std::string> tokens;
    while (std::getline(iss, strLine))
    {
        SplitString(strLine, strDelim, tokens);
        for (auto i = tokens.size() - 1; i >= 10; --i)
        {
            if (std::regex_match(tokens[i], g_addressPattern))
            {
                exceptionInfos.push_back(ExceptionInfo{tokens[0], tokens[i]});
                break;
            }
        }
    }
}

bool TryParsePrintExceptionOutput(
    _In_ const std::string &strOutput,
    _Out_ ExceptionDetail &detail)
{
    detail = ExceptionDetail{};

    std::istringstream iss(strOutput);
    std::string strLine;

    // Exception object
    {
        if (!std::getline(iss, strLine))
        {
            return false;
        }

        std::smatch match;
        if (!std::regex_search(strLine, match, g_addressPattern))
        {
            return false;
        }

        detail.m_strExceptionObject = match.str();
    }

    // Exception type
    {
        if (!std::getline(iss, strLine))
        {
            return false;
        }

        size_t pos = strLine.find_last_of(" :");
        if (pos == std::string::npos)
        {
            return false;
        }

        detail.m_strExceptionType = strLine.substr(pos + 1);
    }

    // Skip Message
    if (!std::getline(iss, strLine))
    {
        return false;
    }

    // InnerException [optional]
    {
        if (!std::getline(iss, strLine))
        {
            return false;
        }

        std::smatch match;
        if (std::regex_search(strLine, match, g_addressPattern))
        {
            detail.m_strInnerExceptionObject = match.str();
        }
    }

    // StackTrace (generated)
    {
        if (!std::getline(iss, strLine))
        {
            return false;
        }

        //     SP       IP       Function
        if (!std::getline(iss, strLine))
        {
            return false;
        }

        size_t pos = strLine.rfind("Function");
        if (pos == std::string::npos)
        {
            return false;
        }

        while (std::getline(iss, strLine) && !strLine.empty())
        {
            detail.m_stackTraces.push_back(strLine.substr(pos));
        }

        if (detail.m_stackTraces.empty())
        {
            return false;
        }
    }

    // Skip StackTraceString
    if (!std::getline(iss, strLine))
    {
        return false;
    }

    // HResult
    {
        if (!std::getline(iss, strLine))
        {
            return false;
        }

        size_t pos = strLine.find_last_of(" :");
        if (pos == std::string::npos)
        {
            return false;
        }

        detail.m_strHResult = strLine.substr(pos + 1);
    }

    detail.m_strRawText = strOutput;

    return true;
}

void CollectExceptionDetails(
    _In_ IDebugClient* pDebugClient,
    _In_ const std::string &strSOS,
    _In_ const std::string &strExceptionObject,
    _Out_ std::vector<ExceptionDetail> &exceptionDetailsIncludingInnerExceptions)
{
    std::string strPrintExceptionCommand = GetFullCommand(strSOS, "PrintException");
    std::string strException = strExceptionObject;
    while (!strException.empty())
    {
        std::string strCommand = strPrintExceptionCommand + " " + strException;
        strException.clear();

        std::string strOutput;
        if (SUCCEEDED(Execute(pDebugClient, strCommand, strOutput)))
        {
            ExceptionDetail detail;
            if (TryParsePrintExceptionOutput(strOutput, detail))
            {
                exceptionDetailsIncludingInnerExceptions.push_back(detail);
                strException = detail.m_strInnerExceptionObject;
            }
        }
    }
}

static std::vector<std::string> g_immunizedSymbolsStartWithIgnoreCase =
{
    "mscorlib",
    "System",
    "Microsoft_VisualStudio_Validation",
    "Microsoft_VisualStudio_Threading",
    "Microsoft_Collections_Immutable",

    "Microsoft.VisualStudio.Validation",
    "Microsoft.VisualStudio.Threading",
    "Microsoft.Collections.Immutable",
    "Microsoft.VisualStudio.Shell.VsTaskLibraryHelper",
    "Microsoft.VisualStudio.Services.VsTask",
    "Microsoft.VisualStudio.ProjectSystem.ThreadHandlingMultithreaded",
    "Microsoft.VisualStudio.ProjectSystem.VS.HResult",
    "Microsoft.VisualStudio.Project.VisualC.VCProjectEngine.ApartmentMarshaler.Invoke",
};

static std::vector<std::string> g_immunizedSymbolsContain =
{
    "ErrorUtilities.",
    "ErrorHandler.",
    "ThrowOnFailure",
    "ThrowIfNotOnUIThread",
    "HrInvoke",
};

bool IsImmunized(_In_ const std::string &strFrame)
{
    for (const std::string &strImmunizedSymbolStartWithIgnoreCase : g_immunizedSymbolsStartWithIgnoreCase)
    {
        if (strFrame.length() > strImmunizedSymbolStartWithIgnoreCase.length()
            && 0 == _strnicmp(strFrame.c_str(), strImmunizedSymbolStartWithIgnoreCase.c_str(), strImmunizedSymbolStartWithIgnoreCase.length()))
        {
            return true;
        }
    }

    for (const std::string &strImmunizedSymbolsContain : g_immunizedSymbolsContain)
    {
        if (strFrame.find(strImmunizedSymbolsContain) != std::string::npos)
        {
            return true;
        }
    }

    return false;
}

std::string GetSymbolFromFrame(_In_ const std::string &strFrame)
{
    size_t pos = strFrame.find('(');
    std::string strSymbol = strFrame.substr(0, pos);

    // Normalize NGENed image name.
    pos = strSymbol.find("_ni!");
    if (pos != std::string::npos)
    {
        strSymbol.erase(pos, 3);
    }

    return strSymbol;
}

std::string GuessBlamedSymbol(
    _In_ const ExceptionDetail &detail)
{
    for (const std::string &strFrame : detail.m_stackTraces)
    {
        if (!IsImmunized(strFrame))
        {
            return GetSymbolFromFrame(strFrame);
        }
    }

    // Otherwise, fallback to the last frame.
    return GetSymbolFromFrame(detail.m_stackTraces.back());
}

bool IsDueToHangDetected(
    _In_ const ExceptionDetail &detail)
{
    for (const std::string &stack : detail.m_stackTraces)
    {
        if (stack.find("OnHangDetected") != std::string::npos)
        {
            return true;
        }
    }

    return false;
}

static std::regex g_framePattern("^[0-9a-f]{8} [0-9a-f]{8} ([a-z].+)", std::regex_constants::icase);

bool ParseClrStackFrame(
    _In_ const std::string &strFrame,
    _Out_ std::string &strFunc)
{
    std::smatch matches;
    if (std::regex_match(strFrame, matches, g_framePattern))
    {
        strFunc = matches[1].str();
        return true;
    }

    return false;
}

bool ParseKStackFrame(
    _In_ const std::string &strFrame,
    _Out_ std::string &strFunc)
{
    static std::regex s_funcPattern("[0-9a-fA-F-]{8} [0-9a-fA-F-]{8} (.+!.+\\+0x[0-9a-fA-F]+)");
    std::smatch match;
    if (std::regex_search(strFrame, match, s_funcPattern))
    {
        strFunc = match[1].str();
        return true;
    }

    return false;
}

/* !threadpool
CPU utilization: 3%
Worker Thread: Total: 129 Running: 110 Idle: 0 MaxLimit: 2047 MinLimit: 12
Work Request in Queue: 0
--------------------------------------
Number of Timers: 1
--------------------------------------
Completion Port Thread:Total: 4 Free: 4 MaxFree: 24 CurrentLimit: 4 MaxLimit: 1000 MinLimit: 12
*/
bool GetThreadPoolStatus(
    _In_ const std::string &strThreadPoolOutput,
    _Out_ ThreadPoolStatus &status)
{
    static std::regex s_workerThreadPattern("^Worker Thread: Total: (\\d+) Running: (\\d+)", std::regex::icase);

    std::stringstream ss(strThreadPoolOutput);
    std::string strLine;
    while (std::getline(ss, strLine))
    {
        std::smatch match;
        if (std::regex_search(strLine, match, s_workerThreadPattern))
        {
            status.m_iRunningWorkers = std::atoi(match[2].str().c_str());
            return true;
        }
    }

    return false;
}

int GetNumberOfThreads(
    _In_ PDEBUG_CLIENT pDebugClient)
{
    static std::regex s_threadIdPattern("^\\s*(\\d+)\\s*Id:", std::regex::icase);

    std::string strOutput;
    if (SUCCEEDED(Execute(pDebugClient, "~*", strOutput)))
    {
        std::stringstream ss(strOutput);
        std::string strLine;
        int threadId = -1;
        while (std::getline(ss, strLine))
        {
            std::smatch match;
            if (std::regex_search(strLine, match, s_threadIdPattern))
            {
                threadId = std::atoi(match[1].str().c_str());
            }
        }

        return threadId + 1;
    }

    return 0;
}

struct FieldHeaderInfo
{
    std::string m_strName;
    size_t m_iStart;
    size_t m_iLength;
};

bool ParseFieldHeaders(
    _In_ const std::string &strHeaders,
    _Out_ std::vector<FieldHeaderInfo> &headers)
{
    static std::regex s_fieldHeaderPattern("^\\s+(\\S+)");

    headers.clear();
    std::smatch match;
    std::string::const_iterator begin = strHeaders.begin();
    while (std::regex_search(begin, strHeaders.end(), match, s_fieldHeaderPattern))
    {
        FieldHeaderInfo header{};
        header.m_strName.assign(match[1].first, match[1].second);
        header.m_iStart = begin - strHeaders.begin();
        header.m_iLength = match.length();
        headers.push_back(std::move(header));
        begin += match.length();
    }

    if (!headers.empty())
    {
        headers.back().m_iLength = std::string::npos;
    }

    return headers.size() == 8;
}

void Trim(_Inout_ std::string &strText)
{
    size_t start = strText.find_first_not_of(' ');
    if (start == std::string::npos)
    {
        strText.clear();
        return;
    }

    size_t end = strText.find_last_not_of(' ');
    if (end - start + 1 < strText.length())
    {
        strText.erase(end + 1);
        strText.erase(0, start);
    }
}

void SetFieldValue(
    _Inout_ FieldInfo &field,
    _In_ const std::string &strName,
    _Inout_ std::string &&strValue)
{
    Trim(strValue);

    if (strName == "MT")
    {
        field.m_strMethodTable = strValue;
    }
    else if (strName == "Offset")
    {
        field.m_iOffset = atoi(strValue.c_str());
    }
    else if (strName == "VT")
    {
        field.m_fIsValueType = atoi(strValue.c_str()) != 0;
    }
    else if (strName == "Value")
    {
        field.m_strValue = strValue;
    }
    else if (strName == "Name")
    {
        field.m_strName = strValue;
    }
}

/* !do 032f2398
Name:        Microsoft.VisualStudio.Services.Settings.SettingsPackage+<ShellInitPlusFiveSeconds>d__1
MethodTable: 6f6a03fc
EEClass:     6f58da24
Size:        32(0x20) bytes
File:        C:\windows\Microsoft.Net\assembly\GAC_MSIL\Microsoft.VisualStudio.Shell.UI.Internal\v4.0_14.0.0.0__b03f5f7f11d50a3a\Microsoft.VisualStudio.Shell.UI.Internal.dll
Fields:
      MT    Field   Offset                 Type VT     Attr    Value Name
6cc19158  4001249        8         System.Int32  1 instance        1 <>1__state
6cc223fc  400124a        c ...TaskMethodBuilder  1 instance 032f23a4 <>t__builder
6cbe4fd0  400124b       18 ...olean, mscorlib]]  1 instance 032f23b0 <>u__1
6cc17670  400124c        4        System.Object  0 instance 0a4862f4 <>u__2
*/
bool ParseDumpObjectOutput(
    _In_ const std::string &strDumpObject,
    _Out_ ObjectInfo &objectInfo)
{
    objectInfo.m_strType.clear();
    objectInfo.m_fields.clear();

    std::stringstream ss(strDumpObject);
    std::string strLine;
    bool fFoundFields = false;
    while (std::getline(ss, strLine))
    {
        size_t sep = strLine.find(':');
        if (sep != std::string::npos)
        {
            if (strncmp(strLine.c_str(), "Name", sep) == 0)
            {
                size_t start = strLine.find_first_not_of(' ', sep + 1);
                if (start != std::string::npos)
                {
                    objectInfo.m_strType.assign(strLine, start);
                }
            }
            if (strncmp(strLine.c_str(), "String", sep) == 0)
            {
                size_t start = strLine.find_first_not_of(' ', sep + 1);
                if (start != std::string::npos)
                {
                    objectInfo.m_string.assign(strLine, start);
                }
            }
            else if (strncmp(strLine.c_str(), "Fields", sep) == 0)
            {
                fFoundFields = true;
                break;
            }
        }
    }

    if (fFoundFields && !objectInfo.m_strType.empty())
    {
        if (std::getline(ss, strLine))
        {
            std::vector<FieldHeaderInfo> headers;
            if (ParseFieldHeaders(strLine, headers))
            {
                while (std::getline(ss, strLine))
                {
                    if (strLine.empty() || isspace(strLine[0]) || (int)strLine.length() <= headers.back().m_iStart )
                    {
                        continue;
                    }

                    int start = 0;
                    int end = 0;
                    int fieldIndex = 0;
                    FieldInfo fieldInfo{};
                    for (const FieldHeaderInfo &headerInfo : headers)
                    {
                        // SOS doesn't align all fields with head correctly, causing the code reads wrong values and names.
                        if (fieldIndex < 6)
                        {
                            std::string strValue(strLine, headerInfo.m_iStart, headerInfo.m_iLength);
                            SetFieldValue(fieldInfo, headerInfo.m_strName, std::move(strValue));
                            start = headerInfo.m_iStart + headerInfo.m_iLength;
                        }
                        else
                        {
                            while (start < (int)strLine.length() && isspace(strLine[start]))
                            {
                                start++;
                            }

                            end = start;
                            while (end < (int)strLine.length() && !isspace(strLine[end]))
                            {
                                end++;
                            }

                            std::string strValue(strLine, start, end - start);
                            SetFieldValue(fieldInfo, headerInfo.m_strName, std::move(strValue));
                            start = end;
                        }

                        fieldIndex++;
                    }

                    objectInfo.m_fields.push_back(std::move(fieldInfo));
                }
            }
        }
    }

    return !objectInfo.m_strType.empty() && !objectInfo.m_fields.empty();
}

/* !name2ee mscorlib.dll!System.Runtime.CompilerServices.AsyncMethodBuilderCore+MoveNextRunner
Module:      6c7f1000
Assembly:    mscorlib.dll
Token:       020008be
MethodTable: 6cbe4dbc
EEClass:     6c855780
Name:        System.Runtime.CompilerServices.AsyncMethodBuilderCore+MoveNextRunner
*/
bool ParseName2EEOutput(
    _In_ const std::string &strName2EE,
    _Out_ std::string &strMethodTable)
{
    std::stringstream ss(strName2EE);
    std::string strLine;
    while (std::getline(ss, strLine))
    {
        size_t sep = strLine.find(':');
        if (sep != std::string::npos)
        {
            size_t start = strLine.find_first_not_of(' ', sep + 1);
            if (start != std::string::npos)
            {
                if (strncmp(strLine.c_str(), "MethodTable", sep) == 0)
                {
                    strMethodTable.assign(strLine, start);
                    return true;
                }
            }
        }
    }

    return false;
}

bool GetFieldInfo(
    _In_ const ObjectInfo &objectInfo,
    _In_ const std::string &strFieldName,
    _Out_ FieldInfo &fieldInfo)
{
    for (const FieldInfo &fi: objectInfo.m_fields)
    {
        if (fi.m_strName == strFieldName)
        {
            fieldInfo = fi;
            return true;
        }
    }

    return false;
}

bool FindFieldAndGetObjectInfo(
    _In_ PDEBUG_CLIENT pDebugClient,
    _In_ const std::string &strSOS,
    _In_ const ObjectInfo &objectInfo,
    _In_ const std::string &strFieldName,
    _Out_ ObjectInfo &fieldObjectInfo)
{
    FieldInfo fieldInfo{};
    if (!GetFieldInfo(objectInfo, strFieldName, fieldInfo) || fieldInfo.m_strValue == "00000000")
    {
        return false;
    }

    std::string strCommand;
    if (fieldInfo.m_fIsValueType)
    {
        if (fieldInfo.m_strMethodTable == "00000000")
        {
            return false;
        }

        strCommand.append("dumpvc ")
            .append(fieldInfo.m_strMethodTable)
            .append(" ")
            .append(fieldInfo.m_strValue);
    }
    else
    {
        strCommand.append("do ")
            .append(fieldInfo.m_strValue);
    }

    std::string strOutput;
    HRESULT hr = Execute(pDebugClient, GetFullCommand(strSOS, strCommand), strOutput);
    if (FAILED(hr))
    {
        CComQIPtr<IDebugControl> srpControl(pDebugClient);
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to run !%s: %s\n", strCommand.c_str(), strOutput.c_str());
        return false;
    }

    if (!ParseDumpObjectOutput(strOutput, fieldObjectInfo))
    {
        CComQIPtr<IDebugControl> srpControl(pDebugClient);
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to parse the output of !%s: %s\n", strCommand.c_str(), strOutput.c_str());
        return false;
    }

    fieldObjectInfo.m_strAddress = fieldInfo.m_strValue;
    return true;
}
