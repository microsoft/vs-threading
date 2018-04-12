#include "stdafx.h"
#include "dbgexts.h"
#include "Helpers.h"
#include "ThreadPoolExhausted.h"
#include "DML.h"
#include <memory>

struct StateMachineNode
{
    ObjectInfo m_objectInfo;
    StateMachineNode *m_pNext;
    StateMachineNode *m_pPrevious;
    int m_state;
    int m_iDepth;
    std::string m_task;
};

bool GetContinuation(
    _In_ PDEBUG_CLIENT pDebugClient,
    _In_ const std::string &strSOS,
    _In_ const ObjectInfo &stateMachine,
    _Out_ std::string &strContinuation);

HRESULT CALLBACK
dumpasync(PDEBUG_CLIENT pDebugClient, PCSTR args)
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

	/*
    if (IsThreadPoolExhausted(pDebugClient, strSOS))
    {
        return OnThreadPoolExhausted(pDebugClient, strSOS);
    }
	*/

    // TODO: Support .NET Core by allowing AsyncMethodBuilderCore to be defined in System.Private.CoreLib.ni.dll
    //       and perhaps other modules. 
    HRESULT hr = Execute(pDebugClient, GetFullCommand(strSOS, "name2ee mscorlib.dll!System.Runtime.CompilerServices.AsyncMethodBuilderCore+MoveNextRunner"), strOutput);
    if (FAILED(hr))
    {
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to run !name2ee: %s\n", strOutput.c_str());
        return hr;
    }

    std::string strMethodTable;
    if (!ParseName2EEOutput(strOutput, strMethodTable))
    {
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to parse the output of !name2ee: %s\n", strOutput.c_str());
        return hr;
    }

    hr = Execute(pDebugClient, GetFullCommand(strSOS, "dumpheap -short -mt " + strMethodTable), strOutput);
    if (FAILED(hr))
    {
        srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to run !dumpheap: %s\n", strOutput.c_str());
        return hr;
    }

    // Extract the inner m_stateMachine.
    std::stringstream ss(strOutput);
    std::string strObject;
    ObjectInfo objectInfo{};
    FieldInfo fieldInfo{};
    std::vector<std::unique_ptr<StateMachineNode>> stateMachineNodes;
    while (std::getline(ss, strObject))
    {
        // TODO: avoid calling !do 00000000 which will always error out.
        hr = Execute(pDebugClient, GetFullCommand(strSOS, "do " + strObject), strOutput);
        if (FAILED(hr))
        {
            srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to run !do: %s\n", strOutput.c_str());
            continue;
        }

        if (!ParseDumpObjectOutput(strOutput, objectInfo))
        {
            srpControl->Output(DEBUG_OUTPUT_ERROR, "Failed to parse the output of !do: %s\n", strOutput.c_str());
            continue;
        }

        if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, objectInfo, "m_stateMachine", objectInfo))
        {
            if (GetFieldInfo(objectInfo, "<>1__state", fieldInfo)
                && atoi(fieldInfo.m_strValue.c_str()) >= -1)
            {
                auto found = std::find_if(stateMachineNodes.begin(), stateMachineNodes.end(), [&](const std::unique_ptr<StateMachineNode> &node)
                {
                    return node->m_objectInfo.m_strAddress == objectInfo.m_strAddress;
                });

                // state machine can be reported more than once...
                if (found != stateMachineNodes.end())
                {
                    continue;
                }

                stateMachineNodes.push_back(std::make_unique<StateMachineNode>());
                stateMachineNodes.back()->m_objectInfo = std::move(objectInfo);
                stateMachineNodes.back()->m_state = atoi(fieldInfo.m_strValue.c_str());

                ObjectInfo asyncBuilder{};
                FieldInfo taskFieldInfo{};
                
                if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, stateMachineNodes.back()->m_objectInfo, "<>t__builder", asyncBuilder))
                {
                    ObjectInfo taskObject{};
                    while (!GetFieldInfo(asyncBuilder, "m_task", taskFieldInfo)
                         && FindFieldAndGetObjectInfo(pDebugClient, strSOS, asyncBuilder, "m_builder", asyncBuilder));

                    if (!taskFieldInfo.m_strValue.empty())
                    {
                        stateMachineNodes.back()->m_task = taskFieldInfo.m_strValue;
                    }
                }
            }
        }
    }

    // Link the state machines via the continuation.
    std::string strContinuation;
    for (size_t i = 0; i < stateMachineNodes.size(); ++i)
    {
        if (GetContinuation(pDebugClient, strSOS, stateMachineNodes[i]->m_objectInfo, strContinuation))
        {
            auto found = std::find_if(stateMachineNodes.begin(), stateMachineNodes.end(), [&](const std::unique_ptr<StateMachineNode> &node)
            {
                return node->m_objectInfo.m_strAddress == strContinuation;
            });

            if (found != stateMachineNodes.end())
            {
                StateMachineNode *pContinuation = (*found).get();
                stateMachineNodes[i]->m_pNext = pContinuation;
                pContinuation->m_pPrevious = (stateMachineNodes[i]).get();
            }
        }
    }

    // Fix Joinable Task chains
    for (std::unique_ptr<StateMachineNode> &node : stateMachineNodes)
    {
        if (node->m_pPrevious == nullptr)
        {
            ObjectInfo joinableTask{};
            FieldInfo wrappedTask{};

            if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, node->m_objectInfo, "<>4__this", joinableTask)
                && GetFieldInfo(joinableTask, "wrappedTask", wrappedTask))
            {
                std::string task = wrappedTask.m_strValue;
                auto found = std::find_if(stateMachineNodes.begin(), stateMachineNodes.end(), [&](const std::unique_ptr<StateMachineNode> &node)
                {
                    return node->m_task == task;
                });

                if (found != stateMachineNodes.end())
                {
                    StateMachineNode *pPrevious = (*found).get();
                    node->m_pPrevious = pPrevious;
                    pPrevious->m_pNext = node.get();
                }
            }
        }
    }

    // Compute the depth
    for (std::unique_ptr<StateMachineNode> &node : stateMachineNodes)
    {
        node->m_iDepth = 0;
        if (node->m_pPrevious == nullptr)
        {
            for (const StateMachineNode *p = node.get(); p != nullptr; p = p->m_pNext)
            {
                node->m_iDepth++;
            }
        }
    }

    // Sort by depth DESC
    std::sort(stateMachineNodes.begin(), stateMachineNodes.end(), [](const std::unique_ptr<StateMachineNode> &x, const std::unique_ptr<StateMachineNode> &y)
    {
        return x->m_iDepth > y->m_iDepth;
    });

    bool fOutputDML = PreferDML(pDebugClient) && AbilityDML(pDebugClient);

    // Dump the state machines
    for (const std::unique_ptr<StateMachineNode> &node : stateMachineNodes)
    {
        if (node->m_iDepth > 0)
        {
            std::string strIndent;
            for (const StateMachineNode *p = node.get(); p != nullptr; p = p->m_pNext)
            {
                if (fOutputDML)
                {
                    srpControl->Output(DEBUG_OUTPUT_NORMAL, strIndent.c_str());
                    srpControl->ControlledOutput(DEBUG_OUTCTL_AMBIENT_DML, DEBUG_OUTPUT_NORMAL, "<link cmd=\"!do %s\">%s</link>",
                        p->m_objectInfo.m_strAddress.c_str(),
                        p->m_objectInfo.m_strAddress.c_str());
                    srpControl->Output(DEBUG_OUTPUT_NORMAL, " <%d> %s\n", p->m_state, p->m_objectInfo.m_strType.c_str());
                }
                else
                {
                    srpControl->Output(DEBUG_OUTPUT_NORMAL, "%s%s <%d> %s\n", strIndent.c_str(), p->m_objectInfo.m_strAddress.c_str(), p->m_state, p->m_objectInfo.m_strType.c_str());
                }

                strIndent.append(".");
            }

            if (node->m_iDepth > 1)
            {
                srpControl->Output(DEBUG_OUTPUT_NORMAL, "\n");
            }
        }
    }

    return S_OK;
}

bool GetContinuationFromTarget(
    _In_ PDEBUG_CLIENT pDebugClient,
    _In_ const std::string &strSOS,
    _In_ const ObjectInfo &targetObject,
    _Out_ std::string &strContinuation)
{
    FieldInfo stateMachineField{};
    if (GetFieldInfo(targetObject, "m_stateMachine", stateMachineField))
    {
        strContinuation = stateMachineField.m_strValue;
        return true;
    }

    ObjectInfo nextContinuationObject{};
    if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, targetObject, "m_continuation", nextContinuationObject))
    {
        ObjectInfo targetObject{};
        if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, nextContinuationObject, "_target", targetObject)
            && GetContinuationFromTarget(pDebugClient, strSOS, targetObject, strContinuation))
        {
            return true;
        }
    }

    return false;
}

bool GetContinuation(
    _In_ PDEBUG_CLIENT pDebugClient,
    _In_ const std::string &strSOS,
    _In_ const ObjectInfo &stateMachine,
    _Out_ std::string &strContinuation)
{
    ObjectInfo builderObject{};
    if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, stateMachine, "<>t__builder", builderObject))
    {
        ObjectInfo taskObject{};
        while (!FindFieldAndGetObjectInfo(pDebugClient, strSOS, builderObject, "m_task", taskObject)
            && FindFieldAndGetObjectInfo(pDebugClient, strSOS, builderObject, "m_builder", builderObject));

        if (!taskObject.m_strType.empty())
        {
            ObjectInfo continuationObject{};
            ObjectInfo actionObject{};
            ObjectInfo targetObject{};
            while (FindFieldAndGetObjectInfo(pDebugClient, strSOS, taskObject, "m_continuationObject", continuationObject))
            {
                if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, continuationObject, "m_action", actionObject)
                    && FindFieldAndGetObjectInfo(pDebugClient, strSOS, actionObject, "_target", targetObject))
                {
                    if (GetContinuationFromTarget(pDebugClient, strSOS, targetObject, strContinuation))
                    {
                        return true;
                    }
                }

                if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, continuationObject, "_target", targetObject))
                {
                    if (GetContinuationFromTarget(pDebugClient, strSOS, targetObject, strContinuation))
                    {
                        return true;
                    }
                }

                ObjectInfo continuationTaskObject{};
                ObjectInfo stateObject{};
                if (FindFieldAndGetObjectInfo(pDebugClient, strSOS, continuationObject, "m_task", continuationTaskObject)
                    && FindFieldAndGetObjectInfo(pDebugClient, strSOS, continuationTaskObject, "m_stateObject", stateObject)
                    && FindFieldAndGetObjectInfo(pDebugClient, strSOS, stateObject, "_target", targetObject))
                {
                    if (GetContinuationFromTarget(pDebugClient, strSOS, targetObject, strContinuation))
                    {
                        return true;
                    }
                }

                taskObject = continuationObject;
            }
        }
    }

    return false;
}
