#include "FechoProvider.h"
#include "Fields.h"
#include "UnlockState.h"
#include <new>
#include <sddl.h>
#include <shlwapi.h>
#pragma comment(lib, "shlwapi.lib")

FechoProvider::FechoProvider()
    : _cRef(1), _cpus(CPUS_INVALID), _pCredential(nullptr), _started(false) {}

FechoProvider::~FechoProvider()
{
    if (_pCredential) _pCredential->Release();
    UnlockState::ClearNotify();
}

ULONG FechoProvider::Release()
{
    LONG r = --_cRef;
    if (!r) delete this;
    return r;
}

HRESULT FechoProvider::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_ICredentialProvider)
    {
        *ppv = static_cast<ICredentialProvider*>(this);
    }
    else if (riid == IID_ICredentialProviderSetUserArray)
    {
        *ppv = static_cast<ICredentialProviderSetUserArray*>(this);
    }
    else
    {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

HRESULT FechoProvider::SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD)
{
    // O Fecho só atua no desbloqueio da estação (e no logon, caso você queira).
    switch (cpus)
    {
    case CPUS_UNLOCK_WORKSTATION:
    case CPUS_LOGON:
        _cpus = cpus;
        if (!_pCredential)
        {
            _pCredential = new (std::nothrow) FechoCredential();
            if (!_pCredential) return E_OUTOFMEMORY;
        }
        _pCredential->SetUsageScenario(cpus);
        if (!_started)
        {
            UnlockState::Start(nullptr);
            _started = true;
        }
        return S_OK;
    default:
        return E_NOTIMPL;
    }
}

HRESULT FechoProvider::SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*)
{
    return E_NOTIMPL;
}

HRESULT FechoProvider::Advise(ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext)
{
    UnlockState::SetNotify(pcpe, upAdviseContext);
    return S_OK;
}

HRESULT FechoProvider::UnAdvise()
{
    UnlockState::ClearNotify();
    return S_OK;
}

HRESULT FechoProvider::GetFieldDescriptorCount(DWORD* pdwCount)
{
    *pdwCount = FFI_NUM_FIELDS;
    return S_OK;
}

HRESULT FechoProvider::GetFieldDescriptorAt(DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd)
{
    if (dwIndex >= FFI_NUM_FIELDS || !ppcpfd) return E_INVALIDARG;
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* pcpfd =
        (CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*)CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR));
    if (!pcpfd) return E_OUTOFMEMORY;
    *pcpfd = s_rgFieldDescriptors[dwIndex];
    if (s_rgFieldDescriptors[dwIndex].pszLabel)
    {
        SHStrDupW(s_rgFieldDescriptors[dwIndex].pszLabel, &pcpfd->pszLabel);
    }
    *ppcpfd = pcpfd;
    return S_OK;
}

HRESULT FechoProvider::GetCredentialCount(DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault)
{
    *pdwCount = 1;
    *pdwDefault = 0;
    // Se o agente já mandou a credencial, o LogonUI entra sozinho.
    *pbAutoLogonWithDefault = UnlockState::HasPending() ? TRUE : FALSE;
    return S_OK;
}

HRESULT FechoProvider::GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc)
{
    if (dwIndex != 0 || !_pCredential || !ppcpc) return E_INVALIDARG;
    return _pCredential->QueryInterface(IID_ICredentialProviderCredential, (void**)ppcpc);
}

HRESULT FechoProvider::SetUserArray(ICredentialProviderUserArray* users)
{
    // Captura o SID do usuário bloqueado para restringir o pipe a ele.
    if (!users) return S_OK;
    DWORD count = 0;
    if (SUCCEEDED(users->GetCount(&count)) && count > 0)
    {
        ICredentialProviderUser* user = nullptr;
        if (SUCCEEDED(users->GetAt(0, &user)) && user)
        {
            PWSTR sidStr = nullptr;
            if (SUCCEEDED(user->GetSid(&sidStr)) && sidStr)
            {
                PSID sid = nullptr;
                if (ConvertStringSidToSidW(sidStr, &sid))
                {
                    UnlockState::Start(sid);
                    _started = true;
                    LocalFree(sid);
                }
                CoTaskMemFree(sidStr);
            }
            user->Release();
        }
    }
    return S_OK;
}
