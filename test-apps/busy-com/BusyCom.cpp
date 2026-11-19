// ATL in-proc COM server. Registered with a DllSurrogate so it runs inside dllhost.exe.
#include <atlbase.h>
#include <atlcom.h>
#include <math.h>
#include "BusyCom_h.h"
#include <windows.h>

#define NOINLINE __declspec(noinline)

NOINLINE static double Spin(int iterations)
{
    double s = 0;
    for (int i = 0; i < iterations; i++) s += sqrt((double)i) * sin((double)i);
    return s;
}

NOINLINE static double Level3() { return Spin(50000); }
NOINLINE static double Level2() { return Level3() + Spin(20000); }
NOINLINE static double Level1() { return Level2() + Spin(10000); }

class ATL_NO_VTABLE CBusy :
    public CComObjectRootEx<CComMultiThreadModel>,
    public CComCoClass<CBusy, &CLSID_Busy>,
    public IDispatchImpl<IBusy, &IID_IBusy, &LIBID_BusyComLib, 1, 0>
{
public:
    BEGIN_COM_MAP(CBusy)
        COM_INTERFACE_ENTRY(IBusy)
        COM_INTERFACE_ENTRY(IDispatch)
    END_COM_MAP()

    DECLARE_NOT_AGGREGATABLE(CBusy)
    DECLARE_NO_REGISTRY()

    NOINLINE double BurnMs(int ms)
    {
        double s = 0;
        ULONGLONG end = GetTickCount64() + ms;
        while (GetTickCount64() < end) s += Level1();
        return s;
    }

    STDMETHOD(Compute)(LONG milliseconds, DOUBLE* result)
    {
        if (!result) return E_POINTER;
        *result = BurnMs(milliseconds);
        return S_OK;
    }
};

OBJECT_ENTRY_AUTO(CLSID_Busy, CBusy)

class CBusyComModule : public ATL::CAtlDllModuleT<CBusyComModule>
{
public:
    DECLARE_LIBID(LIBID_BusyComLib)
};

CBusyComModule _AtlModule;

extern "C" BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID reserved)
{
    return _AtlModule.DllMain(reason, reserved);
}

STDAPI DllCanUnloadNow() { return _AtlModule.DllCanUnloadNow(); }
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) { return _AtlModule.DllGetClassObject(rclsid, riid, ppv); }
