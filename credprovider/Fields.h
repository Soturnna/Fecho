#pragma once
#include <credentialprovider.h>

// A tela do Fecho é mínima: só um rótulo. O desbloqueio é automático, então
// não há campo de senha para o usuário digitar nada aqui.
enum FECHO_FIELD_ID
{
    FFI_LABEL = 0,
    FFI_NUM_FIELDS = 1,
};

static const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR s_rgFieldDescriptors[] =
{
    { FFI_LABEL, CPFT_LARGE_TEXT, const_cast<PWSTR>(L"Fecho") },
};
