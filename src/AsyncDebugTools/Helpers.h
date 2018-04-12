#pragma once

#include "stdafx.h"

struct ExceptionInfo
{
    std::string m_strThreadId;
    std::string m_strExceptionObject;
};

struct ExceptionDetail
{
    std::string m_strExceptionObject;
    std::string m_strExceptionType;
    std::string m_strInnerExceptionObject;
    std::vector<std::string> m_stackTraces;
    std::string m_strHResult;
    std::string m_strRawText;
};

struct ThreadPoolStatus
{
    int m_iRunningWorkers = 0;
};

struct FieldInfo
{
    std::string m_strMethodTable;
    int m_iOffset;
    bool m_fIsValueType;
    std::string m_strValue;
    std::string m_strName;
};

struct ObjectInfo
{
    std::string m_strAddress;
    std::string m_strType;
    std::string m_string;
    std::vector<FieldInfo> m_fields;
};

bool EnsureLoadSOS(
    _In_ IDebugClient* pDebugClient,
    _Out_ std::string &strSOS,
    _Out_ std::string &strOutput);

std::string GetFullCommand(
    _In_ const std::string &strSOS,
    _In_ const std::string &strCommand);

HRESULT Execute(
    _In_ IDebugClient* pDebugClient,
    _In_ const std::string &strCommand,
    _Out_ std::string &strOutput);

void ExtractExceptionInfosFromThreadsOutput(
    _In_ const std::string &strOutput,
    _Out_ std::vector<ExceptionInfo> &exceptionInfos);

void CollectExceptionDetails(
    _In_ IDebugClient* pDebugClient,
    _In_ const std::string &strSOS,
    _In_ const std::string &strExceptionObject,
    _Out_ std::vector<ExceptionDetail> &exceptionDetailsIncludingInnerExceptions);

std::string GuessBlamedSymbol(
    _In_ const ExceptionDetail &detail);

bool IsDueToHangDetected(
    _In_ const ExceptionDetail &detail);

bool ParseClrStackFrame(
    _In_ const std::string &strFrame,
    _Out_ std::string &strFunc);

bool ParseKStackFrame(
    _In_ const std::string &strFrame,
    _Out_ std::string &strFunc);

bool IsImmunized(
    _In_ const std::string &strFrame);

std::string GetSymbolFromFrame(
    _In_ const std::string &strFrame);

bool GetThreadPoolStatus(
    _In_ const std::string &strThreadPoolOutput,
    _Out_ ThreadPoolStatus &status);

int GetNumberOfThreads(
    _In_ PDEBUG_CLIENT pDebugClient);

bool ParseDumpObjectOutput(
    _In_ const std::string &strDumpObject,
    _Out_ ObjectInfo &objectInfo);

bool ParseName2EEOutput(
    _In_ const std::string &strName2EE,
    _Out_ std::string &strMethodTable);

bool GetFieldInfo(
    _In_ const ObjectInfo &objectInfo,
    _In_ const std::string &strFieldName,
    _Out_ FieldInfo &fieldInfo);

bool FindFieldAndGetObjectInfo(
    _In_ PDEBUG_CLIENT pDebugClient,
    _In_ const std::string &strSOS,
    _In_ const ObjectInfo &objectInfo,
    _In_ const std::string &strFieldName,
    _Out_ ObjectInfo &fieldObjectInfo);
