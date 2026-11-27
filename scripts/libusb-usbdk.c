// SPDX-License-Identifier: GPL-2.0-or-later
// QEMU 11 initializes libusb and leaves the WinUSB backend in place. UsbDk is what can take a device
// away from Windows, and its bus and port numbers are not the WinUSB ones. This DLL is loaded in place
// of libusb-1.0.dll: libusb_init runs the real one, then selects UsbDk. Every other export is forwarded
// to libusb_orig.dll, which is the libusb that shipped with the runtime.

typedef int BOOL;
typedef unsigned long DWORD;
typedef unsigned short wchar;
typedef void *HMODULE;
typedef const wchar *LPCWSTR;
typedef const char *LPCSTR;

__declspec(dllimport) BOOL __stdcall GetModuleHandleExW(DWORD flags, LPCWSTR name, HMODULE *module);
__declspec(dllimport) DWORD __stdcall GetModuleFileNameW(HMODULE module, wchar *file, DWORD size);
__declspec(dllimport) HMODULE __stdcall LoadLibraryExW(LPCWSTR file, void *reserved, DWORD flags);
__declspec(dllimport) void *__stdcall GetProcAddress(HMODULE module, LPCSTR name);

enum { FromAddress = 0x4, UnchangedRefcount = 0x2, AlteredSearch = 0x8, UseUsbDk = 1 };

typedef int (*init_fn)(void **ctx);
typedef int (*option_fn)(void *ctx, int option);

static init_fn real_init;
static option_fn real_option;

static void load_real(void)
{
    wchar path[260];
    HMODULE self = 0;
    DWORD length;
    wchar *slash;
    static const wchar name[] = { 'l','i','b','u','s','b','_','o','r','i','g','.','d','l','l', 0 };
    int i;
    if (real_init) return;
    if (!GetModuleHandleExW(FromAddress | UnchangedRefcount, (LPCWSTR)load_real, &self)) return;
    length = GetModuleFileNameW(self, path, 260);
    if (length == 0 || length >= 260) return;
    slash = path + length;
    while (slash > path && slash[-1] != '\\') slash--;
    for (i = 0; name[i]; i++) {
        if ((DWORD)(slash - path) + (DWORD)i + 1 >= 260) return;
        slash[i] = name[i];
    }
    slash[i] = 0;
    self = LoadLibraryExW(path, 0, AlteredSearch);
    if (!self) return;
    real_init = (init_fn)GetProcAddress(self, "libusb_init");
    real_option = (option_fn)GetProcAddress(self, "libusb_set_option");
}

int libusb_init(void **ctx)
{
    int rc;
    load_real();
    if (!real_init) return -1;
    rc = real_init(ctx);
    if (rc == 0 && ctx && *ctx && real_option) real_option(*ctx, UseUsbDk);
    return rc;
}

BOOL __stdcall DllMain(HMODULE module, DWORD reason, void *reserved)
{
    (void)module; (void)reason; (void)reserved;
    return 1;
}
