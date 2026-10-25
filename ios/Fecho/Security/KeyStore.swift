import Foundation
import LocalAuthentication
import Security

/// As duas chaves do aparelho (docs/SEGURANCA.md, seção 4).
enum KeyRole: String {
    /// Volume, mídia, mouse, teclado, bloquear. Exige só o iPhone desbloqueado.
    case control
    /// Desbloquear, desligar, reiniciar. Exige Face ID a cada uso.
    case unlock
}

enum KeyStoreError: LocalizedError {
    case secureEnclaveUnavailable
    case creationFailed(String)
    case notFound
    case signingFailed(String)
    case authenticationCancelled

    var errorDescription: String? {
        switch self {
        case .secureEnclaveUnavailable: "Este aparelho não tem Secure Enclave disponível."
        case .creationFailed(let m): "Não foi possível criar a chave: \(m)"
        case .notFound: "Chave não encontrada. Pareie o iPhone de novo."
        case .signingFailed(let m): "Falha ao assinar: \(m)"
        case .authenticationCancelled: "Face ID cancelado."
        }
    }
}

/// Chaves geradas DENTRO do Secure Enclave. A chave privada nunca sai do chip:
/// não pode ser lida, exportada nem entrar em backup.
enum KeyStore {
    private static func tag(_ role: KeyRole) -> Data {
        Data("com.soturnna.fecho.key.\(role.rawValue)".utf8)
    }

    /// Cria (ou recria) a chave e devolve a chave pública no formato X9.63.
    static func createKey(_ role: KeyRole) throws -> Data {
        deleteKey(role)

        var flags: SecAccessControlCreateFlags = [.privateKeyUsage]
        if role == .unlock {
            // Se alguém cadastrar outro rosto, a chave é invalidada.
            flags.insert(.biometryCurrentSet)
        }
        var error: Unmanaged<CFError>?
        guard let access = SecAccessControlCreateWithFlags(
            kCFAllocatorDefault, kSecAttrAccessibleWhenUnlockedThisDeviceOnly, flags, &error
        ) else {
            throw KeyStoreError.creationFailed(error?.takeRetainedValue().localizedDescription ?? "acesso")
        }

        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256,
            kSecAttrTokenID as String: kSecAttrTokenIDSecureEnclave,
            kSecPrivateKeyAttrs as String: [
                kSecAttrIsPermanent as String: true,
                kSecAttrApplicationTag as String: tag(role),
                kSecAttrAccessControl as String: access,
            ] as [String: Any],
        ]

        guard let privateKey = SecKeyCreateRandomKey(attributes as CFDictionary, &error) else {
            let message = error?.takeRetainedValue().localizedDescription ?? "desconhecido"
            throw KeyStoreError.creationFailed(message)
        }
        return try publicKeyData(of: privateKey)
    }

    static func deleteKey(_ role: KeyRole) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrApplicationTag as String: tag(role),
            kSecAttrTokenID as String: kSecAttrTokenIDSecureEnclave,
        ]
        SecItemDelete(query as CFDictionary)
    }

    /// Assina os bytes com a chave pedida. Para a chave de desbloqueio, o
    /// contexto já precisa ter passado pelo Face ID (veja `authenticate`).
    static func sign(_ data: Data, role: KeyRole, context: LAContext? = nil) throws -> Data {
        var query: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrApplicationTag as String: tag(role),
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecReturnRef as String: true,
        ]
        if let context {
            query[kSecUseAuthenticationContext as String] = context
        }

        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess, let found = item else { throw KeyStoreError.notFound }
        // swiftlint:disable:next force_cast
        let key = found as! SecKey

        var error: Unmanaged<CFError>?
        guard let signature = SecKeyCreateSignature(
            key, .ecdsaSignatureMessageX962SHA256, data as CFData, &error
        ) as Data? else {
            let cfError = error?.takeRetainedValue()
            if let cfError, CFErrorGetCode(cfError) == Int(errSecUserCanceled) {
                throw KeyStoreError.authenticationCancelled
            }
            throw KeyStoreError.signingFailed(cfError?.localizedDescription ?? "desconhecido")
        }
        return signature
    }

    /// Pede o Face ID uma vez e devolve o contexto aprovado, usado logo em
    /// seguida para assinar com a chave de desbloqueio.
    static func authenticate(reason: String) async throws -> LAContext {
        let context = LAContext()
        context.localizedCancelTitle = "Cancelar"
        context.touchIDAuthenticationAllowableReuseDuration = 0
        do {
            let ok = try await context.evaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, localizedReason: reason)
            guard ok else { throw KeyStoreError.authenticationCancelled }
        } catch let error as LAError where error.code == .userCancel || error.code == .appCancel || error.code == .systemCancel {
            throw KeyStoreError.authenticationCancelled
        }
        return context
    }

    private static func publicKeyData(of privateKey: SecKey) throws -> Data {
        guard let publicKey = SecKeyCopyPublicKey(privateKey) else {
            throw KeyStoreError.creationFailed("sem chave pública")
        }
        var error: Unmanaged<CFError>?
        guard let data = SecKeyCopyExternalRepresentation(publicKey, &error) as Data? else {
            throw KeyStoreError.creationFailed(error?.takeRetainedValue().localizedDescription ?? "exportação")
        }
        return data // 0x04 || X || Y (65 bytes)
    }
}
