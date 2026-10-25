import AppIntents
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @AppStorage("confirmPower") private var confirmPower = true
    @State private var confirmForget = false

    var body: some View {
        NavigationStack {
            List {
                Section {
                    LabeledContent("Face ID para desbloquear, desligar e reiniciar") {
                        Text("Sempre").foregroundStyle(.secondary)
                    }
                    Toggle("Confirmar antes de desligar", isOn: $confirmPower)
                } header: {
                    Text("Segurança")
                } footer: {
                    Text("O Face ID não pode ser desligado: ele protege a chave que autoriza o desbloqueio, e essa chave nunca sai do chip de segurança do iPhone.")
                }

                Section {
                    Label("Bloquear ao me afastar", systemImage: "dot.radiowaves.left.and.right")
                        .foregroundStyle(.secondary)
                } header: {
                    Text("Proximidade")
                } footer: {
                    Text("O bloqueio automático por Bluetooth chega em uma próxima versão.")
                }

                Section {
                    SiriTipView(intent: LockPCIntent())
                    NavigationLink("Ver todos os atalhos") { ShortcutsList() }
                } header: {
                    Text("Siri e Atalhos")
                }

                Section("PC pareado") {
                    LabeledContent(model.pcName) {
                        Text(model.isConnected ? "Conectado" : "Sem conexão")
                            .foregroundStyle(model.isConnected ? Color.green : Color.secondary)
                    }
                    if let info = model.pairing {
                        LabeledContent("Endereço", value: "\(info.host):\(info.port)")
                        LabeledContent("Certificado") {
                            Text(String(info.fingerprint.prefix(16)) + "…")
                                .font(.caption.monospaced())
                                .foregroundStyle(.secondary)
                        }
                    }
                    Button("Remover pareamento", role: .destructive) { confirmForget = true }
                }

                Section {
                    LabeledContent("Versão", value: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—")
                }
            }
            .navigationTitle("Ajustes")
            .confirmationDialog("Remover o pareamento com \(model.pcName)?", isPresented: $confirmForget, titleVisibility: .visible) {
                Button("Remover", role: .destructive) { Task { await model.forgetPairing() } }
            } message: {
                Text("As chaves deste iPhone serão apagadas. Remova também o aparelho no menu do Fecho no PC.")
            }
        }
    }
}

struct ShortcutsList: View {
    var body: some View {
        List {
            Section {
                row("Bloquear o PC", "“Bloquear o PC no Fecho”")
                row("Suspender o PC", "“Suspender o PC no Fecho”")
                row("Reproduzir ou pausar", "“Pausar a música no Fecho”")
                row("Próxima faixa", "“Próxima faixa no Fecho”")
            } footer: {
                Text("Desbloquear, desligar e reiniciar não ficam na Siri de propósito: exigem o Face ID dentro do app.")
            }
        }
        .navigationTitle("Atalhos")
    }

    private func row(_ title: String, _ phrase: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
            Text(phrase).font(.subheadline).foregroundStyle(.secondary)
        }
    }
}
