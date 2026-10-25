import SwiftUI

struct ControlView: View {
    @Environment(AppModel.self) private var model
    @State private var mode = Mode.trackpad
    @State private var text = ""
    @State private var ctrl = false
    @State private var alt = false
    @State private var shift = false

    enum Mode: String, CaseIterable, Identifiable {
        case trackpad = "Trackpad"
        case keys = "Teclas"
        var id: String { rawValue }
    }

    private var mods: [String] {
        var m: [String] = []
        if ctrl { m.append("ctrl") }
        if alt { m.append("alt") }
        if shift { m.append("shift") }
        return m
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 14) {
                    Picker("Modo", selection: $mode) {
                        ForEach(Mode.allCases) { Text($0.rawValue).tag($0) }
                    }
                    .pickerStyle(.segmented)

                    if mode == .trackpad {
                        Trackpad()
                        HStack(spacing: 10) {
                            pill("Clique esquerdo") { Task { await model.click("left") } }
                            pill("Clique direito") { Task { await model.click("right") } }
                        }
                    } else {
                        KeyGrid(mods: mods)
                    }

                    modifierRow
                    textField
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 24)
            }
            .background(Color(.systemGroupedBackground))
            .navigationTitle("Controle")
            .disabled(!model.isConnected || model.pcLocked == true)
            .overlay {
                if model.pcLocked == true {
                    ContentUnavailableView("PC bloqueado", systemImage: "lock.fill",
                                           description: Text("Desbloqueie o PC para usar mouse e teclado."))
                }
            }
        }
    }

    private var modifierRow: some View {
        HStack(spacing: 8) {
            ModifierToggle(title: "Ctrl", isOn: $ctrl)
            ModifierToggle(title: "Alt", isOn: $alt)
            ModifierToggle(title: "Shift", isOn: $shift)
            keyButton("Esc", key: "esc")
            keyButton("Tab", key: "tab")
            Button {
                Task { await model.key("win", mods: mods) }
            } label: {
                Image(systemName: "squareshape.split.2x2").frame(maxWidth: .infinity, minHeight: 44)
            }
            .buttonStyle(.bordered)
            .accessibilityLabel("Tecla Windows")
        }
    }

    private var textField: some View {
        HStack(spacing: 8) {
            TextField("Digite para enviar ao PC", text: $text)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.send)
                .onSubmit(send)
                .padding(.horizontal, 18)
                .frame(height: 48)
                .background(Color(.secondarySystemGroupedBackground), in: .capsule)
            Button(action: send) {
                Image(systemName: "arrow.up").font(.headline).foregroundStyle(.black).frame(width: 48, height: 48)
            }
            .background(Color.green, in: .circle)
            .accessibilityLabel("Enviar texto")
            .disabled(text.isEmpty)
        }
    }

    private func send() {
        let value = text
        text = ""
        Task { await model.sendText(value) }
    }

    private func keyButton(_ title: String, key: String) -> some View {
        Button {
            Task { await model.key(key, mods: mods) }
        } label: {
            Text(title).font(.subheadline.weight(.semibold)).frame(maxWidth: .infinity, minHeight: 44)
        }
        .buttonStyle(.bordered)
    }

    private func pill(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).frame(maxWidth: .infinity, minHeight: 34)
        }
        .buttonStyle(.bordered)
        .buttonBorderShape(.capsule)
        .controlSize(.large)
    }
}

struct ModifierToggle: View {
    let title: String
    @Binding var isOn: Bool

    var body: some View {
        Toggle(title, isOn: $isOn)
            .toggleStyle(.button)
            .font(.subheadline.weight(.semibold))
            .frame(maxWidth: .infinity, minHeight: 44)
    }
}

/// Área de toque que vira mouse. Faixa à direita rola a página.
struct Trackpad: View {
    @Environment(AppModel.self) private var model
    @State private var last: CGPoint?
    @State private var moved: CGFloat = 0
    @State private var pendingDX: CGFloat = 0
    @State private var pendingDY: CGFloat = 0
    @State private var lastSent = Date.distantPast
    @State private var scrollLast: CGFloat?

    private let sensitivity: CGFloat = 1.6

    var body: some View {
        HStack(spacing: 8) {
            RoundedRectangle(cornerRadius: 26)
                .fill(Color(.secondarySystemGroupedBackground))
                .overlay {
                    VStack(spacing: 10) {
                        Image(systemName: "hand.point.up.left").font(.largeTitle).foregroundStyle(.tertiary)
                        Text("Arraste para mover o cursor\nToque para clicar")
                            .font(.subheadline)
                            .multilineTextAlignment(.center)
                            .foregroundStyle(.secondary)
                    }
                    .allowsHitTesting(false)
                }
                .gesture(moveGesture)
                .accessibilityElement()
                .accessibilityLabel("Trackpad")
                .accessibilityHint("Arraste para mover o cursor do PC. Toque para clicar.")
                .accessibilityAction { Task { await model.click("left") } }

            RoundedRectangle(cornerRadius: 18)
                .fill(Color(.secondarySystemGroupedBackground))
                .frame(width: 44)
                .overlay {
                    Image(systemName: "arrow.up.and.down").foregroundStyle(.tertiary).allowsHitTesting(false)
                }
                .gesture(scrollGesture)
                .accessibilityLabel("Rolagem")
                .accessibilityAdjustableAction { direction in
                    model.mouseScroll(direction == .increment ? 3 : -3)
                }
        }
        .frame(height: 290)
    }

    private var moveGesture: some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                if let last {
                    pendingDX += (value.location.x - last.x) * sensitivity
                    pendingDY += (value.location.y - last.y) * sensitivity
                    moved += abs(value.location.x - last.x) + abs(value.location.y - last.y)
                }
                last = value.location
                flush(force: false)
            }
            .onEnded { _ in
                flush(force: true)
                if moved < 6 { Task { await model.click("left") } }
                last = nil
                moved = 0
            }
    }

    private var scrollGesture: some Gesture {
        DragGesture(minimumDistance: 4)
            .onChanged { value in
                let y = value.location.y
                if let prev = scrollLast, abs(y - prev) >= 18 {
                    model.mouseScroll(y < prev ? 1 : -1)
                    scrollLast = y
                } else if scrollLast == nil {
                    scrollLast = y
                }
            }
            .onEnded { _ in scrollLast = nil }
    }

    /// Junta os movimentos em pacotes de ~60 por segundo.
    private func flush(force: Bool) {
        let now = Date()
        guard force || now.timeIntervalSince(lastSent) >= 1.0 / 60 else { return }
        let dx = Int(pendingDX.rounded()), dy = Int(pendingDY.rounded())
        guard dx != 0 || dy != 0 else { return }
        model.mouseMove(dx: max(-2000, min(2000, dx)), dy: max(-2000, min(2000, dy)))
        pendingDX -= CGFloat(dx)
        pendingDY -= CGFloat(dy)
        lastSent = now
    }
}

/// Teclas de navegação e atalhos comuns.
struct KeyGrid: View {
    @Environment(AppModel.self) private var model
    let mods: [String]

    private let shortcuts: [(String, String, [String])] = [
        ("Copiar", "c", ["ctrl"]), ("Colar", "v", ["ctrl"]), ("Desfazer", "z", ["ctrl"]),
        ("Trocar janela", "tab", ["alt"]), ("Área de trabalho", "d", ["win"]), ("Fechar", "f4", ["alt"]),
    ]

    var body: some View {
        VStack(spacing: 10) {
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 3), spacing: 8) {
                ForEach(shortcuts, id: \.0) { s in
                    Button {
                        Task { await model.key(s.1, mods: s.2) }
                    } label: {
                        Text(s.0).font(.subheadline.weight(.medium)).lineLimit(1).minimumScaleFactor(0.8)
                            .frame(maxWidth: .infinity, minHeight: 44)
                    }
                    .buttonStyle(.bordered)
                }
            }
            Grid(horizontalSpacing: 8, verticalSpacing: 8) {
                GridRow {
                    Color.clear.frame(height: 44)
                    arrow("arrow.up", key: "up", label: "Seta para cima")
                    Color.clear.frame(height: 44)
                }
                GridRow {
                    arrow("arrow.left", key: "left", label: "Seta para a esquerda")
                    arrow("arrow.down", key: "down", label: "Seta para baixo")
                    arrow("arrow.right", key: "right", label: "Seta para a direita")
                }
            }
            HStack(spacing: 8) {
                arrow("return", key: "enter", label: "Enter")
                arrow("delete.left", key: "backspace", label: "Apagar")
                arrow("space", key: "space", label: "Espaço")
            }
        }
        .padding(14)
        .background(Color(.secondarySystemGroupedBackground), in: .rect(cornerRadius: 26))
    }

    private func arrow(_ symbol: String, key: String, label: String) -> some View {
        Button {
            Task { await model.key(key, mods: mods) }
        } label: {
            Image(systemName: symbol).frame(maxWidth: .infinity, minHeight: 44)
        }
        .buttonStyle(.bordered)
        .accessibilityLabel(label)
    }
}
