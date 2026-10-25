#include "UnlockState.h"
#include <sddl.h>
#include <wincrypt.h>
#include <algorithm>
#include <strsafe.h>
#include <vector>
#include <string>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "crypt32.lib")

// Protocolo do pipe (UTF-8, uma linha por campo):
//   FECHO1\n <segredoBase64>\n <dominio>\n <usuario>\n <senha>\n
// Resposta: "OK\n" ou "ERR\n".
static const wchar_t* kPipeName = L"\\\\.\\pipe\\Fecho.Unlock";

namespace
{
    CRITICAL_SECTION g_cs;
    bool g_csReady = false;
    bool g_started = false;

    ICredentialProviderEvents* g_notify = nullptr;
    UINT_PTR g_context = 0;

    bool g_pending = false;
    wchar_t g_domain[256];
    wchar_t g_user[256];
    wchar_t g_password[512];

    std::string g_secretB64; // segredo esperado do agente
    PSID g_userSid = nullptr;

    void EnsureCs()
    {
        if (!g_csReady) { InitializeCriticalSection(&g_cs); g_csReady = true; }
    }

    // Pasta %ProgramData%\Fecho
    std::wstring ProgramDataDir()
    {
        wchar_t base[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"ProgramData", base, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) return L"";
        std::wstring dir = base;
        dir += L"\\Fecho";
        CreateDirectoryW(dir.c_str(), nullptr);
        return dir;
    }

    std::string Base64(const BYTE* data, DWORD len)
    {
        DWORD outLen = 0;
        CryptBinaryToStringA(data, len, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, nullptr, &outLen);
        std::string out(outLen, '\0');
        CryptBinaryToStringA(data, len, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, &out[0], &outLen);
        if (!out.empty() && out.back() == '\0') out.pop_back();
        return out;
    }

    // Gera o segredo e grava em %ProgramData%\Fecho\unlock.secret para o agente ler.
    void PrepareSecret()
    {
        BYTE raw[32];
        HCRYPTPROV prov = 0;
        if (CryptAcquireContextW(&prov, nullptr, nullptr, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT))
        {
            CryptGenRandom(prov, sizeof(raw), raw);
            CryptReleaseContext(prov, 0);
        }
        g_secretB64 = Base64(raw, sizeof(raw));

        std::wstring file = ProgramDataDir();
        if (!file.empty())
        {
            file += L"\\unlock.secret";
            HANDLE h = CreateFileW(file.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                                   CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE)
            {
                DWORD written = 0;
                WriteFile(h, g_secretB64.data(), (DWORD)g_secretB64.size(), &written, nullptr);
                CloseHandle(h);
            }
        }
    }

    // DACL: permite SYSTEM e o usuário bloqueado. Sem o SID, cai para
    // "Usuários autenticados" (há só um usuário na tela de bloqueio).
    PSECURITY_DESCRIPTOR BuildPipeSd()
    {
        std::wstring sddl = L"D:(A;;GA;;;SY)";
        if (g_userSid)
        {
            LPWSTR sidStr = nullptr;
            if (ConvertSidToStringSidW(g_userSid, &sidStr))
            {
                sddl += L"(A;;GRGW;;;";
                sddl += sidStr;
                sddl += L")";
                LocalFree(sidStr);
            }
        }
        else
        {
            sddl += L"(A;;GRGW;;;AU)";
        }
        PSECURITY_DESCRIPTOR sd = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr))
            return nullptr;
        return sd;
    }

    std::wstring Utf8ToWide(const std::string& s)
    {
        if (s.empty()) return L"";
        int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
        std::wstring w(n, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], n);
        return w;
    }

    bool ConstantTimeEqual(const std::string& a, const std::string& b)
    {
        if (a.size() != b.size()) return false;
        unsigned char diff = 0;
        for (size_t i = 0; i < a.size(); i++) diff |= (unsigned char)(a[i] ^ b[i]);
        return diff == 0;
    }

    // Processa uma mensagem e, se válida, guarda a credencial pendente.
    bool HandleMessage(const std::string& msg)
    {
        std::vector<std::string> parts;
        size_t start = 0;
        while (start <= msg.size())
        {
            size_t nl = msg.find('\n', start);
            if (nl == std::string::npos) { parts.push_back(msg.substr(start)); break; }
            parts.push_back(msg.substr(start, nl - start));
            start = nl + 1;
        }
        // FECHO1, segredo, dominio, usuario, senha
        if (parts.size() < 5) return false;
        if (parts[0] != "FECHO1") return false;
        if (!ConstantTimeEqual(parts[1], g_secretB64)) return false;

        std::wstring domain = Utf8ToWide(parts[2]);
        std::wstring user = Utf8ToWide(parts[3]);
        std::wstring pass = Utf8ToWide(parts[4]);

        EnterCriticalSection(&g_cs);
        StringCchCopyW(g_domain, 256, domain.c_str());
        StringCchCopyW(g_user, 256, user.c_str());
        StringCchCopyW(g_password, 512, pass.c_str());
        g_pending = true;
        ICredentialProviderEvents* notify = g_notify;
        UINT_PTR ctx = g_context;
        LeaveCriticalSection(&g_cs);

        SecureZeroMemory(&pass[0], pass.size() * sizeof(wchar_t));

        // Avisa o LogonUI que agora há uma credencial pronta.
        if (notify) notify->CredentialsChanged(ctx);
        return true;
    }

    DWORD WINAPI PipeThread(LPVOID)
    {
        PrepareSecret();
        PSECURITY_DESCRIPTOR sd = BuildPipeSd();
        SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };

        for (;;)
        {
            HANDLE pipe = CreateNamedPipeW(
                kPipeName, PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                1, 8192, 8192, 0, sd ? &sa : nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                Sleep(1000);
                continue;
            }

            BOOL connected = ConnectNamedPipe(pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
            if (connected)
            {
                char buf[8192];
                DWORD read = 0;
                std::string msg;
                while (ReadFile(pipe, buf, sizeof(buf), &read, nullptr) && read > 0)
                {
                    msg.append(buf, read);
                    if (msg.size() > 32768) break;
                    // mensagem termina quando vemos 5 linhas
                    if (std::count(msg.begin(), msg.end(), '\n') >= 5) break;
                }
                bool ok = HandleMessage(msg);
                const char* resp = ok ? "OK\n" : "ERR\n";
                DWORD written = 0;
                WriteFile(pipe, resp, (DWORD)strlen(resp), &written, nullptr);
                SecureZeroMemory(buf, sizeof(buf));
                FlushFileBuffers(pipe);
            }
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }
    }
}

namespace UnlockState
{
    void Start(PSID userSid)
    {
        EnsureCs();
        EnterCriticalSection(&g_cs);
        bool already = g_started;
        g_started = true;
        if (userSid && !g_userSid)
        {
            DWORD len = GetLengthSid(userSid);
            g_userSid = (PSID)LocalAlloc(LPTR, len);
            if (g_userSid) CopySid(len, g_userSid, userSid);
        }
        LeaveCriticalSection(&g_cs);
        if (!already)
        {
            HANDLE t = CreateThread(nullptr, 0, PipeThread, nullptr, 0, nullptr);
            if (t) CloseHandle(t);
        }
    }

    void Stop() {}

    void SetNotify(ICredentialProviderEvents* pcpe, UINT_PTR context)
    {
        EnsureCs();
        EnterCriticalSection(&g_cs);
        g_notify = pcpe;
        g_context = context;
        LeaveCriticalSection(&g_cs);
    }

    void ClearNotify()
    {
        EnsureCs();
        EnterCriticalSection(&g_cs);
        g_notify = nullptr;
        g_context = 0;
        LeaveCriticalSection(&g_cs);
    }

    bool HasPending()
    {
        EnsureCs();
        EnterCriticalSection(&g_cs);
        bool p = g_pending;
        LeaveCriticalSection(&g_cs);
        return p;
    }

    bool TakePending(PWSTR domain, DWORD domainCch, PWSTR user, DWORD userCch,
                     PWSTR password, DWORD passwordCch)
    {
        EnsureCs();
        EnterCriticalSection(&g_cs);
        bool p = g_pending;
        if (p)
        {
            StringCchCopyW(domain, domainCch, g_domain);
            StringCchCopyW(user, userCch, g_user);
            StringCchCopyW(password, passwordCch, g_password);
            SecureZeroMemory(g_password, sizeof(g_password));
            g_pending = false;
        }
        LeaveCriticalSection(&g_cs);
        return p;
    }
}
