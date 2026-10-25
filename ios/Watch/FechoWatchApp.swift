import SwiftUI
import WatchConnectivity

@main
struct FechoWatchApp: App {
    @WKApplicationDelegateAdaptor private var delegate: WatchDelegate

    var body: some Scene {
        WindowGroup {
            WatchContentView()
        }
    }
}

final class WatchDelegate: NSObject, WKApplicationDelegate {
    func applicationDidFinishLaunching() {
        WatchConnector.shared.activate()
    }
}

/// No Apple Watch: manda os comandos ao iPhone, que fala com o PC.
/// Desbloquear exige Face ID, que o relógio não tem, então fica no iPhone.
final class WatchConnector: NSObject, ObservableObject, WCSessionDelegate {
    static let shared = WatchConnector()

    @Published var reachable = false

    func activate() {
        guard WCSession.isSupported() else { return }
        let session = WCSession.default
        session.delegate = self
        session.activate()
    }

    func send(cmd: String, action: String? = nil) {
        guard WCSession.isSupported() else { return }
        var message: [String: Any] = ["cmd": cmd]
        if let action { message["action"] = action }
        let session = WCSession.default
        if session.isReachable {
            session.sendMessage(message, replyHandler: nil, errorHandler: { _ in
                session.transferUserInfo(message)
            })
        } else {
            session.transferUserInfo(message)
        }
    }

    func session(_ session: WCSession, activationDidCompleteWith activationState: WCSessionActivationState, error: Error?) {
        DispatchQueue.main.async { self.reachable = session.isReachable }
    }

    func sessionReachabilityDidChange(_ session: WCSession) {
        DispatchQueue.main.async { self.reachable = session.isReachable }
    }
}

struct WatchContentView: View {
    @StateObject private var wc = WatchConnector.shared
    @State private var sent: String?

    var body: some View {
        ScrollView {
            VStack(spacing: 10) {
                Text("Meu PC").font(.headline)

                WatchButton(title: "Bloquear", symbol: "lock.fill", tint: .yellow) {
                    wc.send(cmd: "lock"); flash("Bloqueando…")
                }
                HStack(spacing: 8) {
                    WatchButton(title: nil, symbol: "playpause.fill", tint: .white) {
                        wc.send(cmd: "media", action: "playpause"); flash("Mídia")
                    }
                    WatchButton(title: nil, symbol: "forward.fill", tint: .white) {
                        wc.send(cmd: "media", action: "next"); flash("Próxima")
                    }
                }
                WatchButton(title: "Suspender", symbol: "moon.fill", tint: .indigo) {
                    wc.send(cmd: "sleep"); flash("Suspendendo…")
                }

                if let sent {
                    Text(sent).font(.footnote).foregroundStyle(.secondary)
                }
                Text(wc.reachable ? "iPhone conectado" : "Abra o Fecho no iPhone")
                    .font(.caption2)
                    .foregroundStyle(wc.reachable ? .green : .secondary)
            }
            .padding(.horizontal, 4)
        }
    }

    private func flash(_ text: String) {
        sent = text
        Task { try? await Task.sleep(for: .seconds(2)); if sent == text { sent = nil } }
    }
}

struct WatchButton: View {
    let title: String?
    let symbol: String
    let tint: Color
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack {
                Image(systemName: symbol).foregroundStyle(tint)
                if let title { Text(title) }
            }
            .frame(maxWidth: .infinity, minHeight: 36)
        }
        .buttonStyle(.bordered)
    }
}
