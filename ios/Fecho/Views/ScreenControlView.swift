import SwiftUI

/// Aba "Tela": vê a tela do PC ao vivo e controla por toque. Também traz o
/// trackpad sem vídeo (mais leve) e o teclado.
struct ScreenControlView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @State private var mode = Mode.video
    @State private var dragMode = false
    @State private var last = CGPoint(x: 0.5, y: 0.5)

    enum Mode: String, CaseIterable, Identifiable {
        case video = "Vídeo"
        case trackpad = "Trackpad"
        case keyboard = "Teclado"
        var id: String { rawValue }
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 12) {
                Picker("Modo", selection: $mode) {
                    ForEach(Mode.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .padding(.horizontal, 16)
                .padding(.top, 8)

                switch mode {
                case .video: videoMode
                case .trackpad: trackpadMode
                case .keyboard: KeyboardPanel()
                }
            }
            .background(Color(.systemGroupedBackground))
            .navigationTitle("Tela")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .topBarTrailing) { ConnectionBadge() } }
        }
        .disabled(!model.isConnected)
        .onChange(of: mode) { _, new in
            Task { if new == .video { await model.startScreen() } else { await model.stopScreen() } }
        }
        .onChange(of: scenePhase) { _, phase in
            if phase != .active, model.streamingScreen { Task { await model.stopScreen() } }
            if phase == .active, mode == .video { Task { await model.startScreen() } }
        }
        .task { if mode == .video { await model.startScreen() } }
        .onDisappear { Task { await model.stopScreen() } }
    }

    // MARK: - Vídeo + toque

    private var videoMode: some View {
        VStack(spacing: 10) {
            ScreenCanvas(dragMode: dragMode, last: $last)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .overlay {
                    if model.pcLocked == true {
                        ContentUnavailableView("PC bloqueado", systemImage: "lock.fill",
                                               description: Text("Desbloqueie o PC para ver e controlar a tela."))
                            .background(.ultraThinMaterial)
                    }
                }

            HStack(spacing: 10) {
                Button {
                    model.pointerClick(last.x, last.y, right: true)
                } label: {
                    Label("Clique dir.", systemImage: "cursorarrow.click.2")
                        .lineLimit(1).minimumScaleFactor(0.8).frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.bordered)

                Toggle(isOn: $dragMode) {
                    Label("Arrastar", systemImage: "hand.draw")
                        .lineLimit(1).minimumScaleFactor(0.8).frame(maxWidth: .infinity, minHeight: 44)
                }
                .toggleStyle(.button)

                Button {
                    mode = .keyboard
                } label: {
                    Label("Teclado", systemImage: "keyboard")
                        .lineLimit(1).minimumScaleFactor(0.8).frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.bordered)
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 8)
        }
    }

    // MARK: - Trackpad sem vídeo

    private var trackpadMode: some View {
        ScrollView {
            VStack(spacing: 14) {
                Trackpad()
                HStack(spacing: 10) {
                    Button { Task { await model.click("left") } } label: {
                        Text("Clique esquerdo").frame(maxWidth: .infinity, minHeight: 34)
                    }
                    Button { Task { await model.click("right") } } label: {
                        Text("Clique direito").frame(maxWidth: .infinity, minHeight: 34)
                    }
                }
                .buttonStyle(.bordered)
                .buttonBorderShape(.capsule)
                .controlSize(.large)
                Text("Sem vídeo: gasta menos rede. Toque para clicar, arraste para mover.")
                    .font(.footnote).foregroundStyle(.secondary)
            }
            .padding(16)
        }
    }
}

/// A tela do PC. Mapeia o toque para a posição absoluta do cursor.
struct ScreenCanvas: View {
    @Environment(AppModel.self) private var model
    let dragMode: Bool
    @Binding var last: CGPoint

    @State private var started = false
    @State private var moved: CGFloat = 0
    @State private var lastSent = Date.distantPast
    @State private var scrollAnchor: CGFloat?

    var body: some View {
        GeometryReader { geo in
            ZStack {
                Color.black
                if let image = model.screenImage {
                    Image(uiImage: image)
                        .resizable()
                        .interpolation(.medium)
                        .aspectRatio(contentMode: .fit)
                } else {
                    VStack(spacing: 10) {
                        ProgressView().tint(.white)
                        Text("Recebendo a tela do PC…").font(.subheadline).foregroundStyle(.white.opacity(0.7))
                    }
                }
            }
            .contentShape(Rectangle())
            .gesture(pointer(in: geo.size))
            .overlay(alignment: .trailing) { scrollStrip }
            .clipShape(.rect(cornerRadius: 20))
        }
        .padding(.horizontal, 16)
        .accessibilityElement()
        .accessibilityLabel("Tela do PC")
        .accessibilityHint("Arraste para mover o cursor, toque para clicar.")
    }

    private func pointer(in size: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                let n = normalize(value.location, in: size)
                last = n
                if !started {
                    started = true
                    moved = 0
                    if dragMode { model.pointerDown(n.x, n.y) }
                }
                moved = max(moved, hypot(value.translation.width, value.translation.height))
                let now = Date()
                if now.timeIntervalSince(lastSent) >= 1.0 / 60 {
                    model.pointerMove(n.x, n.y)
                    lastSent = now
                }
            }
            .onEnded { value in
                let n = normalize(value.location, in: size)
                if dragMode {
                    model.pointerMove(n.x, n.y)
                    model.pointerUp(n.x, n.y)
                } else if moved < 10 {
                    model.pointerClick(n.x, n.y)
                }
                started = false
            }
    }

    private var scrollStrip: some View {
        Rectangle()
            .fill(.white.opacity(0.06))
            .frame(width: 36)
            .overlay {
                Image(systemName: "arrow.up.and.down").foregroundStyle(.white.opacity(0.35))
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 4)
                    .onChanged { value in
                        let y = value.location.y
                        if let prev = scrollAnchor, abs(y - prev) >= 16 {
                            model.mouseScroll(y < prev ? 1 : -1)
                            scrollAnchor = y
                        } else if scrollAnchor == nil {
                            scrollAnchor = y
                        }
                    }
                    .onEnded { _ in scrollAnchor = nil }
            )
            .accessibilityLabel("Rolagem")
            .accessibilityAdjustableAction { model.mouseScroll($0 == .increment ? 3 : -3) }
    }

    /// Converte o ponto do toque para 0..1 dentro da imagem (descontando as bordas pretas).
    private func normalize(_ point: CGPoint, in size: CGSize) -> CGPoint {
        let rect = imageRect(in: size)
        guard rect.width > 0, rect.height > 0 else { return CGPoint(x: 0.5, y: 0.5) }
        let nx = (point.x - rect.minX) / rect.width
        let ny = (point.y - rect.minY) / rect.height
        return CGPoint(x: min(max(nx, 0), 1), y: min(max(ny, 0), 1))
    }

    private func imageRect(in size: CGSize) -> CGRect {
        let aspect = model.screenAspect
        guard aspect > 0, size.width > 0, size.height > 0 else { return .zero }
        var w = size.width
        var h = size.width / aspect
        if h > size.height {
            h = size.height
            w = size.height * aspect
        }
        return CGRect(x: (size.width - w) / 2, y: (size.height - h) / 2, width: w, height: h)
    }
}

/// Teclado completo: teclas especiais, atalhos e campo de texto.
struct KeyboardPanel: View {
    @Environment(AppModel.self) private var model
    @State private var text = ""
    @State private var ctrl = false
    @State private var alt = false
    @State private var shift = false
    @FocusState private var typing: Bool

    private var mods: [String] {
        var m: [String] = []
        if ctrl { m.append("ctrl") }
        if alt { m.append("alt") }
        if shift { m.append("shift") }
        return m
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                HStack(spacing: 8) {
                    ModifierToggle(title: "Ctrl", isOn: $ctrl)
                    ModifierToggle(title: "Alt", isOn: $alt)
                    ModifierToggle(title: "Shift", isOn: $shift)
                }
                KeyGrid(mods: mods)
                HStack(spacing: 8) {
                    TextField("Digite para enviar ao PC", text: $text)
                        .focused($typing)
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
            .padding(16)
        }
        .scrollDismissesKeyboard(.interactively)
        .toolbar {
            ToolbarItemGroup(placement: .keyboard) {
                Spacer()
                Button("OK") { typing = false }.fontWeight(.semibold)
            }
        }
    }

    private func send() {
        let value = text
        text = ""
        Task { await model.sendText(value) }
    }
}
