#pragma once
#define SECURITY_WIN32
#include <windows.h>
#include <ntsecapi.h>
#include <sspi.h>
#include <credentialprovider.h>

// Empacota usuário/domínio/senha no formato que o Windows espera para um
// logon interativo (KERB_INTERACTIVE_UNLOCK_LOGON). Baseado no exemplo
// oficial de Credential Provider da Microsoft.

HRESULT KerbInteractiveUnlockLogonInit(
    PWSTR pwzDomain, PWSTR pwzUsername, PWSTR pwzPassword,
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus,
    KERB_INTERACTIVE_UNLOCK_LOGON* pkiul);

HRESULT KerbInteractiveUnlockLogonPack(
    const KERB_INTERACTIVE_UNLOCK_LOGON& rkiulIn,
    BYTE** prgb, DWORD* pcb);

HRESULT RetrieveNegotiateAuthPackage(ULONG* pulAuthPackage);
