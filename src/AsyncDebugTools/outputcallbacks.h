#ifndef __OUTPUTCALLBACKS_H__
#define __OUTPUTCALLBACKS_H__

#include "dbgexts.h"

class COutputCallbacks : public IDebugOutputCallbacks
{
private:
    long m_ref;
    PCHAR m_pBufferNormal;
    size_t m_nBufferNormal;
    PCHAR m_pBufferError;
    size_t m_nBufferError;

public:
    COutputCallbacks()
    {
        m_ref = 1;
        m_pBufferNormal = NULL;
        m_nBufferNormal = 0;
        m_pBufferError = NULL;
        m_nBufferError = 0;
    }

    ~COutputCallbacks()
    {
        Clear();
    }

    // IUnknown
    STDMETHOD(QueryInterface)(__in REFIID InterfaceId, __out PVOID* Interface);
    STDMETHOD_(ULONG, AddRef)();
    STDMETHOD_(ULONG, Release)();

    // IDebugOutputCallbacks
    STDMETHOD(Output)(__in ULONG Mask, __in PCSTR Text);

    // Helpers
    ULONG SupportedMask() { return DEBUG_OUTPUT_NORMAL | DEBUG_OUTPUT_ERROR; }
    PCHAR BufferNormal() { return m_pBufferNormal; }
    PCHAR BufferError() { return m_pBufferError; }
    void Clear();
};

#endif // __OUTPUTCALLBACKS_H__
