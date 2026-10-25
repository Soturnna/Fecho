import Foundation
import WatchConnectivity

/// No iPhone: recebe os comandos do Apple Watch e os executa no PC.
/// O relógio é outro dispositivo e não tem o pareamento; por isso ele pede
/// ao iPhone, que fala com o PC usando a chave de controle (sem Face ID).
final class WatchBridge: NSObject, WCSessionDelegate {
    static let shared = WatchBridge()

    func activate() {
        guard WCSession.isSupported() else { return }
        let session = WCSession.default
        session.delegate = self
        session.activate()
    }

    private func run(_ message: [String: Any]) {
        guard let cmd = message["cmd"] as? String else { return }
        var args: [String: Any] = [:]
        if let action = message["action"] as? String { args["action"] = action }
        Task { try? await FechoQuick.send(cmd, args) }
    }

    func session(_ session: WCSession, didReceiveMessage message: [String: Any]) {
        run(message)
    }

    func session(_ session: WCSession, didReceiveMessage message: [String: Any],
                 replyHandler: @escaping ([String: Any]) -> Void) {
        guard let cmd = message["cmd"] as? String else { replyHandler(["ok": false]); return }
        var args: [String: Any] = [:]
        if let action = message["action"] as? String { args["action"] = action }
        Task {
            do { try await FechoQuick.send(cmd, args); replyHandler(["ok": true]) }
            catch { replyHandler(["ok": false]) }
        }
    }

    func session(_ session: WCSession, didReceiveUserInfo userInfo: [String: Any]) {
        run(userInfo)
    }

    func session(_ session: WCSession, activationDidCompleteWith activationState: WCSessionActivationState, error: Error?) {}
    func sessionDidBecomeInactive(_ session: WCSession) {}
    func sessionDidDeactivate(_ session: WCSession) { session.activate() }
}
