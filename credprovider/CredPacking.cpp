#include "CredPacking.h"
#include <intsafe.h>
#include <strsafe.h>

static void UnicodeStringInitWithString(PWSTR pwz, UNICODE_STRING* pus)
{
    if (pwz)
    {
        size_t lch = 0;
        StringCchLengthW(pwz, STRSAFE_MAX_CCH, &lch);
        pus->Length = (USHORT)(lch * sizeof(WCHAR));
        pus->MaximumLength = (USHORT)((lch + 1) * sizeof(WCHAR));
        pus->Buffer = pwz;
    }
    else
    {
        pus->Length = 0;
        pus->MaximumLength = 0;
        pus->Buffer = nullptr;
    }
}

HRESULT KerbInteractiveUnlockLogonInit(
    PWSTR pwzDomain, PWSTR pwzUsername, PWSTR pwzPassword,
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus,
    KERB_INTERACTIVE_UNLOCK_LOGON* pkiul)
{
    KERB_INTERACTIVE_UNLOCK_LOGON kiul;
    ZeroMemory(&kiul, sizeof(kiul));

    KERB_INTERACTIVE_LOGON* pkil = &kiul.Logon;

    UnicodeStringInitWithString(pwzDomain, &pkil->LogonDomainName);
    UnicodeStringInitWithString(pwzUsername, &pkil->UserName);
    UnicodeStringInitWithString(pwzPassword, &pkil->Password);

    switch (cpus)
    {
    case CPUS_UNLOCK_WORKSTATION:
        pkil->MessageType = KerbWorkstationUnlockLogon;
        break;
    case CPUS_LOGON:
        pkil->MessageType = KerbInteractiveLogon;
        break;
    default:
        pkil->MessageType = KerbInteractiveLogon;
        break;
    }

    *pkiul = kiul;
    return S_OK;
}

// Copia uma UNICODE_STRING para dentro de um buffer contíguo, guardando o
// deslocamento (offset) em vez do ponteiro, como o LSA exige.
static HRESULT UnicodeStringPackedUnicodeStringCopy(
    const UNICODE_STRING& rus, PWSTR pwzBuffer, UNICODE_STRING* pus)
{
    HRESULT hr;
    if (rus.MaximumLength != 0)
    {
        if (rus.Buffer)
        {
            CopyMemory(pwzBuffer, rus.Buffer, rus.MaximumLength);
            pus->Buffer = (PWSTR)pwzBuffer;
            hr = S_OK;
        }
        else
        {
            hr = E_INVALIDARG;
        }
    }
    else
    {
        pus->Buffer = nullptr;
        hr = S_OK;
    }
    if (SUCCEEDED(hr))
    {
        pus->Length = rus.Length;
        pus->MaximumLength = rus.MaximumLength;
    }
    return hr;
}

HRESULT KerbInteractiveUnlockLogonPack(
    const KERB_INTERACTIVE_UNLOCK_LOGON& rkiulIn,
    BYTE** prgb, DWORD* pcb)
{
    HRESULT hr;
    const KERB_INTERACTIVE_LOGON* pkilIn = &rkiulIn.Logon;

    DWORD cb = sizeof(rkiulIn)
        + pkilIn->LogonDomainName.MaximumLength
        + pkilIn->UserName.MaximumLength
        + pkilIn->Password.MaximumLength;

    KERB_INTERACTIVE_UNLOCK_LOGON* pkiul = (KERB_INTERACTIVE_UNLOCK_LOGON*)CoTaskMemAlloc(cb);
    if (pkiul)
    {
        ZeroMemory(&pkiul->LogonId, sizeof(pkiul->LogonId));

        KERB_INTERACTIVE_LOGON* pkilOut = &pkiul->Logon;
        pkilOut->MessageType = pkilIn->MessageType;

        BYTE* pbBuffer = (BYTE*)pkiul + sizeof(*pkiul);

        hr = UnicodeStringPackedUnicodeStringCopy(pkilIn->LogonDomainName, (PWSTR)pbBuffer, &pkilOut->LogonDomainName);
        if (SUCCEEDED(hr))
        {
            pkilOut->LogonDomainName.Buffer = (PWSTR)(pbBuffer - (BYTE*)pkiul);
            pbBuffer += pkilOut->LogonDomainName.MaximumLength;

            hr = UnicodeStringPackedUnicodeStringCopy(pkilIn->UserName, (PWSTR)pbBuffer, &pkilOut->UserName);
            if (SUCCEEDED(hr))
            {
                pkilOut->UserName.Buffer = (PWSTR)(pbBuffer - (BYTE*)pkiul);
                pbBuffer += pkilOut->UserName.MaximumLength;

                hr = UnicodeStringPackedUnicodeStringCopy(pkilIn->Password, (PWSTR)pbBuffer, &pkilOut->Password);
                if (SUCCEEDED(hr))
                {
                    pkilOut->Password.Buffer = (PWSTR)(pbBuffer - (BYTE*)pkiul);
                }
            }
        }

        if (SUCCEEDED(hr))
        {
            *prgb = (BYTE*)pkiul;
            *pcb = cb;
        }
        else
        {
            CoTaskMemFree(pkiul);
        }
    }
    else
    {
        hr = E_OUTOFMEMORY;
    }
    return hr;
}

HRESULT RetrieveNegotiateAuthPackage(ULONG* pulAuthPackage)
{
    HRESULT hr;
    HANDLE hLsa;

    NTSTATUS status = LsaConnectUntrusted(&hLsa);
    if (SUCCEEDED(HRESULT_FROM_NT(status)))
    {
        ULONG ulAuthPackage;
        LSA_STRING lsaszPackageName;
        LPCSTR szPackageName = NEGOSSP_NAME_A;
        size_t len = strlen(szPackageName);
        lsaszPackageName.Length = (USHORT)len;
        lsaszPackageName.MaximumLength = (USHORT)(len + 1);
        lsaszPackageName.Buffer = (PCHAR)szPackageName;

        status = LsaLookupAuthenticationPackage(hLsa, &lsaszPackageName, &ulAuthPackage);
        if (SUCCEEDED(HRESULT_FROM_NT(status)))
        {
            *pulAuthPackage = ulAuthPackage;
            hr = S_OK;
        }
        else
        {
            hr = HRESULT_FROM_NT(status);
        }
        LsaDeregisterLogonProcess(hLsa);
    }
    else
    {
        hr = HRESULT_FROM_NT(status);
    }
    return hr;
}
