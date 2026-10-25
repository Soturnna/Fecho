import SwiftUI

struct MediaView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        NavigationStack {
            List {
                Section {
                    HStack(spacing: 30) {
                        Spacer()
                        mediaButton("backward.fill", label: "Faixa anterior", action: "prev")
                        Button {
                            Task { await model.media("playpause") }
                        } label: {
                            Image(systemName: "playpause.fill")
                                .font(.title)
                                .frame(width: 64, height: 64)
                                .foregroundStyle(Color(.systemBackground))
                                .background(Color.primary, in: .circle)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("Reproduzir ou pausar")
                        mediaButton("forward.fill", label: "Próxima faixa", action: "next")
                        Spacer()
                    }
                    .padding(.vertical, 12)
                } footer: {
                    Text("Controla o player que estiver tocando no PC (Spotify, YouTube, VLC…).")
                }

                Section("Volume do PC") {
                    HStack(spacing: 8) {
                        Button {
                            Task { await model.setMuted(!model.muted) }
                        } label: {
                            Image(systemName: model.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                                .foregroundStyle(model.muted ? Color.red : Color.primary)
                                .frame(width: 44, height: 44)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(model.muted ? "Ativar som" : "Silenciar")

                        Slider(value: $model.volume, in: 0...100, step: 1, onEditingChanged: { editing in
                            if !editing { Task { await model.setVolume(model.volume) } }
                        })
                        .accessibilityLabel("Volume do PC")

                        Text(model.muted ? "Mudo" : "\(Int(model.volume))%")
                            .font(.subheadline)
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                            .frame(width: 48, alignment: .trailing)
                    }
                }

                if !model.apps.isEmpty {
                    Section("Por aplicativo") {
                        ForEach($model.apps) { $app in
                            VStack(alignment: .leading, spacing: 2) {
                                HStack {
                                    Text(app.name)
                                    Spacer()
                                    Text("\(Int(app.volume))%").font(.subheadline).monospacedDigit().foregroundStyle(.secondary)
                                }
                                Slider(value: $app.volume, in: 0...100, step: 1, onEditingChanged: { editing in
                                    if !editing { Task { await model.setAppVolume(app, app.volume) } }
                                })
                                .accessibilityLabel("Volume de \(app.name)")
                            }
                            .padding(.vertical, 4)
                        }
                    }
                }
            }
            .navigationTitle("Mídia")
            .disabled(!model.isConnected)
            .refreshable { await model.refreshVolume() }
            .task(id: model.isConnected) { if model.isConnected { await model.refreshVolume() } }
        }
    }

    private func mediaButton(_ symbol: String, label: String, action: String) -> some View {
        Button {
            Task { await model.media(action) }
        } label: {
            Image(systemName: symbol).font(.title2).frame(width: 52, height: 52)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }
}
