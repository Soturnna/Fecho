import SwiftUI

@main
struct FechoApp: App {
    @State private var model = AppModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
                .tint(.green)
                .task {
                    WatchBridge.shared.activate()
                    await model.start()
                }
                .onChange(of: scenePhase) { _, phase in
                    if phase == .active, model.pairing != nil, !model.isConnected {
                        Task { await model.connect() }
                    }
                }
        }
    }
}

struct RootView: View {
    @Environment(AppModel.self) private var model
    @State private var tab = AppTab.home

    var body: some View {
        if model.pairing == nil {
            PairingView()
        } else {
            TabView(selection: $tab) {
                Tab("Início", systemImage: "house.fill", value: AppTab.home) { HomeView() }
                Tab("Tela", systemImage: "display", value: AppTab.screen) { ScreenControlView() }
                Tab("Mídia", systemImage: "speaker.wave.2.fill", value: AppTab.media) { MediaView() }
                Tab("Arquivos", systemImage: "folder.fill", value: AppTab.files) { FilesView() }
                Tab("Ajustes", systemImage: "gearshape.fill", value: AppTab.settings) { SettingsView() }
            }
            .tabViewBottomAccessory {
                // Escondido na aba Tela para não atrapalhar o toque sobre a tela do PC.
                if tab != .screen { MiniPlayer() }
            }
            .overlay(alignment: .top) { ToastView() }
        }
    }
}

enum AppTab: Hashable {
    case home, screen, media, files, settings
}

/// Acessório da barra de abas: controle rápido da mídia do PC.
struct MiniPlayer: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "music.note")
                .font(.body)
                .foregroundStyle(.secondary)
                .frame(width: 32, height: 32)
                .background(.quaternary, in: .rect(cornerRadius: 8))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 0) {
                Text("Mídia do PC").font(.subheadline.weight(.semibold))
                Text(model.pcName).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button { Task { await model.media("playpause") } } label: {
                Image(systemName: "playpause.fill").frame(width: 44, height: 44)
            }
            .accessibilityLabel("Reproduzir ou pausar")
            Button { Task { await model.media("next") } } label: {
                Image(systemName: "forward.fill").frame(width: 44, height: 44)
            }
            .accessibilityLabel("Próxima faixa")
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 8)
        .disabled(!model.isConnected)
    }
}

/// Aviso curto no topo; some sozinho, mas também fecha com um toque.
struct ToastView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let message = model.toast {
            Text(message)
                .font(.subheadline)
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                .glassEffect(.regular, in: .capsule)
                .padding(.top, 8)
                .onTapGesture { model.toast = nil }
                .task(id: message) {
                    try? await Task.sleep(for: .seconds(4))
                    if model.toast == message { model.toast = nil }
                }
                .accessibilityAddTraits(.isStaticText)
        }
    }
}
