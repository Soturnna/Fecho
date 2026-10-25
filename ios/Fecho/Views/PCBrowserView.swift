import QuickLook
import SwiftUI

/// Navega nas pastas pessoais do PC. A primeira abertura pede Face ID.
struct PCBrowserView: View {
    @Environment(AppModel.self) private var model
    let root: PCRoot?
    let path: String
    let title: String

    @State private var roots: [PCRoot] = []
    @State private var entries: [PCEntry] = []
    @State private var loading = true
    @State private var error: String?
    @State private var downloading: String?
    @State private var progress: Double = 0
    @State private var preview: URL?

    var body: some View {
        List {
            if let root {
                ForEach(entries) { entry in
                    if entry.isDir {
                        NavigationLink {
                            PCBrowserView(root: root, path: childPath(entry.name), title: entry.name)
                        } label: {
                            Label(entry.name, systemImage: "folder.fill")
                        }
                    } else {
                        Button {
                            Task { await open(entry, in: root) }
                        } label: {
                            FileRow(entry: entry, progress: downloading == entry.name ? progress : nil)
                        }
                        .buttonStyle(.plain)
                        .disabled(downloading != nil)
                    }
                }
            } else {
                ForEach(roots) { r in
                    NavigationLink {
                        PCBrowserView(root: r, path: "", title: r.name)
                    } label: {
                        Label(r.name, systemImage: icon(for: r.id))
                    }
                }
            }
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(root == nil ? .large : .inline)
        .overlay {
            if loading {
                ProgressView()
            } else if let error {
                ContentUnavailableView {
                    Label("Não foi possível abrir", systemImage: "lock.doc.fill")
                } description: {
                    Text(error)
                } actions: {
                    Button("Tentar de novo") { Task { await load() } }
                        .buttonStyle(.borderedProminent)
                        .foregroundStyle(.black)
                }
            } else if root != nil && entries.isEmpty {
                ContentUnavailableView("Pasta vazia", systemImage: "folder")
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .quickLookPreview($preview)
    }

    private func load() async {
        loading = true
        error = nil
        do {
            if let root {
                entries = try await model.filesList(root: root.id, path: path)
            } else {
                roots = try await model.filesRoots()
            }
        } catch KeyStoreError.authenticationCancelled {
            error = "É preciso confirmar com Face ID para ver as pastas do PC."
        } catch {
            self.error = error.localizedDescription
        }
        loading = false
    }

    private func open(_ entry: PCEntry, in root: PCRoot) async {
        guard entry.size <= AppModel.maxDownloadBytes else {
            model.toast = "Arquivos acima de 200 MB ainda não podem ser baixados."
            return
        }
        downloading = entry.name
        progress = 0
        defer { downloading = nil }
        do {
            preview = try await model.download(root: root.id, path: childPath(entry.name), name: entry.name,
                                               size: entry.size) { value in
                Task { @MainActor in progress = value }
            }
        } catch {
            model.toast = error.localizedDescription
        }
    }

    private func childPath(_ name: String) -> String {
        path.isEmpty ? name : path + "/" + name
    }

    private func icon(for id: String) -> String {
        switch id {
        case "desktop": "menubar.dock.rectangle"
        case "documents": "doc.fill"
        case "downloads": "arrow.down.circle.fill"
        case "pictures": "photo.fill"
        case "videos": "film.fill"
        case "music": "music.note"
        default: "folder.fill"
        }
    }
}

struct FileRow: View {
    let entry: PCEntry
    let progress: Double?

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.title3)
                .foregroundStyle(.secondary)
                .frame(width: 28)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.name).lineLimit(1).truncationMode(.middle)
                if let progress {
                    ProgressView(value: progress)
                } else {
                    Text("\(entry.size.formatted(.byteCount(style: .file))) · \(entry.modified.formatted(date: .abbreviated, time: .shortened))")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
        }
        .contentShape(.rect)
    }

    private var symbol: String {
        switch (entry.name as NSString).pathExtension.lowercased() {
        case "jpg", "jpeg", "png", "heic", "gif", "webp": "photo"
        case "mp4", "mov", "mkv", "avi": "film"
        case "mp3", "wav", "m4a", "flac": "music.note"
        case "pdf": "doc.richtext"
        case "xlsx", "xls", "csv": "tablecells"
        case "docx", "doc", "txt", "md": "doc.text"
        case "zip", "rar", "7z": "doc.zipper"
        default: "doc"
        }
    }
}
