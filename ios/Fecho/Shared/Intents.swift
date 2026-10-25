import AppIntents

// Ações para Siri, Atalhos, widgets e Central de Controle.
// Bloquear, suspender e mídia usam a chave de controle (sem Face ID), então
// rodam direto do widget. Desbloquear exige Face ID e por isso abre o app.

struct LockPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Bloquear o PC"
    static var description = IntentDescription("Bloqueia a tela do PC pareado.")
    static var authenticationPolicy: IntentAuthenticationPolicy = .alwaysAllowed

    func perform() async throws -> some IntentResult {
        try await FechoQuick.lock()
        return .result()
    }
}

struct SleepPCIntent: AppIntent {
    static var title: LocalizedStringResource = "Suspender o PC"
    static var description = IntentDescription("Coloca o PC pareado em suspensão.")
    static var authenticationPolicy: IntentAuthenticationPolicy = .alwaysAllowed

    func perform() async throws -> some IntentResult {
        try await FechoQuick.sleepPC()
        return .result()
    }
}

struct PlayPauseIntent: AppIntent {
    static var title: LocalizedStringResource = "Reproduzir ou pausar no PC"
    static var authenticationPolicy: IntentAuthenticationPolicy = .alwaysAllowed

    func perform() async throws -> some IntentResult {
        try await FechoQuick.media("playpause")
        return .result()
    }
}

struct NextTrackIntent: AppIntent {
    static var title: LocalizedStringResource = "Próxima faixa no PC"
    static var authenticationPolicy: IntentAuthenticationPolicy = .alwaysAllowed

    func perform() async throws -> some IntentResult {
        try await FechoQuick.media("next")
        return .result()
    }
}

/// Desbloqueio: abre o app, onde o Face ID confirma.
struct OpenUnlockIntent: AppIntent {
    static var title: LocalizedStringResource = "Desbloquear o PC"
    static var description = IntentDescription("Abre o Fecho para desbloquear o PC com Face ID.")
    static var openAppWhenRun = true

    func perform() async throws -> some IntentResult {
        return .result()
    }
}
