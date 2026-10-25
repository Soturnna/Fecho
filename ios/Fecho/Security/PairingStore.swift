import Foundation
import Security

/// Dados de um PC pareado. O token do QR NÃO é guardado: ele só serve uma vez.
struct PairingInfo: Codable, Equatable {
    var host: String
    var port: Int
    /// SHA-256 do certificado do PC, em hexadecimal minúsculo.
    var fingerprint: String
    var agentId: String
    var pcName: String
    var deviceId: String
}

/// Conteúdo validado de um QR de pareamento.
struct PairingOffer: Equatable {
    var host: String
    var port: Int
    var fingerprint: String
    var token: String
    var agentId: String
    var pcName: String

    /// Lê `fecho://parear?...` e recusa qualquer campo fora do formato.
    init?(qr: String) {
        guard qr.count < 600,
              let comps = URLComponents(string: qr),
              comps.scheme == "fecho", comps.host == "parear" else { return nil }
        var q: [String: String] = [:]
        for item in comps.queryItems ?? [] { q[item.name] = item.value }

        guard let host = q["h"], Self.isPrivateIPv4(host),
              let portText = q["p"], let port = Int(portText), (1...65535).contains(port),
              let fp = q["f"], fp.count == 64, fp.allSatisfy({ $0.isHexDigit && !$0.isUppercase }),
              let token = q["t"], (40...64).contains(token.count),
              token.allSatisfy({ $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" }),
              let agent = q["id"], (8...64).contains(agent.count), agent.allSatisfy({ $0.isHexDigit })
        else { return nil }

        self.host = host
        self.port = port
        self.fingerprint = fp
        self.token = token
        self.agentId = agent
        let name = (q["n"] ?? "Meu PC").filter { !$0.isNewline }
        self.pcName = name.isEmpty ? "Meu PC" : String(name.prefix(40))
    }

    /// Só aceitamos IPs da rede local no QR (10/8, 172.16/12, 192.168/16, 169.254/16).
    static func isPrivateIPv4(_ text: String) -> Bool {
        let parts = text.split(separator: ".").compactMap { UInt8($0) }
        guard parts.count == 4, text.split(separator: ".").count == 4 else { return false }
        switch (parts[0], parts[1]) {
        case (10, _): return true
        case (172, 16...31): return true
        case (192, 168): return true
        case (169, 254): return true
        default: return false
        }
    }
}

/// Guarda o pareamento no Keychain, só neste aparelho (sem iCloud, sem backup).
enum PairingStore {
    private static let service = "com.soturnna.fecho.pairing"
    private static let account = "pc"

    static func load() -> PairingInfo? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else { return nil }
        return try? JSONDecoder().decode(PairingInfo.self, from: data)
    }

    static func save(_ info: PairingInfo) throws {
        let data = try JSONEncoder().encode(info)
        delete()
        let attributes: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
            kSecValueData as String: data,
        ]
        let status = SecItemAdd(attributes as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status))
        }
    }

    static func delete() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
