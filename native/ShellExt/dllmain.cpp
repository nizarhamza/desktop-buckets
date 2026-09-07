// Desktop Buckets — Windows 11 modern context-menu handler.
//
// Implements IExplorerCommand. Registered (not via the registry) through a
// signed sparse MSIX package that declares a windows.fileExplorerContextMenus
// extension; Explorer hosts this DLL in a COM surrogate (dllhost.exe).
//
// "New Bucket" appears on the folder-background and folder context menus and,
// when invoked, launches DesktopBuckets.exe (sitting next to this DLL) with
//   --new-bucket "<folder>"

#include <windows.h>
#include <shlobj.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/implements.h>
#include <wrl/module.h>
#include <string>

using namespace Microsoft::WRL;

// {13D2D614-C25F-438C-B9DE-A842FC2B5743} — must match AppxManifest.xml
#define NEWBUCKET_CLSID_STR "13D2D614-C25F-438C-B9DE-A842FC2B5743"
static const CLSID CLSID_NewBucketCommand =
{ 0x13d2d614, 0xc25f, 0x438c, { 0xb9, 0xde, 0xa8, 0x42, 0xfc, 0x2b, 0x57, 0x43 } };

static HMODULE g_hModule = nullptr;

static std::wstring ModuleDir()
{
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(g_hModule, path, ARRAYSIZE(path));
    PathRemoveFileSpecW(path);
    return path;
}

class __declspec(uuid(NEWBUCKET_CLSID_STR)) NewBucketCommand
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand, IObjectWithSite>
{
public:
    // ---- IExplorerCommand ----------------------------------------------

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* ppszName) override
    {
        return SHStrDupW(L"New Bucket", ppszName);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* ppszIcon) override
    {
        const std::wstring icon = ModuleDir() + L"\\DesktopBuckets.exe,0";
        return SHStrDupW(icon.c_str(), ppszIcon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* ppszInfotip) override
    {
        *ppszInfotip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) override
    {
        *pguidCommandName = CLSID_NewBucketCommand;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* pCmdState) override
    {
        *pCmdState = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) override
    {
        *pFlags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) override
    {
        *ppEnum = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* psiItemArray, IBindCtx*) noexcept override
    {
        std::wstring folder = FolderFromSelection(psiItemArray);
        if (folder.empty())
            folder = KnownFolder(FOLDERID_Desktop);

        const std::wstring exe = ModuleDir() + L"\\DesktopBuckets.exe";
        const std::wstring args = L"--new-bucket \"" + folder + L"\"";

        SHELLEXECUTEINFOW sei{ sizeof(sei) };
        sei.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
        sei.lpVerb = L"open";
        sei.lpFile = exe.c_str();
        sei.lpParameters = args.c_str();
        sei.nShow = SW_SHOWNORMAL;
        return ShellExecuteExW(&sei) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    // ---- IObjectWithSite ---------------------------------------------

    IFACEMETHODIMP SetSite(IUnknown* site) noexcept override { m_site = site; return S_OK; }
    IFACEMETHODIMP GetSite(REFIID riid, void** ppv) noexcept override
    {
        return m_site ? m_site.CopyTo(riid, ppv) : E_FAIL;
    }

private:
    static std::wstring FolderFromSelection(IShellItemArray* items)
    {
        std::wstring result;
        if (!items) return result;

        DWORD count = 0;
        if (FAILED(items->GetCount(&count)) || count == 0) return result;

        ComPtr<IShellItem> item;
        if (FAILED(items->GetItemAt(0, &item))) return result;

        PWSTR psz = nullptr;
        if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &psz)) && psz)
        {
            result = psz;
            CoTaskMemFree(psz);
        }
        return result;
    }

    static std::wstring KnownFolder(REFKNOWNFOLDERID id)
    {
        std::wstring result;
        PWSTR psz = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(id, 0, nullptr, &psz)) && psz)
        {
            result = psz;
            CoTaskMemFree(psz);
        }
        return result;
    }

    ComPtr<IUnknown> m_site;
};

CoCreatableClass(NewBucketCommand);

// ---- COM DLL exports (surrogate loads us via DllGetClassObject) --------

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    return Module<InProc>::GetModule().GetClassObject(rclsid, riid, ppv);
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE hInstance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hModule = hInstance;
        DisableThreadLibraryCalls(hInstance);
    }
    return TRUE;
}
