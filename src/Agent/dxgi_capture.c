#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <stdlib.h>
#include <string.h>

#include "dxgi_capture.h"
#include "eventlog.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dxguid.lib")

struct dxgi_capture
{
    ID3D11Device*           device;
    ID3D11DeviceContext*    context;
    IDXGIOutputDuplication* dupl;
    ID3D11Texture2D*        staging;
    int                     width;
    int                     height;
};

static void release_pipeline(dxgi_capture* c)
{
    if (c->dupl    != NULL) { c->dupl->lpVtbl->Release(c->dupl);          c->dupl = NULL; }
    if (c->staging != NULL) { c->staging->lpVtbl->Release(c->staging);    c->staging = NULL; }
    if (c->context != NULL) { c->context->lpVtbl->Release(c->context);    c->context = NULL; }
    if (c->device  != NULL) { c->device->lpVtbl->Release(c->device);      c->device = NULL; }
}

static void release_all(dxgi_capture* c)
{
    release_pipeline(c);
}

// Создаёт device/output duplication и staging-текстуру для чтения кадра с CPU.
static int init_pipeline(dxgi_capture* c)
{
    D3D_FEATURE_LEVEL levels[] =
    {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0,
    };

    HRESULT hr = D3D11CreateDevice(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, 0,
                                   levels, (UINT)_countof(levels), D3D11_SDK_VERSION,
                                   &c->device, NULL, &c->context);
    if (FAILED(hr))
    {
        // На VM без аппаратного ускорения пробуем программный растеризатор.
        hr = D3D11CreateDevice(NULL, D3D_DRIVER_TYPE_WARP, NULL, 0,
                               levels, (UINT)_countof(levels), D3D11_SDK_VERSION,
                               &c->device, NULL, &c->context);
        if (FAILED(hr))
            return -1;
    }

    IDXGIDevice* dxgiDev = NULL;
    if (FAILED(c->device->lpVtbl->QueryInterface(c->device, &IID_IDXGIDevice, (void**)&dxgiDev)))
        return -1;

    IDXGIAdapter* adapter = NULL;
    hr = dxgiDev->lpVtbl->GetAdapter(dxgiDev, &adapter);
    dxgiDev->lpVtbl->Release(dxgiDev);
    if (FAILED(hr))
        return -1;

    IDXGIOutput* output = NULL;
    hr = adapter->lpVtbl->EnumOutputs(adapter, 0, &output);
    adapter->lpVtbl->Release(adapter);
    if (FAILED(hr))
        return -1;

    IDXGIOutput1* output1 = NULL;
    hr = output->lpVtbl->QueryInterface(output, &IID_IDXGIOutput1, (void**)&output1);
    output->lpVtbl->Release(output);
    if (FAILED(hr))
        return -1;

    hr = output1->lpVtbl->DuplicateOutput(output1, (IUnknown*)c->device, &c->dupl);
    output1->lpVtbl->Release(output1);
    if (FAILED(hr))
        return -1;

    D3D11_TEXTURE2D_DESC td;
    ZeroMemory(&td, sizeof(td));
    td.Width = (UINT)c->width;
    td.Height = (UINT)c->height;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_STAGING;
    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

    if (FAILED(c->device->lpVtbl->CreateTexture2D(c->device, &td, NULL, &c->staging)))
        return -1;

    return 0;
}

dxgi_capture* dxgi_capture_create(int width, int height)
{
    if (width <= 0 || height <= 0)
        return NULL;

    dxgi_capture* c = (dxgi_capture*)calloc(1, sizeof(dxgi_capture));
    if (c == NULL)
        return NULL;

    c->width = width;
    c->height = height;

    if (init_pipeline(c) != 0)
    {
        rc_event_log(EVENTLOG_WARNING_TYPE, 1350,
            L"DXGI Desktop Duplication недоступен — захват через GDI BitBlt");
        release_all(c);
        free(c);
        return NULL;
    }

    rc_event_log(EVENTLOG_INFORMATION_TYPE, 1351,
        L"DXGI Desktop Duplication активен (%dx%d)", width, height);
    return c;
}

int dxgi_capture_grab(dxgi_capture* c, void* dstBgra, int dstStride, int timeoutMs)
{
    if (c == NULL || c->dupl == NULL || dstBgra == NULL)
        return -1;

    DXGI_OUTDUPL_FRAME_INFO info;
    IDXGIResource* res = NULL;

    HRESULT hr = c->dupl->lpVtbl->AcquireNextFrame(c->dupl, (UINT)timeoutMs, &info, &res);
    if (hr == DXGI_ERROR_WAIT_TIMEOUT)
        return 0;
    if (FAILED(hr))
        return -1;

    ID3D11Texture2D* tex = NULL;
    hr = res->lpVtbl->QueryInterface(res, &IID_ID3D11Texture2D, (void**)&tex);
    if (SUCCEEDED(hr))
    {
        c->context->lpVtbl->CopyResource(c->context,
            (ID3D11Resource*)c->staging, (ID3D11Resource*)tex);
        tex->lpVtbl->Release(tex);
    }
    res->lpVtbl->Release(res);

    // Кадр нужно освободить в любом случае, иначе дупликатор перестанет отдавать новые.
    c->dupl->lpVtbl->ReleaseFrame(c->dupl);

    if (FAILED(hr))
        return -1;

    D3D11_MAPPED_SUBRESOURCE mapped;
    ZeroMemory(&mapped, sizeof(mapped));
    hr = c->context->lpVtbl->Map(c->context, (ID3D11Resource*)c->staging,
                                 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
        return -1;

    BYTE* src = (BYTE*)mapped.pData;
    BYTE* dst = (BYTE*)dstBgra;
    size_t rowBytes = (size_t)c->width * 4;
    for (int y = 0; y < c->height; y++)
    {
        memcpy(dst, src, rowBytes);
        src += mapped.RowPitch;
        dst += dstStride;
    }

    c->context->lpVtbl->Unmap(c->context, (ID3D11Resource*)c->staging, 0);
    return 1;
}

void dxgi_capture_destroy(dxgi_capture* c)
{
    if (c == NULL)
        return;
    release_all(c);
    free(c);
}
