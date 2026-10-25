import AVFoundation
import SwiftUI

struct PairingView: View {
    @Environment(AppModel.self) private var model
    @State private var scanning = true
    @State private var working = false
    @State private var error: String?
    @State private var manual = false
    @State private var manualText = ""

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Conecte ao seu PC").font(.title.bold())
                        Text("No PC, clique no ícone do Fecho na bandeja e escolha Parear iPhone. Depois, aponte a câmera para o código.")
                            .foregroundStyle(.secondary)
                    }

                    ZStack {
                        RoundedRectangle(cornerRadius: 32).fill(Color(.secondarySystemGroupedBackground))
                        if scanning && !working {
                            QRScanner { code in handle(code) }
                                .clipShape(.rect(cornerRadius: 32))
                        }
                        if working { ProgressView("Pareando…") }
                        ScannerFrame().allowsHitTesting(false)
                    }
                    .frame(height: 300)
                    .accessibilityLabel("Visor da câmera para ler o QR code")

                    if let error {
                        Label(error, systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                            .font(.subheadline)
                    }

                    HStack(alignment: .top, spacing: 12) {
                        Image(systemName: "checkmark.shield.fill").foregroundStyle(.green).font(.title3)
                        Text("O Fecho usa a rede local só para falar com o seu PC. Nada passa pela internet. O iOS vai pedir essa permissão a seguir.")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    .padding(14)
                    .background(Color(.secondarySystemGroupedBackground), in: .rect(cornerRadius: 20))

                    Button("Colar o código manualmente") { manual = true }
                        .buttonStyle(.bordered)
                        .buttonBorderShape(.capsule)
                        .controlSize(.large)
                        .frame(maxWidth: .infinity)
                }
                .padding(20)
            }
            .background(Color(.systemGroupedBackground))
            .alert("Código de pareamento", isPresented: $manual) {
                TextField("fecho://parear?…", text: $manualText)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                Button("Cancelar", role: .cancel) { manualText = "" }
                Button("Parear") { handle(manualText); manualText = "" }
            } message: {
                Text("Cole o conteúdo do QR code exibido no PC.")
            }
        }
    }

    private func handle(_ code: String) {
        guard !working else { return }
        guard let offer = PairingOffer(qr: code.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            error = "Este QR code não é um código de pareamento do Fecho."
            return
        }
        working = true
        error = nil
        Task {
            do {
                try await model.pair(with: offer)
            } catch {
                self.error = (error as? LocalizedError)?.errorDescription ?? "Não foi possível parear."
                working = false
                scanning = true
            }
        }
    }
}

struct ScannerFrame: View {
    var body: some View {
        CornerBrackets()
            .stroke(Color.green, style: StrokeStyle(lineWidth: 4, lineCap: .round, lineJoin: .round))
            .frame(width: 196, height: 196)
    }
}

/// Quatro cantos de mira, como no design aprovado.
struct CornerBrackets: Shape {
    func path(in r: CGRect) -> Path {
        let l: CGFloat = 40, c: CGFloat = 20
        var p = Path()
        // superior esquerdo
        p.move(to: CGPoint(x: r.minX, y: r.minY + l))
        p.addLine(to: CGPoint(x: r.minX, y: r.minY + c))
        p.addQuadCurve(to: CGPoint(x: r.minX + c, y: r.minY), control: CGPoint(x: r.minX, y: r.minY))
        p.addLine(to: CGPoint(x: r.minX + l, y: r.minY))
        // superior direito
        p.move(to: CGPoint(x: r.maxX - l, y: r.minY))
        p.addLine(to: CGPoint(x: r.maxX - c, y: r.minY))
        p.addQuadCurve(to: CGPoint(x: r.maxX, y: r.minY + c), control: CGPoint(x: r.maxX, y: r.minY))
        p.addLine(to: CGPoint(x: r.maxX, y: r.minY + l))
        // inferior direito
        p.move(to: CGPoint(x: r.maxX, y: r.maxY - l))
        p.addLine(to: CGPoint(x: r.maxX, y: r.maxY - c))
        p.addQuadCurve(to: CGPoint(x: r.maxX - c, y: r.maxY), control: CGPoint(x: r.maxX, y: r.maxY))
        p.addLine(to: CGPoint(x: r.maxX - l, y: r.maxY))
        // inferior esquerdo
        p.move(to: CGPoint(x: r.minX + l, y: r.maxY))
        p.addLine(to: CGPoint(x: r.minX + c, y: r.maxY))
        p.addQuadCurve(to: CGPoint(x: r.minX, y: r.maxY - c), control: CGPoint(x: r.minX, y: r.maxY))
        p.addLine(to: CGPoint(x: r.minX, y: r.maxY - l))
        return p
    }
}

/// Leitor de QR code com a câmera traseira (AVFoundation).
struct QRScanner: UIViewControllerRepresentable {
    let onCode: (String) -> Void

    func makeUIViewController(context: Context) -> ScannerController {
        let vc = ScannerController()
        vc.onCode = onCode
        return vc
    }

    func updateUIViewController(_ uiViewController: ScannerController, context: Context) {}
}

final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    var onCode: ((String) -> Void)?
    private let session = AVCaptureSession()
    private var preview: AVCaptureVideoPreviewLayer?
    private var lastCode: String?

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
            guard granted else { return }
            DispatchQueue.main.async { self?.configure() }
        }
    }

    private func configure() {
        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device),
              session.canAddInput(input) else { return }
        session.addInput(input)

        let output = AVCaptureMetadataOutput()
        guard session.canAddOutput(output) else { return }
        session.addOutput(output)
        output.setMetadataObjectsDelegate(self, queue: .main)
        output.metadataObjectTypes = [.qr]

        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspectFill
        layer.frame = view.bounds
        view.layer.addSublayer(layer)
        preview = layer

        let session = self.session
        DispatchQueue.global(qos: .userInitiated).async { session.startRunning() }
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        preview?.frame = view.bounds
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        let session = self.session
        DispatchQueue.global(qos: .userInitiated).async { session.stopRunning() }
    }

    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject],
                        from connection: AVCaptureConnection) {
        guard let obj = metadataObjects.first as? AVMetadataMachineReadableCodeObject,
              let value = obj.stringValue, value != lastCode else { return }
        lastCode = value
        UINotificationFeedbackGenerator().notificationOccurred(.success)
        onCode?(value)
    }
}
