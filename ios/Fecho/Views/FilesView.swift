import SwiftUI

struct FilesView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(alignment: .leading, spacing: 12) {
                        Text(model.clipboardText.map { $0.isEmpty ? "A área de transferência do PC está vazia." : $0 }
                             ?? "Toque em Buscar para ver o último texto copiado no PC.")
                            .font(.subheadline)
                            .foregroundStyle(model.clipboardText?.isEmpty == false ? .primary : .secondary)
                            .lineLimit(6)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(12)
                            .background(Color(.tertiarySystemGroupedBackground), in: .rect(cornerRadius: 14))
                            .textSelection(.enabled)

                        HStack(spacing: 10) {
                            if model.clipboardText?.isEmpty == false {
                                Button {
                                    model.copyPCClipboardToPhone()
                                } label: {
                                    Text("Copiar no iPhone").font(.subheadline.weight(.semibold))
                                        .foregroundStyle(.black).frame(maxWidth: .infinity)
                                }
                                .buttonStyle(.borderedProminent)
                            } else {
                                Button {
                                    Task { await model.fetchClipboard() }
                                } label: {
                                    Text("Buscar do PC").font(.subheadline.weight(.semibold))
                                        .foregroundStyle(.black).frame(maxWidth: .infinity)
                                }
                                .buttonStyle(.borderedProminent)
                            }
                            Button {
                                Task { await model.sendClipboardToPC() }
                            } label: {
                                Text("Colar no PC").font(.subheadline.weight(.semibold)).frame(maxWidth: .infinity)
                            }
                            .buttonStyle(.bordered)
                        }
                        .buttonBorderShape(.capsule)
                        .controlSize(.large)
                    }
                    .padding(.vertical, 6)
                } header: {
                    Text("Área de transferência do PC")
                } footer: {
                    Text("O texto só é lido quando você pede. Nada fica salvo no iPhone além do que você copiar.")
                }

                Section {
                    Label("Enviar fotos e arquivos ao PC", systemImage: "arrow.up.doc.fill")
                        .foregroundStyle(.secondary)
                    Label("Abrir pastas do PC", systemImage: "folder.fill")
                        .foregroundStyle(.secondary)
                } header: {
                    Text("Arquivos")
                } footer: {
                    Text("A troca de arquivos chega em uma próxima versão do Fecho.")
                }
            }
            .navigationTitle("Arquivos")
            .disabled(!model.isConnected)
            .refreshable { await model.fetchClipboard() }
        }
    }
}
