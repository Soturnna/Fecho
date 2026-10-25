import Foundation

/// Ações rápidas usadas por App Intents, widgets e controles. Todas usam a
/// chave de controle (sem Face ID). O desbloqueio, que exige Face ID, não
/// passa por aqui — ele abre o app.
enum FechoQuick {
    static func send(_ cmd: String, _ args: [String: Any] = [:]) async throws {
        guard let info = PairingStore.load() else { throw AgentError.notPaired }
        let client = AgentClient.shared
        await client.configure(info)
        try await client.request(cmd, args)
    }

    static func lock() async throws { try await send("lock") }
    static func sleepPC() async throws { try await send("sleep") }
    static func media(_ action: String) async throws { try await send("media", ["action": action]) }

    static var isPaired: Bool { PairingStore.load() != nil }
    static var pcName: String { PairingStore.load()?.pcName ?? "Meu PC" }
}
