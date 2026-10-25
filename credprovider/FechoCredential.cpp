#include "FechoCredential.h"
#include "Fields.h"
#include "Guid.h"
#include "CredPacking.h"
#include "UnlockState.h"
#include <shlwapi.h>

#pragma comment(lib, "shlwapi.lib")

static HRESULT CoTaskString(PCWSTR src, PWSTR* dst)
{
    *dst = nullptr;
    size_t cch = wcslen(src) + 1;
    *dst = (PWSTR)CoTaskMemAlloc(cch * sizeof(wchar_t));
    if (!*dst) return E_OUTOFMEMORY;
    wcscpy_s(*dst, cch, src);
    return S_OK;
}

FechoCredential::FechoCredential()
    : _cRef(1), _cpus(CPUS_UNLOCK_WORKSTATION), _pcpce(nullptr) {}

FechoCredential::~FechoCredential() {}

ULONG FechoCredential::Release()
{
    LONG r = --_cRef;
    if (!r) delete this;
    return r;
}

HRESULT FechoCredential::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredential)
    {
        *ppv = static_cast<ICredentialProviderCredential*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

HRESULT FechoCredential::Advise(ICredentialProviderCredentialEvents* pcpce)
{
    if (_pcpce) _pcpce->Release();
    _pcpce = pcpce;
    if (_pcpce) _pcpce->AddRef();
    return S_OK;
}

HRESULT FechoCredential::UnAdvise()
{
    if (_pcpce) _pcpce->Release();
    _pcpce = nullptr;
    return S_OK;
}

HRESULT FechoCredential::SetSelected(BOOL* pbAutoLogon)
{
    // Se há uma credencial pendente (o agente mandou), peça logon imediato.
    *pbAutoLogon = UnlockState::HasPending() ? TRUE : FALSE;
    return S_OK;
}

HRESULT FechoCredential::SetDeselected() { return S_OK; }

HRESULT FechoCredential::GetFieldState(DWORD dwFieldID,
    CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
    CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis)
{
    if (dwFieldID >= FFI_NUM_FIELDS) return E_INVALIDARG;
    *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
    *pcpfis = CPFIS_NONE;
    return S_OK;
}

HRESULT FechoCredential::GetStringValue(DWORD dwFieldID, PWSTR* ppwsz)
{
    if (dwFieldID == FFI_LABEL) return CoTaskString(L"Desbloquear com o Fecho", ppwsz);
    return E_INVALIDARG;
}

HRESULT FechoCredential::GetBitmapValue(DWORD, HBITMAP*) { return E_NOTIMPL; }

HRESULT FechoCredential::GetSerialization(
    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
    PWSTR* ppwszOptionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon)
{
    *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
    if (ppwszOptionalStatusText) *ppwszOptionalStatusText = nullptr;
    if (pcpsiOptionalStatusIcon) *pcpsiOptionalStatusIcon = CPSI_NONE;

    wchar_t domain[256] = {};
    wchar_t user[256] = {};
    wchar_t password[512] = {};
    if (!UnlockState::TakePending(domain, 256, user, 256, password, 512))
    {
        return S_OK; // nada pendente: não finaliza
    }

    HRESULT hr;
    KERB_INTERACTIVE_UNLOCK_LOGON kiul;
    hr = KerbInteractiveUnlockLogonInit(domain[0] ? domain : nullptr, user, password, _cpus, &kiul);
    if (SUCCEEDED(hr))
    {
        BYTE* rgb = nullptr;
        DWORD cb = 0;
        hr = KerbInteractiveUnlockLogonPack(kiul, &rgb, &cb);
        if (SUCCEEDED(hr))
        {
            ULONG ulAuthPackage = 0;
            hr = RetrieveNegotiateAuthPackage(&ulAuthPackage);
            if (SUCCEEDED(hr))
            {
                pcpcs->ulAuthenticationPackage = ulAuthPackage;
                pcpcs->clsidCredentialProvider = CLSID_FechoProvider;
                pcpcs->rgbSerialization = rgb;
                pcpcs->cbSerialization = cb;
                *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;
            }
            else
            {
                CoTaskMemFree(rgb);
            }
        }
    }

    SecureZeroMemory(password, sizeof(password));
    return SUCCEEDED(hr) ? S_OK : hr;
}

HRESULT FechoCredential::ReportResult(
    NTSTATUS, NTSTATUS,
    PWSTR* ppwszOptionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon)
{
    if (ppwszOptionalStatusText) *ppwszOptionalStatusText = nullptr;
    if (pcpsiOptionalStatusIcon) *pcpsiOptionalStatusIcon = CPSI_NONE;
    return S_OK;
}
