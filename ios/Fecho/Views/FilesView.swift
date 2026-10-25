import PhotosUI
import SwiftUI
import UniformTypeIdentifiers

struct FilesView: View {
    @Environment(AppModel.self) private var model
    @State private var photoItems: [PhotosPickerItem] = []
    @State private var importing = false

    var body: some View {
        NavigationStack {
            List {
                clipboardSection

                Section {
                    PhotosPicker(selection: $photoItems, maxSelectionCount: 10, matching: .any(of: [.images, .videos])) {
                        Label("Enviar fotos e vídeos", systemImage: "photo.on.rectangle.angled")
                    }
                    Button {
                        importing = true
                    } label: {
                        Label("Enviar arquivo", systemImage: "arrow.up.doc.fill")
                    }
                    ForEach(model.uploads) { UploadRow(item: $0) }
                } header: {
                    HStack {
                        Text("Enviar ao PC")
                        Spacer()
                        if model.uploads.contains(where: { if case .sending = $0.status { false } else { true } }) {
                            Button("Limpar") { model.clearFinishedUploads() }
                                .font(.footnote)
                                .textCase(nil)
                        }
                    }
                } footer: {
                    Text("Os arquivos chegam em Downloads › Fecho, no PC.")
                }

                Section {
                    NavigationLink {
                        PCBrowserView(root: nil, path: "", title: "Pastas do PC")
                    } label: {
                        Label("Pastas do PC", systemImage: "folder.fill")
                    }
                } footer: {
                    Text("Pede Face ID ao abrir. Mostra só as pastas pessoais: Área de Trabalho, Documentos, Downloads, Imagens, Vídeos e Música.")
                }
            }
            .navigationTitle("Arquivos")
            .disabled(!model.isConnected)
            .refreshable { await model.fetchClipboard() }
            .task(id: model.isConnected) {
                if model.isConnected && model.clipboardText == nil { await model.fetchClipboard() }
            }
            .onChange(of: photoItems) { _, items in
                guard !items.isEmpty else { return }
                photoItems = []
                Task { await sendPhotos(items) }
            }
            .fileImporter(isPresented: $importing, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
                guard case .success(let urls) = result else { return }
                Task { await sendFiles(urls) }
            }
        }
    }

    private var clipboardSection: some View {
        Section {
            VStack(alignment: .leading, spacing: 12) {
                Text(clipboardPreview)
                    .font(.subheadline)
                    .foregroundStyle(model.clipboardText?.isEmpty == false ? .primary : .secondary)
                    .lineLimit(6)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(12)
                    .background(Color(.tertiarySystemGroupedBackground), in: .rect(cornerRadius: 14))

                HStack(spacing: 10) {
                    Button {
                        model.copyPCClipboardToPhone()
                    } label: {
                        Text("Copiar no iPhone")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(.black)
                            .lineLimit(1).minimumScaleFactor(0.8)
                            .frame(maxWidth: .infinity, minHeight: 30)
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.clipboardText?.isEmpty != false)

                    Button {
                        Task { await model.sendClipboardToPC() }
                    } label: {
                        Text("Colar no PC")
                            .font(.subheadline.weight(.semibold))
                            .lineLimit(1).minimumScaleFactor(0.8)
                            .frame(maxWidth: .infinity, minHeight: 30)
                    }
                    .buttonStyle(.bordered)
                }
                .buttonBorderShape(.capsule)
                .controlSize(.large)
            }
            .padding(.vertical, 6)
        } header: {
            HStack {
                Text("Área de transferência do PC")
                Spacer()
                Button {
                    Task { await model.fetchClipboard() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .accessibilityLabel("Buscar de novo")
            }
        } footer: {
            Text("O texto só é lido quando você abre esta tela ou toca em atualizar.")
        }
    }

    private var clipboardPreview: String {
        guard let text = model.clipboardText else { return "Buscando…" }
        return text.isEmpty ? "A área de transferência do PC está vazia." : text
    }

    private func sendPhotos(_ items: [PhotosPickerItem]) async {
        for item in items {
            if let file = try? await item.loadTransferable(type: PickedFile.self) {
                await model.upload(fileAt: file.url, name: file.url.lastPathComponent)
                try? FileManager.default.removeItem(at: file.url)
            } else {
                model.toast = "Não consegui ler um dos itens selecionados."
            }
        }
    }

    private func sendFiles(_ urls: [URL]) async {
        for url in urls {
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            await model.upload(fileAt: url, name: url.lastPathComponent)
        }
    }
}

/// Copia o item escolhido na Fotos para um arquivo temporário, mantendo o nome original.
struct PickedFile: Transferable {
    let url: URL

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .item) { received in
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("FechoEnvio", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let target = folder.appendingPathComponent(received.file.lastPathComponent)
            try? FileManager.default.removeItem(at: target)
            try FileManager.default.copyItem(at: received.file, to: target)
            return PickedFile(url: target)
        }
    }
}

struct UploadRow: View {
    let item: UploadItem

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(item.name).lineLimit(1).truncationMode(.middle)
                Spacer()
                switch item.status {
                case .sending:
                    Text(item.progress, format: .percent.precision(.fractionLength(0)))
                        .font(.subheadline).monospacedDigit().foregroundStyle(.secondary)
                case .done:
                    Label("Enviado", systemImage: "checkmark").font(.subheadline).foregroundStyle(.green)
                case .failed:
                    Label("Falhou", systemImage: "xmark").font(.subheadline).foregroundStyle(.red)
                }
            }
            switch item.status {
            case .sending:
                ProgressView(value: item.progress)
            case .done(let finalName) where finalName != item.name:
                Text("Salvo como \(finalName)").font(.footnote).foregroundStyle(.secondary)
            case .failed(let message):
                Text(message).font(.footnote).foregroundStyle(.secondary)
            default:
                EmptyView()
            }
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }
}
