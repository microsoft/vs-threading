#include "stdafx.h"
#include "dbgexts.h"
#include "Helpers.h"
#include "ThreadPoolExhausted.h"
#include "DML.h"
#include <memory>

HRESULT DumpNode(
	_In_ PDEBUG_CLIENT pDebugClient,
	_In_ const std::string &strSOS,
	_In_ const ObjectInfo &node);

HRESULT CALLBACK
dumpversions(PDEBUG_CLIENT pDebugClient, PCSTR args)
{
	CComQIPtr<IDebugControl> srpControl(pDebugClient);

	std::string strSOS;
	std::string strOutput;
	if (!EnsureLoadSOS(pDebugClient, strSOS, strOutput))
	{
		srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to load SOS.dll: %s\n", strOutput.c_str());
		return E_FAIL;
	}

	std::string strObject(args, strlen(args));
	ObjectInfo objectInfo{};

	HRESULT hr = Execute(pDebugClient, GetFullCommand(strSOS, "do " + strObject), strOutput);
	if (FAILED(hr))
	{
		srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to run !do: %s\n", strOutput.c_str());
		return hr;
	}

	if (!ParseDumpObjectOutput(strOutput, objectInfo))
	{
		srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to parse the output of !do: %s\n", strOutput.c_str());
		return E_FAIL;
	}

	ObjectInfo node{};
	if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, objectInfo, "_root", node))
	{
		srpControl->Output(DEBUG_OUTPUT_NORMAL, "Id\tValue\tRequire\tName\n");
		hr = DumpNode(pDebugClient, strSOS, node);
		if (FAILED(hr))
		{
			return hr;
		}
	}

	return S_OK;
}

HRESULT DumpNode(
	_In_ PDEBUG_CLIENT pDebugClient,
	_In_ const std::string &strSOS,
	_In_ const ObjectInfo &node)
{
	HRESULT hr = S_OK;
	FieldInfo heightInfo{};

	CComQIPtr<IDebugControl> srpControl(pDebugClient);

	if (!GetFieldInfo(node, "_height", heightInfo))
	{
		srpControl->Output(DEBUG_OUTPUT_ERROR, "No _height\n");
		return E_FAIL;
	}

	int height = atoi(heightInfo.m_strValue.c_str());
	if (height == 0)
	{
		return S_OK;
	}

	if (height > 1)
	{
		ObjectInfo left{};
		if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, node, "_left", left))
		{
			hr = DumpNode(pDebugClient, strSOS, left);
			if (FAILED(hr))
			{
				return hr;
			}
		}
	}

	ObjectInfo valueInfo{};
	ObjectInfo firstValueInfo{};
	ObjectInfo keyInfo{};
	FieldInfo idInfo{};
	ObjectInfo nameInfo{};
	if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, node, "_value", valueInfo) &&
		FindFieldAndGetObjectInfo(pDebugClient, strSOS, valueInfo, "_firstValue", firstValueInfo) &&
		FindFieldAndGetObjectInfo(pDebugClient, strSOS, firstValueInfo, "key", keyInfo) &&
		GetFieldInfo(keyInfo, "<Id>k__BackingField", idInfo) &&
		FindFieldAndGetObjectInfo(pDebugClient, strSOS, keyInfo, "<Name>k__BackingField", nameInfo) &&
		FindFieldAndGetObjectInfo(pDebugClient, strSOS, firstValueInfo, "value", valueInfo))
	{
		FieldInfo intValueInfo{};
		ObjectInfo versionInfo{};
		FieldInfo allowMissingValueInfo{};
		ObjectInfo innerVersionInfo{};

		if (GetFieldInfo(valueInfo, "m_value", intValueInfo))
		{
			hr = srpControl->Output(DEBUG_OUTPUT_NORMAL, "%s\t%s\t\t\t%s\n", idInfo.m_strValue.c_str(), intValueInfo.m_strValue.c_str(), nameInfo.m_string.c_str());
		}
		else if (GetFieldInfo(valueInfo, "<AllowMissingData>k__BackingField", allowMissingValueInfo) &&
			FindFieldAndGetObjectInfo(pDebugClient, strSOS, valueInfo, "<Version>k__BackingField", innerVersionInfo) &&
			GetFieldInfo(innerVersionInfo, "m_value", intValueInfo))
		{
			hr = srpControl->Output(DEBUG_OUTPUT_NORMAL, "%s\t%s\t%s\t\t%s\n", idInfo.m_strValue.c_str(), intValueInfo.m_strValue.c_str(), allowMissingValueInfo.m_strValue.c_str(), nameInfo.m_string.c_str());
		}
		else
		{
			return E_FAIL;
		}

		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (height > 1)
	{
		ObjectInfo right{};
		if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, node, "_right", right))
		{
			hr = DumpNode(pDebugClient, strSOS, right);
			if (FAILED(hr))
			{
				return hr;
			}
		}
	}

	return hr;
}
