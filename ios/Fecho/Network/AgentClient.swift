import CryptoKit
import Foundation
import LocalAuthentication

/// Erros que o app mostra. Espelham ErrorCodes do agente.
enum AgentError: LocalizedError, Equatable {
    case notPaired
    case notConnected
    case certificateMismatch
    case refused
    case invalid
    case unavailable
    case failed
    case rateLimited
    case timeout
    case cancelled

    init(code: String?) {
        switch code {
        case "recusado": self = .refused
        case "invalido": self = .invalid
        case "indisponivel": self = .unavailable
        case "devagar": self = .rateLimited
        default: self = .failed
        }
    }

    var errorDescription: String? {
        switch self {
        case .notPaired: "Nenhum PC pareado."
        case .notConnected: "Sem conexão com o PC."
        case .certificateMismatch: "O PC apresentou um certificado diferente do pareado. Conexão recusada por segurança."
        case .refused: "O PC recusou o pedido."
        case .invalid: "Pedido em formato inválido."
        case .unavailable: "Recurso ainda não disponível neste PC."
        case .failed: "O PC não conseguiu executar o pedido."
        case .rateLimited: "Muitos pedidos seguidos. Aguarde um instante."
        case .timeout: "O PC demorou para responder."
        case .cancelled: "Cancelado."
        }
    }
}

/// Como o pedido é autenticado (docs/SEGURANCA.md, seções 4 e 5).
enum Auth {
    /// Assinado no Secure Enclave com a chave de controle.
    case control
    /// Assinado com a chave de desbloqueio, depois do Face ID.
    case unlock(reason: String)
    /// HMAC com a chave da sessão (eventos rápidos).
    case session
}

/// Dicionário JSON que atravessa a fronteira do actor.
struct JSONObject: @unchecked Sendable {
    var value: [String: Any]
    init(_ value: [String: Any] = [:]) { self.value = value }
}

/// Fala com o agente do PC por WebSocket sobre TLS com certificado fixado.
actor AgentClient {
    static let shared = AgentClient()

    private var info: PairingInfo?
    private var session: URLSession?
    private var task: URLSessionWebSocketTask?
    private var pending: [String: CheckedContinuation<JSONObject, Error>] = [:]
    private var sessionKey: SymmetricKey?
    private var seq: Int64 = 0
    private var eventHandler: (@Sendable (String, JSONObject) -> Void)?
    private var disconnectHandler: (@Sendable () -> Void)?

    // MARK: - Conexão

    func configure(_ info: PairingInfo?) {
        if info != self.info { disconnect() }
        self.info = info
    }

    func onEvent(_ handler: @escaping @Sendable (String, JSONObject) -> Void) { eventHandler = handler }
    func onDisconnect(_ handler: @escaping @Sendable () -> Void) { disconnectHandler = handler }

    var isConnected: Bool { task?.state == .running }

    func connect() throws {
        guard let info else { throw AgentError.notPaired }
        try open(host: info.host, port: info.port, fingerprint: info.fingerprint)
    }

    private func open(host: String, port: Int, fingerprint: String) throws {
        if task?.state == .running { return }
        disconnect()
        guard let url = URL(string: "wss://\(host):\(port)/ws") else { throw AgentError.notConnected }
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 10
        config.waitsForConnectivity = false
        let session = URLSession(configuration: config, delegate: PinningDelegate(fingerprint: fingerprint), delegateQueue: nil)
        let task = session.webSocketTask(with: url)
        task.maximumMessageSize = 256 * 1024
        self.session = session
        self.task = task
        task.resume()
        receiveNext(on: task)
    }

    func disconnect() {
        task?.cancel(with: .goingAway, reason: nil)
        task = nil
        session?.invalidateAndCancel()
        session = nil
        sessionKey = nil
        seq = 0
        let waiting = pending
        pending.removeAll()
        for (_, c) in waiting { c.resume(throwing: AgentError.notConnected) }
    }

    // MARK: - Pareamento

    /// Gera as duas chaves no Secure Enclave e registra o iPhone no PC.
    func pair(with offer: PairingOffer, deviceName: String) async throws -> PairingInfo {
        disconnect()
        let deviceId = UUID().uuidString
        let controlKey = try KeyStore.createKey(.control)
        let unlockKey = try KeyStore.createKey(.unlock)

        try open(host: offer.host, port: offer.port, fingerprint: offer.fingerprint)
        let args: [String: Any] = [
            "token": offer.token,
            "name": String(deviceName.prefix(40)),
            "controlKey": controlKey.base64EncodedString(),
            "unlockKey": unlockKey.base64EncodedString(),
        ]
        let newInfo = PairingInfo(host: offer.host, port: offer.port, fingerprint: offer.fingerprint,
                                  agentId: offer.agentId, pcName: offer.pcName, deviceId: deviceId)
        do {
            _ = try await send("pair", args: args, auth: .control, as: newInfo)
        } catch {
            KeyStore.deleteKey(.control)
            KeyStore.deleteKey(.unlock)
            disconnect()
            throw error
        }
        info = newInfo
        return newInfo
    }

    // MARK: - Comandos

    @discardableResult
    func request(_ cmd: String, _ args: [String: Any] = [:], auth: Auth = .control) async throws -> JSONObject {
        guard let info else { throw AgentError.notPaired }
        if task?.state != .running { try connect() }
        if case .session = auth, sessionKey == nil { try await openSession() }
        return try await send(cmd, args: args, auth: auth, as: info)
    }

    /// Para eventos rápidos (mouse): envia sem esperar a resposta.
    func fire(_ cmd: String, _ args: [String: Any]) async {
        guard let info, task?.state == .running else { return }
        if sessionKey == nil { try? await openSession() }
        guard let key = sessionKey else { return }
        seq += 1
        guard let (payload, _) = try? makePayload(cmd, args: args, info: info, seq: seq) else { return }
        let mac = Data(HMAC<SHA256>.authenticationCode(for: payload, using: key))
        let envelope: [String: Any] = ["id": "", "payload": payload.base64EncodedString(), "mac": mac.base64EncodedString()]
        if let data = try? JSONSerialization.data(withJSONObject: envelope), let text = String(data: data, encoding: .utf8) {
            try? await task?.send(.string(text))
        }
    }

    private func openSession() async throws {
        guard let info else { throw AgentError.notPaired }
        let reply = try await send("session.open", args: [:], auth: .control, as: info)
        guard let keyText = (reply.value["result"] as? [String: Any])?["key"] as? String,
              let keyData = Data(base64Encoded: keyText), keyData.count == 32 else { throw AgentError.failed }
        sessionKey = SymmetricKey(data: keyData)
        seq = 0
    }

    private func send(_ cmd: String, args: [String: Any], auth: Auth, as info: PairingInfo) async throws -> JSONObject {
        guard let task, task.state == .running else { throw AgentError.notConnected }

        let id = UUID().uuidString
        var envelope: [String: Any] = ["id": id]
        switch auth {
        case .control:
            let (payload, _) = try makePayload(cmd, args: args, info: info, seq: nil)
            envelope["payload"] = payload.base64EncodedString()
            envelope["sig"] = try KeyStore.sign(payload, role: .control).base64EncodedString()
        case .unlock(let reason):
            // O Face ID acontece aqui; o horário do pedido é gerado depois dele.
            let context = try await KeyStore.authenticate(reason: reason)
            let (payload, _) = try makePayload(cmd, args: args, info: info, seq: nil)
            envelope["payload"] = payload.base64EncodedString()
            envelope["sig"] = try KeyStore.sign(payload, role: .unlock, context: context).base64EncodedString()
            context.invalidate()
        case .session:
            guard let key = sessionKey else { throw AgentError.notConnected }
            seq += 1
            let (payload, _) = try makePayload(cmd, args: args, info: info, seq: seq)
            envelope["payload"] = payload.base64EncodedString()
            envelope["mac"] = Data(HMAC<SHA256>.authenticationCode(for: payload, using: key)).base64EncodedString()
        }

        let data = try JSONSerialization.data(withJSONObject: envelope)
        guard let text = String(data: data, encoding: .utf8) else { throw AgentError.invalid }

        let reply: JSONObject = try await withCheckedThrowingContinuation { continuation in
            pending[id] = continuation
            Task {
                do {
                    try await task.send(.string(text))
                } catch {
                    await self.fail(id, with: Self.map(error))
                }
            }
            Task {
                try? await Task.sleep(for: .seconds(10))
                await self.fail(id, with: AgentError.timeout)
            }
        }

        if reply.value["ok"] as? Bool == true { return reply }
        throw AgentError(code: reply.value["error"] as? String)
    }

    private func fail(_ id: String, with error: Error) {
        pending.removeValue(forKey: id)?.resume(throwing: error)
    }

    /// Monta o conteúdo assinado. A assinatura cobre exatamente estes bytes.
    private func makePayload(_ cmd: String, args: [String: Any], info: PairingInfo, seq: Int64?) throws -> (Data, String) {
        var nonce = Data(count: 16)
        let status = nonce.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        guard status == errSecSuccess else { throw AgentError.failed }
        var payload: [String: Any] = [
            "v": 1,
            "device": info.deviceId,
            "agent": info.agentId,
            "cmd": cmd,
            "args": args,
            "ts": Int64(Date().timeIntervalSince1970 * 1000),
            "nonce": nonce.base64EncodedString(),
        ]
        if let seq { payload["seq"] = seq }
        return (try JSONSerialization.data(withJSONObject: payload), nonce.base64EncodedString())
    }

    // MARK: - Recepção

    private func receiveNext(on task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            guard let self else { return }
            Task { await self.handle(result, from: task) }
        }
    }

    private func handle(_ result: Result<URLSessionWebSocketTask.Message, Error>, from source: URLSessionWebSocketTask) {
        guard source === task else { return }
        switch result {
        case .failure(let error):
            let mapped = Self.map(error)
            let waiting = pending
            pending.removeAll()
            for (_, c) in waiting { c.resume(throwing: mapped) }
            task = nil
            sessionKey = nil
            disconnectHandler?()
        case .success(let message):
            var data: Data?
            switch message {
            case .string(let text): data = text.data(using: .utf8)
            case .data(let d): data = d
            @unknown default: data = nil
            }
            if let data, let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                if let event = obj["event"] as? String {
                    eventHandler?(event, JSONObject(obj["data"] as? [String: Any] ?? [:]))
                } else if let id = obj["id"] as? String, !id.isEmpty {
                    pending.removeValue(forKey: id)?.resume(returning: JSONObject(obj))
                }
            }
            receiveNext(on: source)
        }
    }

    private static func map(_ error: Error) -> AgentError {
        if let agent = error as? AgentError { return agent }
        let ns = error as NSError
        if ns.domain == NSURLErrorDomain, ns.code == NSURLErrorCancelled || ns.code == NSURLErrorServerCertificateUntrusted
            || ns.code == NSURLErrorSecureConnectionFailed {
            return PinningDelegate.lastMismatch ? .certificateMismatch : .notConnected
        }
        return .notConnected
    }
}
