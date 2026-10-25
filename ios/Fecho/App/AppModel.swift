import Foundation
import Observation
import UIKit

struct AppVolume: Identifiable, Equatable {
    var id: String
    var name: String
    var volume: Double
    var muted: Bool
}

struct ActivityItem: Identifiable, Equatable {
    var id: Int64 { ts }
    var ts: Int64
    var cmd: String

    var title: String {
        switch cmd {
        case "unlock": "Desbloqueado com Face ID"
        case "lock": "Bloqueado pelo iPhone"
        case "autolock": "Bloqueado ao se afastar"
        case "sleep": "Suspenso"
        case "restart": "Reiniciado"
        case "shutdown": "Desligado"
        case "pair": "iPhone pareado"
        case "file.received": "Arquivo enviado ao PC"
        default: cmd
        }
    }

    var symbol: String {
        switch cmd {
        case "unlock": "faceid"
        case "lock": "lock.fill"
        case "autolock": "dot.radiowaves.left.and.right"
        case "sleep": "moon.fill"
        case "restart": "arrow.clockwise"
        case "shutdown": "power"
        case "file.received": "arrow.up.doc.fill"
        default: "iphone"
        }
    }

    var date: Date { Date(timeIntervalSince1970: TimeInterval(ts) / 1000) }
}

enum ConnectionState: Equatable {
    case unpaired
    case connecting
    case connected
    case offline(String)
}

enum UnlockStep: Equatable {
    case idle
    case authenticating
    case sending
    case waitingPC
    case done
    case failed(String)
}

/// Estado do app e ações que a interface dispara.
@MainActor
@Observable
final class AppModel {
    var pairing: PairingInfo? = PairingStore.load()
    var connection: ConnectionState = .unpaired
    var pcLocked: Bool?
    var unlockAvailable = false
    var unlockStep: UnlockStep = .idle
    var activity: [ActivityItem] = []

    var volume: Double = 50
    var muted = false
    var apps: [AppVolume] = []

    var clipboardText: String?
    var toast: String?

    var uploads: [UploadItem] = []
    var filesGrantedUntil: Date?

    let client = AgentClient.shared

    init() {
        if pairing != nil { connection = .connecting }
    }

    var pcName: String { pairing?.pcName ?? "Meu PC" }
    var isConnected: Bool { connection == .connected }

    // MARK: - Conexão

    func start() async {
        await client.configure(pairing)
        await client.onEvent { [weak self] event, data in
            Task { @MainActor in self?.handleEvent(event, data.value) }
        }
        await client.onDisconnect { [weak self] in
            Task { @MainActor in self?.connection = .offline("A conexão caiu.") }
        }
        await connect()
    }

    func connect() async {
        guard pairing != nil else { connection = .unpaired; return }
        connection = .connecting
        do {
            let reply = try await client.request("hello")
            applyStatus(reply.value["result"] as? [String: Any])
            connection = .connected
            await refreshActivity()
        } catch {
            connection = .offline(error.localizedDescription)
        }
    }

    private func handleEvent(_ event: String, _ data: [String: Any]) {
        if event == "status", let locked = data["locked"] as? Bool {
            pcLocked = locked
            Task { await refreshActivity() }
        }
    }

    private func applyStatus(_ status: [String: Any]?) {
        guard let status else { return }
        pcLocked = status["locked"] as? Bool
        unlockAvailable = status["unlockAvailable"] as? Bool ?? false
    }

    // MARK: - Pareamento

    func pair(with offer: PairingOffer) async throws {
        let info = try await client.pair(with: offer, deviceName: UIDevice.current.name)
        try PairingStore.save(info)
        pairing = info
        await connect()
    }

    func forgetPairing() async {
        await client.disconnect()
        await client.configure(nil)
        KeyStore.deleteKey(.control)
        KeyStore.deleteKey(.unlock)
        PairingStore.delete()
        pairing = nil
        connection = .unpaired
        activity = []
        pcLocked = nil
    }

    // MARK: - Energia

    func lock() async { await run("lock") { self.pcLocked = true } }
    func sleep() async { await run("sleep") }

    func restart() async {
        await run("restart", auth: .unlock(reason: "Confirme para reiniciar \(pcName)"))
    }

    func shutdown() async {
        await run("shutdown", auth: .unlock(reason: "Confirme para desligar \(pcName)"))
    }

    func unlock() async {
        unlockStep = .authenticating
        do {
            unlockStep = .sending
            try await client.request("unlock", auth: .unlock(reason: "Desbloquear \(pcName)"))
            unlockStep = .done
            pcLocked = false
            await refreshActivity()
        } catch AgentError.unavailable {
            unlockStep = .failed("O PC recebeu e validou o pedido, mas o componente que entra na tela de login do Windows ainda não foi instalado. Ele é a próxima etapa do projeto.")
        } catch KeyStoreError.authenticationCancelled {
            unlockStep = .idle
        } catch {
            unlockStep = .failed(error.localizedDescription)
        }
    }

    // MARK: - Áudio e mídia

    func refreshVolume() async {
        guard let reply = try? await client.request("volume.get", auth: .session),
              let r = reply.value["result"] as? [String: Any] else { return }
        volume = Double(r["volume"] as? Int ?? 0)
        muted = r["muted"] as? Bool ?? false
        apps = (r["apps"] as? [[String: Any]] ?? []).compactMap { a in
            guard let id = a["id"] as? String, let name = a["name"] as? String else { return nil }
            return AppVolume(id: id, name: name, volume: Double(a["volume"] as? Int ?? 0), muted: a["muted"] as? Bool ?? false)
        }
    }

    func setVolume(_ value: Double) async {
        await run("volume.set", ["value": Int(value.rounded())], auth: .session, quiet: true)
    }

    func setMuted(_ value: Bool) async {
        muted = value
        await run("volume.mute", ["muted": value], auth: .session, quiet: true)
    }

    func setAppVolume(_ app: AppVolume, _ value: Double) async {
        await run("volume.app", ["id": app.id, "value": Int(value.rounded())], auth: .session, quiet: true)
    }

    func media(_ action: String) async {
        await run("media", ["action": action], auth: .session, quiet: true)
    }

    // MARK: - Mouse e teclado

    func mouseMove(dx: Int, dy: Int) {
        Task { await client.fire("mouse.move", ["dx": dx, "dy": dy]) }
    }

    func mouseScroll(_ amount: Int) {
        Task { await client.fire("mouse.scroll", ["amount": amount]) }
    }

    func click(_ button: String) async {
        await run("mouse.click", ["button": button], auth: .session, quiet: true)
    }

    func key(_ key: String, mods: [String] = []) async {
        await run("key", ["key": key, "mods": mods], auth: .session, quiet: true)
    }

    func sendText(_ text: String) async {
        let trimmed = String(text.prefix(1000))
        guard !trimmed.isEmpty else { return }
        await run("text", ["text": trimmed], auth: .session)
    }

    // MARK: - Área de transferência

    func fetchClipboard() async {
        do {
            let reply = try await client.request("clipboard.get")
            clipboardText = (reply.value["result"] as? [String: Any])?["text"] as? String ?? ""
        } catch {
            toast = error.localizedDescription
        }
    }

    func sendClipboardToPC() async {
        guard let text = UIPasteboard.general.string, !text.isEmpty else {
            toast = "A área de transferência do iPhone está vazia."
            return
        }
        await run("clipboard.set", ["text": String(text.prefix(64 * 1024))]) {
            self.toast = "Texto colado na área de transferência do PC."
        }
    }

    func copyPCClipboardToPhone() {
        guard let text = clipboardText, !text.isEmpty else { return }
        UIPasteboard.general.string = text
        toast = "Copiado no iPhone."
    }

    // MARK: - Atividade

    func refreshActivity() async {
        guard let reply = try? await client.request("activity"),
              let list = reply.value["result"] as? [[String: Any]] else { return }
        activity = list.compactMap { e in
            guard let ts = (e["ts"] as? NSNumber)?.int64Value, let cmd = e["cmd"] as? String else { return nil }
            return ActivityItem(ts: ts, cmd: cmd)
        }
    }

    // MARK: - Utilitário

    private func run(_ cmd: String, _ args: [String: Any] = [:], auth: Auth = .control, quiet: Bool = false,
                     then: (() -> Void)? = nil) async {
        do {
            try await client.request(cmd, args, auth: auth)
            then?()
            if !quiet { await refreshActivity() }
        } catch KeyStoreError.authenticationCancelled {
            return
        } catch AgentError.notConnected {
            connection = .offline(AgentError.notConnected.localizedDescription)
        } catch {
            toast = error.localizedDescription
        }
    }
}
