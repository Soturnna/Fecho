# Segurança do Fecho

Este documento descreve como o Fecho protege o PC. Ele é a referência para
qualquer mudança no protocolo: se o código divergir daqui, o código está errado.

## O que estamos protegendo

| Ativo | Por que importa |
|---|---|
| Senha do Windows | Quem a tem entra no PC. |
| Controle do PC (desligar, teclado, arquivos) | Quem controla o teclado controla tudo. |
| Conteúdo da área de transferência | Pode conter senhas e dados pessoais. |

## De quem estamos nos protegendo

1. **Alguém na mesma rede Wi-Fi** (visita, aparelho infectado, Wi-Fi público).
   Pode ver e alterar o tráfego e tentar enviar comandos ao agente.
2. **Alguém que pegue o iPhone desbloqueado por alguns instantes.**
3. **Alguém que grave um comando válido e tente reenviá-lo depois** (*replay*).
4. **Um aparelho que já foi pareado e foi perdido ou vendido.**

Fora do escopo: alguém com acesso administrativo ao próprio PC, ou um iPhone
com jailbreak controlado por um invasor.

## Regras do protocolo

### 1. A senha do Windows nunca sai do PC

O agente guarda a senha cifrada com **DPAPI** (criptografia do Windows atrelada
à conta do usuário). O iPhone nunca a recebe, nunca a envia e nunca a armazena.
O iPhone envia apenas um *pedido de desbloqueio assinado*.

### 2. Pareamento por QR code de uso único

1. Na bandeja do PC, o usuário clica em **Parear iPhone**.
2. O agente gera um **token de pareamento** aleatório (32 bytes) válido por
   **2 minutos** e usável **uma única vez**, e mostra um QR com:
   `fecho://parear?h=<ip>&p=<porta>&f=<impressão digital do certificado>&t=<token>&id=<id do agente>`
3. O iPhone lê o QR, abre a conexão TLS **exigindo** que o certificado do PC
   tenha exatamente aquela impressão digital (SHA-256), e envia suas duas
   chaves públicas junto com o token.
4. O agente confere o token (comparação em tempo constante), registra o
   aparelho e **descarta o token**.

Por que o QR? Porque ele é um canal que só quem está fisicamente na frente do
PC enxerga. Isso impede que alguém na rede se passe pelo PC (ataque
*man-in-the-middle*) já no primeiro contato.

### 3. TLS com certificado fixado (*pinning*)

O agente cria um certificado próprio (ECDSA P-256) na primeira execução. O
iPhone **não** confia em autoridades certificadoras: aceita apenas o
certificado cuja impressão digital veio no QR. Qualquer outro certificado
derruba a conexão.

### 4. Duas chaves no Secure Enclave do iPhone

As chaves privadas são geradas **dentro** do Secure Enclave e não podem ser
exportadas nem copiadas, nem por backup.

| Chave | Exige Face ID? | Pode autorizar |
|---|---|---|
| **controle** | Não (só o iPhone desbloqueado) | volume, mídia, mouse, teclado, bloquear, suspender, área de transferência |
| **desbloqueio** | **Sim, a cada uso** | desbloquear o PC, desligar, reiniciar |

A chave de desbloqueio usa a política `biometryCurrentSet`: se alguém
cadastrar um rosto novo no iPhone, a chave é invalidada e é preciso parear
de novo.

### 5. Todo comando é assinado e de uso único

Cada mensagem tem: `aparelho`, `comando`, `argumentos`, `horario` (ms),
`nonce` (16 bytes aleatórios) e a `assinatura` ECDSA-SHA256 sobre a forma
canônica desses campos.

O agente só executa se **todas** as verificações passarem:

1. o aparelho está pareado e não foi revogado;
2. a assinatura confere com a chave certa para aquele comando;
3. o horário está a no máximo **30 segundos** do relógio do PC;
4. o `nonce` nunca foi visto (cache de 2 minutos), o que impede o replay;
5. o aparelho não excedeu o limite de comandos por segundo.

Falhas são registradas no log de auditoria e respondidas com um erro
genérico, sem dizer qual verificação falhou.

A assinatura cobre os **bytes exatos** do conteúdo enviado (o JSON é enviado
em Base64 e o agente verifica antes de interpretar). Assim, não existe
ambiguidade de formatação entre Swift e C#. O conteúdo também traz o `id` do
agente, então um comando feito para um PC não serve para outro.

### 5.1 Sessões para eventos rápidos

Mover o mouse gera dezenas de eventos por segundo, e assinar cada um no
Secure Enclave seria lento. Por isso:

1. O app envia `session.open` **assinado** com a chave de controle.
2. O agente gera uma chave de sessão aleatória (32 bytes) e a devolve dentro
   do canal TLS fixado.
3. Os eventos seguintes usam **HMAC-SHA256** com essa chave e um contador
   (`seq`) que só pode crescer, o que impede o replay.
4. A sessão morre quando a conexão cai.

A sessão **só** autoriza comandos da classe de controle (mouse, teclado,
texto, volume, mídia). Desbloquear, desligar e reiniciar **sempre** exigem a
assinatura da chave de desbloqueio, com Face ID.

### 6. Rede

- O agente escuta apenas na rede local; a regra de firewall vale só para o
  perfil de rede **Privada** do Windows.
- Nada passa pela internet. Não há servidor intermediário.

### 7. Revogação

O menu da bandeja lista os aparelhos pareados e permite remover qualquer um.
A remoção vale na hora, inclusive para conexões já abertas.

### 8. Auditoria

Todo comando aceito ou recusado é registrado com data, aparelho e resultado.
Os últimos eventos aparecem em "Atividade recente" no app.

### 9. Arquivos

**Do iPhone para o PC** (não exige Face ID):

- O arquivo cai **sempre** em `Downloads\Fecho`. O nome enviado é limpo:
  caminhos (`../`), caracteres proibidos e nomes reservados do Windows
  (`CON`, `NUL`…) são removidos; nomes repetidos viram `nome (2).ext`.
- Chega em blocos de até 48 KB, em ordem, até o tamanho declarado
  (máximo 500 MB). No fim, o agente confere o **SHA-256** do arquivo inteiro.
  Se não bater, o arquivo é descartado.
- Enquanto não termina, fica numa pasta oculta `.parcial`. Se a conexão
  cair, o parcial é apagado.
- O arquivo recebido ganha a marca "veio da internet" do Windows, que faz o
  sistema avisar antes de abrir um executável.

**Do PC para o iPhone** (exige Face ID):

- Abrir as pastas do PC exige um `files.grant` assinado com a **chave de
  desbloqueio**. A permissão vale **10 minutos** e **só para aquela conexão**.
- Só as pastas pessoais ficam visíveis: Área de Trabalho, Documentos,
  Downloads, Imagens, Vídeos e Música. Nada de `C:\` ou pastas do sistema.
- O caminho pedido é recusado se tiver `..`, `\`, `:`, segmentos vazios,
  espaço ou ponto no fim, ou se passar por **atalho/junção** (que poderia
  apontar para fora da pasta). Arquivos ocultos e de sistema não aparecem.

### 10. Ver a tela do PC

- O streaming da tela vale como **controle** (iPhone desbloqueado e pareado).
  Quem já pode mexer no mouse e no teclado também pode ver a tela.
- Os quadros vão em JPEG **só dentro da conexão TLS com certificado fixado**,
  na rede local. Nada é gravado, nem no PC nem no iPhone.
- Enquanto o PC está **bloqueado**, a captura falha de propósito (a tela de
  bloqueio é uma "área de trabalho segura" que o agente não enxerga), então o
  streaming pausa sozinho e volta quando você desbloqueia.
- O controle por toque usa posição **absoluta**: o toque na imagem vira a
  mesma posição do cursor no PC. Assim como os outros controles, só funciona
  com o PC desbloqueado.
- O quadro e a largura são limitados; sob carga, o PC descarta quadros em vez
  de acumular atraso.

### 11. Desbloqueio da tela de login

- A senha do Windows fica **só no PC**, cifrada com DPAPI (conta do usuário).
  O iPhone nunca a recebe nem a envia.
- O desbloqueio exige a chave de **desbloqueio** (Face ID a cada vez). O PC só
  age depois de validar essa assinatura.
- O agente (como usuário) entrega a senha ao componente de login por um
  **named pipe local** restrito ao usuário bloqueado e ao SYSTEM, com um
  segredo por sessão. Nada trafega pela rede.
- O componente **acrescenta** uma opção na tela de login; nunca substitui a
  senha ou o PIN, então uma falha dele não tranca o PC. Há desinstalador e
  ponto de restauração; o Modo de Segurança não carrega o componente.

## O que ainda não está implementado

- **Proximidade por Bluetooth**: fase 6.
- **Apple Watch**: o relógio não tem acesso às chaves do iPhone. Ele vai pedir
  ao iPhone para assinar (fase 6).
