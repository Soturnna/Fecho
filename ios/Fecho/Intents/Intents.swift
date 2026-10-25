import AppIntents

/// Ações para Siri, Atalhos e botão de Ação. Usam só a chave de controle.
/// Desbloquear, desligar e reiniciar ficam de fora de propósito.

private func runOnPC(_ cmd: String, _ args: [String: Any] = [:]) async throws {
    guard let info = PairingStore.load() else { throw AgentError.notPaired }
    let client = AgentClient.shared
    await client.configure(info)
    try await client.request(cmd, args)
}

struct LockPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Bloquear o PC"
    static var description = IntentDescription("Bloqueia a tela do PC pareado.")
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    func perform() async throws -> some IntentResult & ProvidesDialog {
        try await runOnPC("lock")
        return .result(dialog: "PC bloqueado.")
    }
}

struct SleepPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Suspender o PC"
    static var description = IntentDescription("Coloca o PC pareado em suspensão.")
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    func perform() async throws -> some IntentResult & ProvidesDialog {
        try await runOnPC("sleep")
        return .result(dialog: "PC suspenso.")
    }
}

struct PlayPauseIntent: AppIntent {
    static var title: LocalizedStringResource = "Reproduzir ou pausar no PC"
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    func perform() async throws -> some IntentResult {
        try await runOnPC("media", ["action": "playpause"])
        return .result()
    }
}

struct NextTrackIntent: AppIntent {
    static var title: LocalizedStringResource = "Próxima faixa no PC"
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresAuthentication

    func perform() async throws -> some IntentResult {
        try await runOnPC("media", ["action": "next"])
        return .result()
    }
}

struct FechoShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: LockPCIntent(),
                    phrases: ["Bloquear o PC no \(.applicationName)", "Trancar o PC no \(.applicationName)"],
                    shortTitle: "Bloquear o PC", systemImageName: "lock.fill")
        AppShortcut(intent: SleepPCIntent(),
                    phrases: ["Suspender o PC no \(.applicationName)"],
                    shortTitle: "Suspender o PC", systemImageName: "moon.fill")
        AppShortcut(intent: PlayPauseIntent(),
                    phrases: ["Pausar a música no \(.applicationName)", "Tocar a música no \(.applicationName)"],
                    shortTitle: "Reproduzir ou pausar", systemImageName: "playpause.fill")
        AppShortcut(intent: NextTrackIntent(),
                    phrases: ["Próxima faixa no \(.applicationName)"],
                    shortTitle: "Próxima faixa", systemImageName: "forward.fill")
    }
}
