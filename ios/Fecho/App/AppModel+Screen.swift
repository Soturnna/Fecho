import UIKit

/// Ver a tela do PC e controlar por toque (docs/SEGURANCA.md, seção 10).
extension AppModel {
    /// Largura máxima do quadro pedida ao PC. Menor = mais fluido; maior = mais nítido.
    static let screenWidth = 960
    static let screenQuality = 50

    func startScreen() async {
        guard isConnected else { return }
        streamingScreen = true
        do {
            try await client.startScreen(maxWidth: Self.screenWidth, quality: Self.screenQuality)
        } catch {
            streamingScreen = false
            if !(error is CancellationError) { toast = error.localizedDescription }
        }
    }

    func stopScreen() async {
        streamingScreen = false
        await client.stopScreen()
    }

    func applyFrame(_ data: Data) {
        guard let image = UIImage(data: data) else { return }
        screenImage = image
        if image.size.height > 0 { screenAspect = image.size.width / image.size.height }
    }

    // Controle por toque. Tudo via sessão (HMAC), para ser rápido.

    func pointerMove(_ x: Double, _ y: Double) {
        Task { await client.fire("mouse.moveto", ["x": x, "y": y]) }
    }

    func pointerClick(_ x: Double, _ y: Double, right: Bool = false) {
        Task { await client.fire("mouse.clickat", ["x": x, "y": y, "button": right ? "right" : "left"]) }
    }

    func pointerDown(_ x: Double, _ y: Double) {
        Task { await client.fire("mouse.down", ["x": x, "y": y, "button": "left"]) }
    }

    func pointerUp(_ x: Double, _ y: Double) {
        Task { await client.fire("mouse.up", ["x": x, "y": y, "button": "left"]) }
    }
}
