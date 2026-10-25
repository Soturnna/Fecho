#pragma once
#include <guiddef.h>

// Declaração do CLSID do provedor do Fecho. A definição fica em Guid.cpp
// (único arquivo que inclui <initguid.h>). Precisa bater com o Registro.
// {C3D1E2F0-5A6B-4C8D-9E0F-1A2B3C4D5E6F}
DEFINE_GUID(CLSID_FechoProvider,
    0xc3d1e2f0, 0x5a6b, 0x4c8d, 0x9e, 0x0f, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e, 0x6f);

#define FECHO_CLSID_STRING L"{C3D1E2F0-5A6B-4C8D-9E0F-1A2B3C4D5E6F}"
#define FECHO_PROVIDER_NAME L"Fecho"
