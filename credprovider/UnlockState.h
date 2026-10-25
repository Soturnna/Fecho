#pragma once
#include <windows.h>
#include <credentialprovider.h>

// Estado compartilhado entre o provedor e o ouvinte do named pipe.
// O agente (rodando como o usuário) envia usuário/senha por um pipe local
// protegido; o provedor pega esses dados e manda o Windows desbloquear.
namespace UnlockState
{
    // Inicia o ouvinte do pipe uma única vez. userSid pode ser nullptr.
    void Start(PSID userSid);
    void Stop();

    // O provedor informa como avisar o LogonUI de que há credencial pronta.
    void SetNotify(ICredentialProviderEvents* pcpe, UINT_PTR context);
    void ClearNotify();

    bool HasPending();

    // Pega (e limpa) a credencial pendente. Os buffers são apagados da memória.
    // Devolve false se não havia nada pendente.
    bool TakePending(PWSTR domain, DWORD domainCch, PWSTR user, DWORD userCch,
                     PWSTR password, DWORD passwordCch);
}
