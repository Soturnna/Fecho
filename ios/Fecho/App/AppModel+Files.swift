import CryptoKit
import Foundation

struct UploadItem: Identifiable, Equatable {
    enum Status: Equatable { case sending, done(String), failed(String) }
    let id = UUID()
    var name: String
    var size: Int64
    var sent: Int64 = 0
    var status: Status = .sending

    var progress: Double { size > 0 ? Double(sent) / Double(size) : 1 }
}

struct PCRoot: Identifiable, Hashable {
    var id: String
    var name: String
}

struct PCEntry: Identifiable, Hashable {
    var id: String { name }
    var name: String
    var isDir: Bool
    var size: Int64
    var modified: Date
}

/// Troca de arquivos (docs/SEGURANCA.md, seção 9).
extension AppModel {
    static let maxDownloadBytes: Int64 = 200 * 1024 * 1024
    static let maxUploadBytes: Int64 = 500 * 1024 * 1024

    // MARK: - Permissão (Face ID)

    /// Pede Face ID uma vez; o PC libera as pastas por 10 minutos nesta conexão.
    func ensureFilesGrant(force: Bool = false) async throws {
        if !force, let until = filesGrantedUntil, until > .now { return }
        try await client.request("files.grant", auth: .unlock(reason: "Abrir as pastas de \(pcName)"))
        filesGrantedUntil = .now.addingTimeInterval(9 * 60)
    }

    /// Executa um pedido de arquivos; se a permissão caiu (reconexão), pede Face ID de novo.
    private func withGrant(_ cmd: String, _ args: [String: Any]) async throws -> JSONObject {
        try await ensureFilesGrant()
        do {
            return try await client.request(cmd, args, auth: .session)
        } catch AgentError.needsGrant {
            filesGrantedUntil = nil
            try await ensureFilesGrant(force: true)
            return try await client.request(cmd, args, auth: .session)
        }
    }

    func filesRoots() async throws -> [PCRoot] {
        let reply = try await withGrant("files.roots", [:])
        let list = reply.value["result"] as? [[String: Any]] ?? []
        return list.compactMap { r in
            guard let id = r["id"] as? String, let name = r["name"] as? String else { return nil }
            return PCRoot(id: id, name: name)
        }
    }

    func filesList(root: String, path: String) async throws -> [PCEntry] {
        let reply = try await withGrant("files.list", ["root": root, "path": path])
        let list = reply.value["result"] as? [[String: Any]] ?? []
        return list.compactMap { e in
            guard let name = e["name"] as? String else { return nil }
            let modified = (e["modified"] as? NSNumber)?.doubleValue ?? 0
            return PCEntry(name: name, isDir: e["dir"] as? Bool ?? false,
                           size: (e["size"] as? NSNumber)?.int64Value ?? 0,
                           modified: Date(timeIntervalSince1970: modified / 1000))
        }
    }

    /// Baixa o arquivo em blocos para uma pasta temporária e devolve o caminho.
    func download(root: String, path: String, name: String, size: Int64,
                  progress: @escaping (Double) -> Void) async throws -> URL {
        guard size <= Self.maxDownloadBytes else { throw AgentError.invalid }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Fecho", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let safeName = name.replacingOccurrences(of: "/", with: "_")
        let target = folder.appendingPathComponent(safeName)
        try? FileManager.default.removeItem(at: target)
        FileManager.default.createFile(atPath: target.path, contents: nil)
        let handle = try FileHandle(forWritingTo: target)
        defer { try? handle.close() }

        var offset: Int64 = 0
        while true {
            let reply = try await withGrant("files.read", ["root": root, "path": path, "offset": offset, "length": 48 * 1024])
            guard let result = reply.value["result"] as? [String: Any],
                  let b64 = result["data"] as? String, let data = Data(base64Encoded: b64) else { throw AgentError.failed }
            try handle.write(contentsOf: data)
            offset += Int64(data.count)
            let total = (result["size"] as? NSNumber)?.int64Value ?? size
            progress(total > 0 ? Double(offset) / Double(total) : 1)
            if result["eof"] as? Bool == true || data.isEmpty { break }
            if offset > Self.maxDownloadBytes { throw AgentError.invalid }
        }
        return target
    }

    // MARK: - Envio do iPhone para o PC

    /// Envia um arquivo local em blocos de 48 KB e confere o SHA-256 no fim.
    func upload(fileAt url: URL, name: String) async {
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
        var item = UploadItem(name: name, size: size)
        uploads.insert(item, at: 0)

        func update(_ change: (inout UploadItem) -> Void) {
            change(&item)
            if let i = uploads.firstIndex(where: { $0.id == item.id }) { uploads[i] = item }
        }

        guard size <= Self.maxUploadBytes else {
            update { $0.status = .failed("Maior que 500 MB.") }
            return
        }

        do {
            let handle = try FileHandle(forReadingFrom: url)
            defer { try? handle.close() }

            let begin = try await client.request("files.upload.begin", ["name": name, "size": size])
            guard let result = begin.value["result"] as? [String: Any], let id = result["id"] as? String else {
                throw AgentError.failed
            }
            let chunk = (result["chunk"] as? Int) ?? 48 * 1024

            var hasher = SHA256()
            var offset: Int64 = 0
            while offset < size {
                guard let data = try handle.read(upToCount: chunk), !data.isEmpty else { break }
                hasher.update(data: data)
                try await client.request("files.upload.chunk",
                                         ["id": id, "offset": offset, "data": data.base64EncodedString()],
                                         auth: .session)
                offset += Int64(data.count)
                let sent = offset
                update { $0.sent = sent }
            }
            let digest = Data(hasher.finalize()).base64EncodedString()
            let end = try await client.request("files.upload.end", ["id": id, "sha256": digest], auth: .session)
            let finalName = (end.value["result"] as? [String: Any])?["name"] as? String ?? name
            update { $0.status = .done(finalName); $0.sent = size }
            await refreshActivity()
        } catch {
            update { $0.status = .failed(error.localizedDescription) }
        }
    }

    func clearFinishedUploads() {
        uploads.removeAll { if case .sending = $0.status { return false } else { return true } }
    }
}
