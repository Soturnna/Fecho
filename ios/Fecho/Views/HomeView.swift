import SwiftUI

struct HomeView: View {
    @Environment(AppModel.self) private var model
    @AppStorage("confirmPower") private var confirmPower = true
    @State private var showUnlock = false
    @State private var confirmShutdown = false
    @State private var confirmRestart = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    if case .offline(let reason) = model.connection {
                        OfflineCard(reason: reason)
                    } else {
                        statusCard
                    }
                    powerSection
                    activitySection
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 24)
            }
            .background(Color(.systemGroupedBackground))
            .navigationTitle("Início")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) { ConnectionBadge() }
            }
            .refreshable { await model.connect() }
            .sheet(isPresented: $showUnlock) {
                UnlockSheet()
                    .presentationDetents([.medium, .large])
                    .presentationDragIndicator(.visible)
            }
            .alert("Desligar \(model.pcName)?", isPresented: $confirmShutdown) {
                Button("Cancelar", role: .cancel) {}
                Button("Desligar", role: .destructive) { Task { await model.shutdown() } }
            } message: {
                Text("Os programas abertos serão fechados. Trabalhos não salvos podem ser perdidos.")
            }
            .alert("Reiniciar \(model.pcName)?", isPresented: $confirmRestart) {
                Button("Cancelar", role: .cancel) {}
                Button("Reiniciar", role: .destructive) { Task { await model.restart() } }
            } message: {
                Text("Os programas abertos serão fechados.")
            }
        }
    }

    private var statusCard: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 14) {
                Image(systemName: "desktopcomputer")
                    .font(.title2)
                    .frame(width: 52, height: 52)
                    .background(Color(.tertiarySystemGroupedBackground), in: .rect(cornerRadius: 14))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 1) {
                    Text(model.pcName).font(.title3.weight(.semibold))
                    Text("Windows · rede local").font(.subheadline).foregroundStyle(.secondary)
                        .lineLimit(1).minimumScaleFactor(0.85)
                }
                Spacer(minLength: 8)
                LockBadge(locked: model.pcLocked)
            }

            if model.pcLocked == false {
                Button {
                    Task { await model.lock() }
                } label: {
                    Label("Bloquear agora", systemImage: "lock.fill")
                        .font(.headline)
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
                .buttonBorderShape(.capsule)
                .controlSize(.large)
                .tint(.gray)
            } else {
                Button {
                    model.unlockStep = .idle
                    showUnlock = true
                    Task { await model.unlock() }
                } label: {
                    Label("Desbloquear com Face ID", systemImage: "faceid")
                        .font(.headline)
                        .foregroundStyle(.black)
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .buttonBorderShape(.capsule)
                .controlSize(.large)
                .disabled(!model.isConnected)
            }

            Label("Bloqueio por proximidade: em breve", systemImage: "dot.radiowaves.left.and.right")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .padding(20)
        .background(Color(.secondarySystemGroupedBackground), in: .rect(cornerRadius: 26))
    }

    private var powerSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionHeader("Energia")
            HStack(spacing: 10) {
                PowerTile(title: "Suspender", symbol: "moon.fill", color: .indigo) {
                    Task { await model.sleep() }
                }
                PowerTile(title: "Reiniciar", symbol: "arrow.clockwise", color: .orange) {
                    if confirmPower { confirmRestart = true } else { Task { await model.restart() } }
                }
                PowerTile(title: "Desligar", symbol: "power", color: .red) {
                    if confirmPower { confirmShutdown = true } else { Task { await model.shutdown() } }
                }
                PowerTile(title: "Bloquear", symbol: "lock.fill", color: .yellow) {
                    Task { await model.lock() }
                }
            }
            .disabled(!model.isConnected)
        }
    }

    @ViewBuilder
    private var activitySection: some View {
        if !model.activity.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                SectionHeader("Atividade recente")
                VStack(spacing: 0) {
                    ForEach(Array(model.activity.prefix(5).enumerated()), id: \.element.id) { index, item in
                        if index > 0 { Divider().padding(.leading, 48) }
                        HStack(spacing: 12) {
                            Image(systemName: item.symbol)
                                .foregroundStyle(.green)
                                .frame(width: 22)
                                .accessibilityHidden(true)
                            Text(item.title)
                            Spacer()
                            Text(item.date, format: .relative(presentation: .named))
                                .font(.subheadline)
                                .foregroundStyle(.secondary)
                        }
                        .padding(.horizontal, 16)
                        .frame(minHeight: 52)
                    }
                }
                .background(Color(.secondarySystemGroupedBackground), in: .rect(cornerRadius: 20))
            }
        }
    }
}

struct SectionHeader: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.footnote)
            .textCase(.uppercase)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 16)
            .accessibilityAddTraits(.isHeader)
    }
}

struct PowerTile: View {
    let title: String
    let symbol: String
    let color: Color
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 8) {
                Image(systemName: symbol)
                    .font(.title3)
                    .foregroundStyle(color)
                Text(title)
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            .frame(maxWidth: .infinity, minHeight: 84)
            .background(Color(.secondarySystemGroupedBackground), in: .rect(cornerRadius: 20))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title)
    }
}

struct LockBadge: View {
    let locked: Bool?

    var body: some View {
        let (text, symbol, color): (String, String, Color) = switch locked {
        case .some(true): ("Bloqueado", "lock.fill", .yellow)
        case .some(false): ("Desbloqueado", "lock.open.fill", .green)
        case .none: ("—", "questionmark", .secondary)
        }
        Label(text, systemImage: symbol)
            .font(.footnote.weight(.semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background(color.opacity(0.16), in: .capsule)
    }
}

struct ConnectionBadge: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        switch model.connection {
        case .connected:
            Label("Conectado", systemImage: "wifi").labelStyle(.iconOnly).foregroundStyle(.green)
                .accessibilityLabel("Conectado ao PC")
        case .connecting:
            ProgressView().accessibilityLabel("Conectando")
        case .offline, .unpaired:
            Label("Sem conexão", systemImage: "wifi.slash").labelStyle(.iconOnly).foregroundStyle(.red)
                .accessibilityLabel("Sem conexão com o PC")
        }
    }
}

struct OfflineCard: View {
    @Environment(AppModel.self) private var model
    let reason: String

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "wifi.slash")
                .font(.title)
                .foregroundStyle(.red)
                .frame(width: 72, height: 72)
                .background(.red.opacity(0.16), in: .circle)
                .accessibilityHidden(true)
            VStack(spacing: 4) {
                Text("Não encontrei \(model.pcName)").font(.title2.bold())
                Text(reason).font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
            }
            VStack(alignment: .leading, spacing: 10) {
                Label("O PC está ligado e não está hibernando", systemImage: "1.circle.fill")
                Label("iPhone e PC estão na mesma rede Wi-Fi", systemImage: "2.circle.fill")
                Label("O Fecho aparece na bandeja do Windows", systemImage: "3.circle.fill")
            }
            .font(.subheadline)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(14)
            .background(Color(.tertiarySystemGroupedBackground), in: .rect(cornerRadius: 18))

            Button {
                Task { await model.connect() }
            } label: {
                Text("Tentar de novo").font(.headline).foregroundStyle(.black).frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .buttonBorderShape(.capsule)
            .controlSize(.large)
        }
        .padding(20)
        .background(Color(.secondarySystemGroupedBackground), in: .rect(cornerRadius: 26))
    }
}
