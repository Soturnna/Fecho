# Fecho

Desbloqueie e controle seu PC Windows pelo iPhone, na rede local, sem servidor
intermediário.

| Peça | Pasta | O que faz |
|---|---|---|
| App do iPhone | `ios/` | SwiftUI, Face ID, chaves no Secure Enclave, Siri/Atalhos |
| Agente do Windows | `agent/Fecho.Agent` | Ícone na bandeja, executa os comandos |
| Núcleo de segurança | `agent/Fecho.Core` | Protocolo, assinaturas, anti-replay, pareamento |
| Servidor | `agent/Fecho.Server` | WebSocket sobre TLS com certificado próprio |
| Testes | `agent/Fecho.Core.Tests` | Ataques simulados + teste ponta a ponta com TLS real |

A segurança está descrita em [`docs/SEGURANCA.md`](docs/SEGURANCA.md). Leia
antes de mudar qualquer coisa no protocolo.

## Como obter os arquivos

Cada envio para a branch `main` dispara o workflow **Compilar** no GitHub
Actions, que gera dois artefatos:

- **Fecho-Windows** → `Fecho.exe` (não precisa instalar .NET)
- **Fecho-iPhone** → `Fecho.ipa` sem assinatura, para instalar pelo iloader

## Primeiro uso

1. No PC, rode `Fecho.exe`. Na primeira vez, o Windows pergunta sobre o
   firewall: marque **somente Redes privadas**.
2. Clique com o botão direito no ícone do Fecho (escudo) na bandeja e
   escolha **Parear iPhone…**.
3. No iPhone, abra o Fecho, permita a câmera e aponte para o QR code.
4. Permita o acesso à rede local quando o iOS pedir.

Dica: reserve um IP fixo para o PC no roteador (reserva DHCP). O endereço
vai no QR; se ele mudar, é preciso parear de novo.

## Roadmap

- [x] Fase 1 — Design das telas
- [x] Fase 2 — Agente: pareamento, energia, mídia, volume, mouse, teclado, área de transferência
- [x] Fase 3 — App do iPhone conectado, com Face ID e Atalhos
- [x] Fase 4 — Credential Provider: desbloqueio real da tela (em testes)
- [x] Fase 5 — Troca de arquivos
- [x] Ver a tela do PC e controlar por toque
- [x] Widgets, Central de Controle e app do Apple Watch (via conta paga; no sideload grátis pode não instalar)
- [ ] Fase 6 — Proximidade por Bluetooth

## Desbloqueio pela tela de login (fase 4, em testes)

O componente que desbloqueia a tela é uma DLL (Credential Provider) que o
Windows carrega na tela de login. Ele **acrescenta** uma opção; a senha e o
PIN continuam funcionando normalmente.

Para ativar (no menu do ícone do Fecho → "Desbloqueio pelo iPhone"):

1. **Instalar componente (admin)** — aceite o aviso de administrador. É criado
   um ponto de restauração antes de qualquer mudança.
2. **Definir a senha do Windows** — fica guardada só no PC, cifrada por DPAPI.
3. Bloqueie o PC (Win+L) e toque em **Desbloquear com Face ID** no iPhone.

### Recuperação do desbloqueio

Se algo der errado na tela de login, você **nunca** fica preso: use a senha
ou o PIN normalmente. Para remover o componente:

- Pelo Fecho: menu → "Desbloqueio pelo iPhone" → "Remover o componente".
- Ou, se o Windows não iniciar direito, entre no **Modo de Segurança**
  (que não carrega provedores de terceiros) e rode `Fecho.exe` →
  "Remover o componente", ou restaure o ponto de restauração criado na
  instalação.

## Rodar os testes localmente

```
cd agent
dotnet run --project Fecho.Core.Tests
```
