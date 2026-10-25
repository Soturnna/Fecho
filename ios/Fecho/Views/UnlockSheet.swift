import SwiftUI

/// Mostra as etapas do desbloqueio, como no design aprovado.
struct UnlockSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 18) {
            Image(systemName: icon)
                .font(.system(size: 48))
                .foregroundStyle(iconColor)
                .frame(width: 104, height: 104)
                .background(iconColor.opacity(0.14), in: .circle)
                .padding(.top, 24)
                .accessibilityHidden(true)

            VStack(spacing: 6) {
                Text(title).font(.title2.bold()).multilineTextAlignment(.center)
                Text("A senha do Windows fica guardada no PC. O iPhone envia apenas um pedido assinado.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }

            if case .failed(let message) = model.unlockStep {
                Text(message)
                    .font(.subheadline)
                    .padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color(.tertiarySystemGroupedBackground), in: .rect(cornerRadius: 18))
            } else {
                VStack(spacing: 0) {
                    StepRow(text: "Identidade confirmada", state: state(for: 0))
                    Divider().padding(.leading, 54)
                    StepRow(text: "Pedido assinado e enviado", state: state(for: 1))
                    Divider().padding(.leading, 54)
                    StepRow(text: "Windows liberando a sessão", state: state(for: 2))
                }
                .background(Color(.tertiarySystemGroupedBackground), in: .rect(cornerRadius: 20))
            }

            Spacer(minLength: 0)

            Button {
                dismiss()
            } label: {
                Text(model.unlockStep == .done ? "Concluir" : "Fechar")
                    .font(.headline)
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .buttonBorderShape(.capsule)
            .controlSize(.large)
        }
        .padding(20)
        .onChange(of: model.unlockStep) { _, step in
            if step == .done {
                Task {
                    try? await Task.sleep(for: .seconds(1.2))
                    dismiss()
                }
            }
        }
        .sensoryFeedback(.success, trigger: model.unlockStep == .done)
    }

    private var title: String {
        switch model.unlockStep {
        case .done: "\(model.pcName) desbloqueado"
        case .failed: "Não foi possível desbloquear"
        default: "Desbloqueando \(model.pcName)"
        }
    }

    private var icon: String {
        switch model.unlockStep {
        case .done: "lock.open.fill"
        case .failed: "exclamationmark.triangle.fill"
        default: "faceid"
        }
    }

    private var iconColor: Color {
        if case .failed = model.unlockStep { return .orange }
        return .green
    }

    private func state(for index: Int) -> StepRow.State {
        let current: Int = switch model.unlockStep {
        case .idle, .authenticating: 0
        case .sending: 1
        case .waitingPC: 2
        case .done: 3
        case .failed: -1
        }
        if index < current { return .done }
        if index == current { return .running }
        return .pending
    }
}

struct StepRow: View {
    enum State { case pending, running, done }
    let text: String
    let state: State

    var body: some View {
        HStack(spacing: 12) {
            Group {
                switch state {
                case .done:
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                case .running:
                    ProgressView()
                case .pending:
                    Image(systemName: "circle").foregroundStyle(.tertiary)
                }
            }
            .font(.title3)
            .frame(width: 26)
            Text(text)
                .foregroundStyle(state == .pending ? .secondary : .primary)
            Spacer()
        }
        .padding(.horizontal, 16)
        .frame(minHeight: 52)
        .accessibilityElement(children: .combine)
        .accessibilityValue(state == .done ? "concluído" : state == .running ? "em andamento" : "pendente")
    }
}
