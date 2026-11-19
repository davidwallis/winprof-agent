// Calls the surrogate-hosted COM object in a loop so dllhost.exe stays busy.
#include <windows.h>
#include <objbase.h>
#include <stdio.h>
#include "BusyCom_h.h"

int main()
{
    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) return 1;

    IBusy* busy = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_Busy, nullptr, CLSCTX_LOCAL_SERVER, IID_IBusy, (void**)&busy);
    if (FAILED(hr))
    {
        printf("CoCreateInstance failed: 0x%08lx\n", hr);
        return 2;
    }

    ULONGLONG end = GetTickCount64() + 300 * 1000;
    double total = 0;
    while (GetTickCount64() < end)
    {
        double r = 0;
        if (FAILED(busy->Compute(500, &r))) break;
        total += r;
    }
    busy->Release();
    CoUninitialize();
    printf("%f\n", total);
    return 0;
}
