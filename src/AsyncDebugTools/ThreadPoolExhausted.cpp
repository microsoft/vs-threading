#include "stdafx.h"
#include "dbgexts.h"
#include "Helpers.h"

static int s_iThresholdToDetectThreadPoolExhausted = 20;
static int s_iThresholdToBlameCallStack = 10;

struct Thread
{
    int m_id;
    std::vector<std::string> m_frames;
};

struct LessThanFrames
{
    bool operator() (const std::vector<std::string> *pLeft, const std::vector<std::string> *pRight) const
    {
        if (pLeft->size() != pRight->size())
        {
            return pLeft->size() < pRight->size();
        }

        for (size_t i = 0; i < pLeft->size(); ++i)
        {
            int result = pLeft->at(i).compare(pRight->at(i));
            if (result != 0)
            {
                return result < 0;
            }
        }

        return false;
    }
};

bool IsThreadPoolExhausted(
    PDEBUG_CLIENT pDebugClient,
    const std::string &strSOS)
{
    // !ThreadPool
    std::string strOutput;
    ThreadPoolStatus threadPoolStatus;
    if (SUCCEEDED(Execute(pDebugClient, GetFullCommand(strSOS, "ThreadPool"), strOutput))
        && GetThreadPoolStatus(strOutput, threadPoolStatus)
        && threadPoolStatus.m_iRunningWorkers >= s_iThresholdToDetectThreadPoolExhausted)
    {
        return true;
    }

    return false;
}

bool IsClrThreadPoolWorkingThread(const std::string &strOutput)
{
    std::stringstream ss(strOutput);
    std::string strLine;
    while (std::getline(ss, strLine))
    {
        if (strLine.find("clr!ThreadpoolMgr::ExecuteWorkRequest") != std::string::npos)
        {
            return true;
        }
    }

    return false;
}

HRESULT OnThreadPoolExhausted(
    PDEBUG_CLIENT pDebugClient,
    const std::string &strSOS)
{
    CComQIPtr<IDebugControl> srpControl(pDebugClient);
    HRESULT hr = S_OK;

    int iThreads = GetNumberOfThreads(pDebugClient);
    std::string strOutput;
    std::vector<Thread> threads;
    threads.reserve(iThreads);
    for (int i = 1; i < iThreads; ++i) // Skip the main thread
    {
        char szCommand[0x20] = { 0 };
        if (sprintf_s(szCommand, "~%dk", i) > 0
            && SUCCEEDED(Execute(pDebugClient, szCommand, strOutput))
            && IsClrThreadPoolWorkingThread(strOutput))
        {
            std::vector<std::string> frames;
            std::stringstream ss(strOutput);
            std::string strLine;
            std::string strFunc;
            while (std::getline(ss, strLine))
            {
                if (ParseKStackFrame(strLine, strFunc))
                {
                    frames.push_back(strFunc);
                }
            }

            if (!frames.empty())
            {
                threads.push_back({ i, std::move(frames) });
            }
        }
    }

    for (const Thread &thread : threads)
    {
        srpControl->Output(DEBUG_OUTPUT_VERBOSE, "%d\n", thread.m_id);
        for (const std::string &strFrame : thread.m_frames)
        {
            srpControl->Output(DEBUG_OUTPUT_VERBOSE, "\t%s\n", strFrame.c_str());
        }
    }

    // Group the threads by the frames
    std::map<const std::vector<std::string> *, int, LessThanFrames> groups;
    for (const Thread &thread : threads)
    {
        const std::vector<std::string> *pKey = &thread.m_frames;
        ++groups[pKey];
    }

    // Sort by count of the threads
    std::vector<std::pair<const std::vector<std::string> *, int>> items(groups.size());
    std::copy(groups.cbegin(), groups.cend(), items.begin());
    std::sort(items.begin(), items.end(), [](const auto &x, const auto &y) { return x.second > y.second; });

    for (const auto &item : items)
    {
        if (item.second >= s_iThresholdToBlameCallStack)
        {
            srpControl->Output(DEBUG_OUTPUT_NORMAL, "Detected thread pool exhausted due to this callstack: (%d threads)\n", item.second);
            for (const std::string &strFrame : *item.first)
            {
                srpControl->Output(DEBUG_OUTPUT_NORMAL, "\t%s\n", strFrame.c_str());
            }

            srpControl->Output(DEBUG_OUTPUT_NORMAL, "\n");
        }
    }

    return hr;
}

