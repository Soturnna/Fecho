import CryptoKit
import Foundation

/// Aceita SOMENTE o certificado cuja impressão digital veio no QR code.
/// Autoridades certificadoras são ignoradas de propósito: nenhuma delas
/// conhece o certificado próprio do PC, e nenhuma deve poder se passar por ele.
final class PinningDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    private let expected: String
    nonisolated(unsafe) static var lastMismatch = false

    init(fingerprint: String) {
        expected = fingerprint.lowercased()
    }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge)
        async -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        evaluate(challenge)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge)
        async -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        evaluate(challenge)
    }

    private func evaluate(_ challenge: URLAuthenticationChallenge) -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust,
              let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = chain.first else {
            return (.cancelAuthenticationChallenge, nil)
        }
        let der = SecCertificateCopyData(leaf) as Data
        let actual = SHA256.hash(data: der).map { String(format: "%02x", $0) }.joined()
        guard actual == expected else {
            Self.lastMismatch = true
            return (.cancelAuthenticationChallenge, nil)
        }
        Self.lastMismatch = false
        return (.useCredential, URLCredential(trust: trust))
    }
}
