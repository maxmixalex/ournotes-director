// OurNotes Director -- GPU-only OpenGL -> D3D11 -> WinUI presentation.
// WGL_NV_DX_interop2 ownership rules: registry.khronos.org/OpenGL/extensions/NV/WGL_NV_DX_interop2.txt
// The WinUI interface ABI below is ISwapChainPanelNative from microsoft.ui.xaml.media.dxinterop.h.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <cwchar>
#include <wrl/client.h>
#include <GL/gl.h>
#include <memory>
#include <cstring>
#include <new>
#include <cstdint>
using Microsoft::WRL::ComPtr;

MIDL_INTERFACE("63aad0b8-7c24-40ff-85a8-640d944cc325")
IWinUISwapChainPanelNative : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain* swapChain) = 0;
};

namespace {
constexpr GLenum ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9, Framebuffer = 0x8D40;
constexpr GLenum Renderbuffer = 0x8D41, ColorAttachment0 = 0x8CE0, FramebufferComplete = 0x8CD5;
using GenObjects = void(APIENTRY*)(GLsizei, GLuint*);
using DeleteObjects = void(APIENTRY*)(GLsizei, const GLuint*);
using BindFramebuffer = void(APIENTRY*)(GLenum, GLuint);
using AttachRenderbuffer = void(APIENTRY*)(GLenum, GLenum, GLenum, GLuint);
using CheckFramebuffer = GLenum(APIENTRY*)(GLenum);
using BlitFramebuffer = void(APIENTRY*)(GLint, GLint, GLint, GLint, GLint, GLint, GLint, GLint, GLbitfield, GLenum);
using OpenDevice = HANDLE(WINAPI*)(void*);
using CloseDevice = BOOL(WINAPI*)(HANDLE);
using RegisterObject = HANDLE(WINAPI*)(HANDLE, void*, GLuint, GLenum, GLenum);
using UnregisterObject = BOOL(WINAPI*)(HANDLE, HANDLE);
using LockObjects = BOOL(WINAPI*)(HANDLE, GLint, HANDLE*);
using ExtensionString = const char*(WINAPI*)(HDC);

struct Functions {
    GenObjects genBuffers{}, genFrames{};
    DeleteObjects deleteBuffers{}, deleteFrames{};
    BindFramebuffer bind{}; AttachRenderbuffer attach{}; CheckFramebuffer check{}; BlitFramebuffer blit{};
    OpenDevice open{}; CloseDevice close{}; RegisterObject registerObject{}; UnregisterObject unregisterObject{};
    LockObjects lock{}, unlock{};
};
template<class T> bool Load(T& function, const char* name) {
    auto p = wglGetProcAddress(name);
    if (p == nullptr || p == reinterpret_cast<PROC>(1) || p == reinterpret_cast<PROC>(2) ||
        p == reinterpret_cast<PROC>(3) || p == reinterpret_cast<PROC>(-1)) return false;
    static_assert(sizeof(function) == sizeof(p), "WGL function pointer ABI");
    std::memcpy(&function, &p, sizeof(p)); return true;
}
bool HasExtension(const char* all, const char* wanted) {
    if (!all) return false;
    const size_t length = std::strlen(wanted);
    for (const char* match = std::strstr(all, wanted); match; match = std::strstr(match + length, wanted))
        if ((match == all || match[-1] == ' ') && (match[length] == '\0' || match[length] == ' ')) return true;
    return false;
}
HRESULT LoadFunctions(Functions& f) {
    if (!wglGetCurrentContext()) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
    ExtensionString extensions{};
    if (!Load(extensions, "wglGetExtensionsStringARB") || !HasExtension(extensions(wglGetCurrentDC()), "WGL_NV_DX_interop2"))
        return DXGI_ERROR_UNSUPPORTED;
    return Load(f.genBuffers, "glGenRenderbuffers") && Load(f.genFrames, "glGenFramebuffers") &&
        Load(f.deleteBuffers, "glDeleteRenderbuffers") && Load(f.deleteFrames, "glDeleteFramebuffers") &&
        Load(f.bind, "glBindFramebuffer") && Load(f.attach, "glFramebufferRenderbuffer") &&
        Load(f.check, "glCheckFramebufferStatus") && Load(f.blit, "glBlitFramebuffer") &&
        Load(f.open, "wglDXOpenDeviceNV") && Load(f.close, "wglDXCloseDeviceNV") &&
        Load(f.registerObject, "wglDXRegisterObjectNV") && Load(f.unregisterObject, "wglDXUnregisterObjectNV") &&
        Load(f.lock, "wglDXLockObjectsNV") && Load(f.unlock, "wglDXUnlockObjectsNV") ? S_OK : DXGI_ERROR_UNSUPPORTED;
}
struct Surface {
    DWORD owner = GetCurrentThreadId(); HGLRC glContext = wglGetCurrentContext();
    Functions f;
    HANDLE interopDevice{}, object{}; GLuint renderbuffer{}, framebuffer{};
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain1> swap;
    ComPtr<ID3D11Texture2D> shared, back;
    UINT width{}, height{};
    bool testOnly = false, locked = false;
    ~Surface() {
        // Managed owner always destroys on the render thread, before deleting its WGL context.
        if (locked && object && f.unlock) f.unlock(interopDevice, 1, &object);
        if (object && f.unregisterObject) f.unregisterObject(interopDevice, object);
        if (framebuffer && f.deleteFrames) f.deleteFrames(1, &framebuffer);
        if (renderbuffer && f.deleteBuffers) f.deleteBuffers(1, &renderbuffer);
        if (interopDevice && f.close) f.close(interopDevice);
        if (context) { context->ClearState(); context->Flush(); }
    }
    bool IsOwner() const { return GetCurrentThreadId() == owner && (testOnly || wglGetCurrentContext() == glContext); }
};
HRESULT CreateSwap(Surface& s) {
    ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIFactory2> factory;
    HRESULT hr = s.device.As(&dxgi); if (FAILED(hr)) return hr;
    hr = dxgi->GetAdapter(&adapter); if (FAILED(hr)) return hr;
    hr = adapter->GetParent(IID_PPV_ARGS(&factory)); if (FAILED(hr)) return hr;
    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width = s.width; description.Height = s.height;
    description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count = 1; description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 2; description.Scaling = DXGI_SCALING_STRETCH;
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    // WinUI does not alpha-composite SwapChainPanel. Checkerboard is composed on the GPU.
    description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    hr = factory->CreateSwapChainForComposition(s.device.Get(), &description, nullptr, &s.swap); if (FAILED(hr)) return hr;
    hr = s.swap->GetBuffer(0, IID_PPV_ARGS(&s.back)); if (FAILED(hr)) return hr;
    ComPtr<IDXGIDevice1> latency; if (SUCCEEDED(s.device.As(&latency))) latency->SetMaximumFrameLatency(1);
    D3D11_TEXTURE2D_DESC texture{};
    texture.Width = s.width; texture.Height = s.height; texture.MipLevels = 1; texture.ArraySize = 1;
    texture.Format = description.Format; texture.SampleDesc.Count = 1; texture.Usage = D3D11_USAGE_DEFAULT;
    texture.BindFlags = D3D11_BIND_RENDER_TARGET;
    return s.device->CreateTexture2D(&texture, nullptr, &s.shared);
}
HRESULT BindShared(Surface& s) {
    s.f.genBuffers(1, &s.renderbuffer);
    s.object = s.f.registerObject(s.interopDevice, s.shared.Get(), s.renderbuffer, Renderbuffer, 1 /* WGL_ACCESS_READ_WRITE_NV */);
    if (!s.object) { const DWORD error = GetLastError(); return HRESULT_FROM_WIN32(error ? error : ERROR_NOT_SUPPORTED); }
    if (!s.f.lock(s.interopDevice, 1, &s.object)) return E_FAIL;
    s.locked = true;
    GLint read{}, draw{}; glGetIntegerv(0x8CAA, &read); glGetIntegerv(0x8CA6, &draw);
    s.f.genFrames(1, &s.framebuffer); s.f.bind(Framebuffer, s.framebuffer);
    s.f.attach(Framebuffer, ColorAttachment0, Renderbuffer, s.renderbuffer);
    bool complete = s.f.check(Framebuffer) == FramebufferComplete;
    s.f.bind(ReadFramebuffer, static_cast<GLuint>(read)); s.f.bind(DrawFramebuffer, static_cast<GLuint>(draw));
    bool unlocked = s.f.unlock(s.interopDevice, 1, &s.object) != FALSE;
    if (unlocked) s.locked = false;
    return complete && unlocked ? S_OK : E_FAIL;
}
HRESULT CreateSurface(UINT width, UINT height, UINT preference, Surface** result) noexcept {
    if (!result) return E_POINTER; *result = nullptr;
    if (width < 16 || height < 16 || width > 16384 || height > 16384 || preference > 2) return E_INVALIDARG;
    try {
        Functions functions{};
        HRESULT hr = LoadFunctions(functions); if (FAILED(hr)) return hr;
        ComPtr<IDXGIFactory1> factory; hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory)); if (FAILED(hr)) return hr;
        ComPtr<IDXGIFactory6> preferredFactory; factory.As(&preferredFactory);
        HRESULT lastFailure = DXGI_ERROR_UNSUPPORTED;
        for (UINT index = 0; ; ++index) {
            ComPtr<IDXGIAdapter1> adapter;
            // D3D device creation does not select or replace the current WGL
            // driver. Prefer the process-start policy among compatible devices.
            hr = preferredFactory
                ? preferredFactory->EnumAdapterByGpuPreference(index, static_cast<DXGI_GPU_PREFERENCE>(preference), IID_PPV_ARGS(&adapter))
                : factory->EnumAdapters1(index, &adapter);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(hr)) return hr;
            DXGI_ADAPTER_DESC1 description{}; hr = adapter->GetDesc1(&description); if (FAILED(hr)) return hr;
            if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            // A candidate is usable only after the shared texture is registered,
            // locked, attached to a complete FBO and unlocked. Some hybrid
            // drivers open a D3D device but reject its texture later. Release
            // every candidate fully before trying the next adapter.
            auto s = std::make_unique<Surface>(); s->width = width; s->height = height; s->f = functions;
            hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr, 0, D3D11_SDK_VERSION, &s->device, nullptr, &s->context);
            if (FAILED(hr)) { lastFailure = hr; continue; }
            s->interopDevice = s->f.open(s->device.Get());
            if (!s->interopDevice) { lastFailure = DXGI_ERROR_UNSUPPORTED; continue; }
            hr = CreateSwap(*s); if (FAILED(hr)) { lastFailure = hr; continue; }
            hr = BindShared(*s); if (FAILED(hr)) { lastFailure = hr; continue; }
            *result = s.release(); return S_OK;
        }
        return lastFailure;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}
}
#define API extern "C" __declspec(dllexport)
API unsigned __cdecl OurNotesGpuAbi() noexcept { return 3; }
API HRESULT __cdecl OurNotesGpuCreate(UINT width, UINT height, Surface** result) noexcept {
    return CreateSurface(width, height, 0, result);
}
API HRESULT __cdecl OurNotesGpuCreatePreferred(UINT width, UINT height, UINT preference, Surface** result) noexcept {
    return CreateSurface(width, height, preference, result);
}
API HRESULT __cdecl OurNotesGpuPresent(Surface* s, GLuint sourceFramebuffer, UINT sourceWidth, UINT sourceHeight) noexcept {
    if (!s || !s->IsOwner() || !s->object) return E_INVALIDARG;
    if (!s->f.lock(s->interopDevice, 1, &s->object)) return DXGI_ERROR_DEVICE_REMOVED;
    s->locked = true;
    GLint read{}, draw{}; glGetIntegerv(0x8CAA, &read); glGetIntegerv(0x8CA6, &draw);
    const GLboolean scissor = glIsEnabled(GL_SCISSOR_TEST);
    glDisable(GL_SCISSOR_TEST);
    s->f.bind(ReadFramebuffer, sourceFramebuffer); s->f.bind(DrawFramebuffer, s->framebuffer);
    // OpenGL origin is bottom-left. D3D/WinUI samples the first row at the top.
    s->f.blit(0, static_cast<GLint>(sourceHeight), static_cast<GLint>(sourceWidth), 0,
        0, 0, static_cast<GLint>(s->width), static_cast<GLint>(s->height), GL_COLOR_BUFFER_BIT, GL_NEAREST);
    s->f.bind(ReadFramebuffer, static_cast<GLuint>(read)); s->f.bind(DrawFramebuffer, static_cast<GLuint>(draw));
    if (scissor) glEnable(GL_SCISSOR_TEST);
    if (!s->f.unlock(s->interopDevice, 1, &s->object)) return DXGI_ERROR_DEVICE_REMOVED;
    s->locked = false;
    s->context->CopyResource(s->back.Get(), s->shared.Get());
    HRESULT hr = s->swap->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
    return hr == DXGI_ERROR_WAS_STILL_DRAWING ? S_FALSE : hr;
}
API void __cdecl OurNotesGpuDestroy(Surface* surface) noexcept { if (surface && surface->IsOwner()) delete surface; }
API IDXGISwapChain* __cdecl OurNotesGpuAcquireSwapChain(Surface* s) noexcept {
    if (!s || !s->swap) return nullptr; s->swap->AddRef(); return s->swap.Get();
}
API void __cdecl OurNotesGpuReleaseSwapChain(IDXGISwapChain* swap) noexcept { if (swap) swap->Release(); }
API HRESULT __cdecl OurNotesGpuScaleSwapChain(IDXGISwapChain* swap, float x, float y) noexcept {
    if (!swap || !(x > 0 && y > 0 && x < 1000 && y < 1000)) return E_INVALIDARG;
    ComPtr<IDXGISwapChain2> composition;
    HRESULT hr = swap->QueryInterface(IID_PPV_ARGS(&composition)); if (FAILED(hr)) return hr;
    DXGI_MATRIX_3X2_F matrix{}; matrix._11 = x; matrix._22 = y;
    return composition->SetMatrixTransform(&matrix);
}
// The managed surface gate serializes this call with render-thread presentation
// and destruction. Only D3D is touched here; WGL ownership remains on its thread.
API HRESULT __cdecl OurNotesGpuSetDisplaySize(Surface* s, float x, float y) noexcept {
    if (!s || !s->swap || !s->shared || !s->back) return E_INVALIDARG;
    HRESULT hr = OurNotesGpuScaleSwapChain(s->swap.Get(), x, y);
    if (FAILED(hr)) return hr;
    // SetMatrixTransform is latched by the NEXT Present. Re-copy the retained
    // texture because flip-sequential Present rotates buffers; presenting an old
    // back buffer without copying it may show blank/stale content while paused.
    s->context->CopyResource(s->back.Get(), s->shared.Get());
    return s->swap->Present(0, 0);
}
API HRESULT __cdecl OurNotesGpuTestPresentCount(Surface* s, UINT* count) noexcept {
    if (!s || !s->testOnly || !s->IsOwner() || !count) return E_INVALIDARG;
    return s->swap->GetLastPresentCount(count);
}
API HRESULT __cdecl OurNotesGpuAttachPanel(IUnknown* panel, IDXGISwapChain* swap) noexcept {
    if (!panel) return E_POINTER;
    ComPtr<IWinUISwapChainPanelNative> native;
    HRESULT hr = panel->QueryInterface(IID_PPV_ARGS(&native));
    return FAILED(hr) ? hr : native->SetSwapChain(swap);
}
// Explicit smoke-test entry point: validates the D3D/WinUI boundary on GPU-less runners.
// Never selected by the automatic production renderer and never called a hardware benchmark.
API HRESULT __cdecl OurNotesGpuCreateTest(UINT width, UINT height, Surface** result) noexcept {
    if (!result) return E_POINTER; *result = nullptr;
    if (width < 16 || height < 16 || width > 4096 || height > 4096) return E_INVALIDARG;
    try {
        auto s = std::make_unique<Surface>(); s->testOnly = true; s->width = width; s->height = height;
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &s->device, nullptr, &s->context); if (FAILED(hr)) return hr;
        hr = CreateSwap(*s); if (FAILED(hr)) return hr;
        ComPtr<ID3D11RenderTargetView> target; hr = s->device->CreateRenderTargetView(s->shared.Get(), nullptr, &target); if (FAILED(hr)) return hr;
        const float color[] = { .125f, .5f, .75f, 1.f }; s->context->ClearRenderTargetView(target.Get(), color);
        s->context->CopyResource(s->back.Get(), s->shared.Get()); hr = s->swap->Present(0, 0); if (FAILED(hr)) return hr;
        *result = s.release(); return S_OK;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}
API HRESULT __cdecl OurNotesGpuTestPixel(Surface* s, uint32_t* rgba) noexcept {
    if (!s || !s->testOnly || !s->IsOwner() || !rgba) return E_INVALIDARG;
    D3D11_TEXTURE2D_DESC description{}; s->shared->GetDesc(&description);
    description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0; description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging; HRESULT hr = s->device->CreateTexture2D(&description, nullptr, &staging); if (FAILED(hr)) return hr;
    s->context->CopyResource(staging.Get(), s->shared.Get()); D3D11_MAPPED_SUBRESOURCE mapped{};
    hr = s->context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped); if (FAILED(hr)) return hr;
    std::memcpy(rgba, mapped.pData, 4); s->context->Unmap(staging.Get(), 0); return S_OK;
}

// Query without opening a GL context. This is the OS preference order, not a
// fabricated report of the active rendering device.
API HRESULT __cdecl OurNotesGpuPreferredAdapter(UINT preference, wchar_t* name, UINT capacity) noexcept {
    if (!name || capacity < 128 || preference > 2) return E_INVALIDARG;
    name[0] = 0;
    ComPtr<IDXGIFactory6> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory)); if (FAILED(hr)) return hr;
    for (UINT index = 0; ; ++index) {
        ComPtr<IDXGIAdapter1> adapter;
        hr = factory->EnumAdapterByGpuPreference(index, static_cast<DXGI_GPU_PREFERENCE>(preference), IID_PPV_ARGS(&adapter));
        if (FAILED(hr)) return hr;
        DXGI_ADAPTER_DESC1 description{}; hr = adapter->GetDesc1(&description); if (FAILED(hr)) return hr;
        if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
        std::wmemcpy(name, description.Description, 128); name[127] = 0; return S_OK;
    }
}
API HRESULT __cdecl OurNotesGpuAdapterName(Surface* s, wchar_t* name, UINT capacity) noexcept {
    if (!s || !name || capacity < 128) return E_INVALIDARG;
    name[0] = 0;
    ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter;
    HRESULT hr = s->device.As(&dxgi); if (FAILED(hr)) return hr;
    hr = dxgi->GetAdapter(&adapter); if (FAILED(hr)) return hr;
    DXGI_ADAPTER_DESC description{}; hr = adapter->GetDesc(&description); if (FAILED(hr)) return hr;
    std::wmemcpy(name, description.Description, 128); name[127] = 0; return S_OK;
}
