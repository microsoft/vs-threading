#include "stdafx.h"
#include "dbgexts.h"
#include "outputcallbacks.h"

#define MAX_OUTPUTCALLBACKS_BUFFER 0x1000000  // 1Mb
#define MAX_OUTPUTCALLBACKS_LENGTH 0x0FFFFFF  // 1Mb - 1

STDMETHODIMP COutputCallbacks::QueryInterface(__in REFIID InterfaceId, __out PVOID* Interface)
{
    *Interface = NULL;
    if (IsEqualIID(InterfaceId, __uuidof(IUnknown)) || IsEqualIID(InterfaceId, __uuidof(IDebugOutputCallbacks)))
    {
        *Interface = (IDebugOutputCallbacks *)this;
        InterlockedIncrement(&m_ref);
        return S_OK;
    }
    else
    {
        return E_NOINTERFACE;
    }
}

STDMETHODIMP_(ULONG) COutputCallbacks::AddRef()
{
    return InterlockedIncrement(&m_ref);
}

STDMETHODIMP_(ULONG) COutputCallbacks::Release()
{
    if (InterlockedDecrement(&m_ref) == 0)
    {
        delete this;
        return 0;
    }
    return m_ref;
}

STDMETHODIMP COutputCallbacks::Output(__in ULONG Mask, __in PCSTR Text)
{
    if ((Mask & DEBUG_OUTPUT_NORMAL) == DEBUG_OUTPUT_NORMAL)
    {
        if (m_pBufferNormal == NULL)
        {
            m_nBufferNormal = 0;
            m_pBufferNormal = (PCHAR)malloc(sizeof(CHAR)*(MAX_OUTPUTCALLBACKS_BUFFER));
            if (m_pBufferNormal == NULL) return E_OUTOFMEMORY;
            m_pBufferNormal[0] = '\0';
            m_pBufferNormal[MAX_OUTPUTCALLBACKS_LENGTH] = '\0';
        }
        size_t len = strlen(Text);
        if (len > (MAX_OUTPUTCALLBACKS_LENGTH-m_nBufferNormal))
        {
            len = MAX_OUTPUTCALLBACKS_LENGTH-m_nBufferNormal;
        }
        if (len > 0)
        {
            memcpy(&m_pBufferNormal[m_nBufferNormal], Text, len);
            m_nBufferNormal += len;
            m_pBufferNormal[m_nBufferNormal] = '\0';
        }
    }
    if ((Mask & DEBUG_OUTPUT_ERROR) == DEBUG_OUTPUT_ERROR)
    {
        if (m_pBufferError == NULL)
        {
            m_nBufferError = 0;
            m_pBufferError = (PCHAR)malloc(sizeof(CHAR)*(MAX_OUTPUTCALLBACKS_BUFFER));
            if (m_pBufferError == NULL) return E_OUTOFMEMORY;
            m_pBufferError[0] = '\0';
            m_pBufferError[MAX_OUTPUTCALLBACKS_LENGTH] = '\0';
        }
        size_t len = strlen(Text);
        if (len >= (MAX_OUTPUTCALLBACKS_LENGTH-m_nBufferError))
        {
            len = MAX_OUTPUTCALLBACKS_LENGTH-m_nBufferError;
        }
        if (len > 0)
        {
            memcpy(&m_pBufferError[m_nBufferError], Text, len);
            m_nBufferError += len;
            m_pBufferError[m_nBufferError] = '\0';
        }
    }
    return S_OK;
}

void COutputCallbacks::Clear()
{
    if (m_pBufferNormal)
    {
        free(m_pBufferNormal);
        m_pBufferNormal = NULL;
        m_nBufferNormal = 0;
    }
    if (m_pBufferError)
    {
        free(m_pBufferError);
        m_pBufferError = NULL;
        m_nBufferError = 0;
    }
}
