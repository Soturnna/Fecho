import AppIntents
import SwiftUI
import WidgetKit

// ---------- Timeline (estático: os widgets são botões) ----------

struct FechoEntry: TimelineEntry {
    let date: Date
    let pcName: String
    let paired: Bool
}

struct FechoProvider: TimelineProvider {
    func placeholder(in context: Context) -> FechoEntry {
        FechoEntry(date: .now, pcName: "Meu PC", paired: true)
    }
    func getSnapshot(in context: Context, completion: @escaping (FechoEntry) -> Void) {
        completion(FechoEntry(date: .now, pcName: FechoQuick.pcName, paired: FechoQuick.isPaired))
    }
    func getTimeline(in context: Context, completion: @escaping (Timeline<FechoEntry>) -> Void) {
        let entry = FechoEntry(date: .now, pcName: FechoQuick.pcName, paired: FechoQuick.isPaired)
        completion(Timeline(entries: [entry], policy: .never))
    }
}

// ---------- Widget da Tela de Início ----------

struct FechoQuickWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "com.soturnna.fecho.widget.quick", provider: FechoProvider()) { entry in
            QuickView(entry: entry)
                .containerBackground(.fill.tertiary, for: .widget)
        }
        .configurationDisplayName("Fecho — ações")
        .description("Bloquear, suspender e mídia do PC, mais desbloquear com Face ID.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}

struct QuickView: View {
    let entry: FechoEntry

    var body: some View {
        if !entry.paired {
            VStack(spacing: 6) {
                Image(systemName: "desktopcomputer").font(.title2).foregroundStyle(.secondary)
                Text("Abra o Fecho e pareie o PC").font(.caption).multilineTextAlignment(.center)
            }
        } else {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 6) {
                    Image(systemName: "desktopcomputer").foregroundStyle(.secondary)
                    Text(entry.pcName).font(.subheadline.weight(.semibold)).lineLimit(1)
                }
                HStack(spacing: 10) {
                    ActionButton(symbol: "faceid", tint: .green, intent: OpenUnlockIntent())
                    ActionButton(symbol: "lock.fill", tint: .yellow, intent: LockPCIntent())
                    ActionButton(symbol: "playpause.fill", tint: .primary, intent: PlayPauseIntent())
                    ActionButton(symbol: "moon.fill", tint: .indigo, intent: SleepPCIntent())
                }
            }
            .padding(4)
        }
    }
}

struct ActionButton<I: AppIntent>: View {
    let symbol: String
    let tint: Color
    let intent: I

    var body: some View {
        Button(intent: intent) {
            Image(systemName: symbol)
                .font(.title3)
                .foregroundStyle(tint)
                .frame(maxWidth: .infinity, minHeight: 40)
                .background(.quaternary, in: .rect(cornerRadius: 12))
        }
        .buttonStyle(.plain)
    }
}

// ---------- Widgets da Tela de Bloqueio ----------

struct FechoLockWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "com.soturnna.fecho.widget.lock", provider: FechoProvider()) { entry in
            LockView(entry: entry)
                .containerBackground(.clear, for: .widget)
        }
        .configurationDisplayName("Fecho — bloquear")
        .description("Um toque para bloquear o PC pela Tela de Bloqueio.")
        .supportedFamilies([.accessoryCircular, .accessoryRectangular])
    }
}

struct LockView: View {
    @Environment(\.widgetFamily) private var family
    let entry: FechoEntry

    var body: some View {
        switch family {
        case .accessoryRectangular:
            HStack(spacing: 10) {
                Button(intent: LockPCIntent()) {
                    Image(systemName: "lock.fill")
                }
                Button(intent: PlayPauseIntent()) {
                    Image(systemName: "playpause.fill")
                }
                Button(intent: OpenUnlockIntent()) {
                    Image(systemName: "faceid")
                }
                Text(entry.pcName).font(.caption2).lineLimit(1)
            }
            .buttonStyle(.plain)
        default:
            Button(intent: LockPCIntent()) {
                Image(systemName: "lock.fill").font(.title3)
            }
            .buttonStyle(.plain)
        }
    }
}

// ---------- Controles da Central de Controle (iOS 18+) ----------

struct FechoLockControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: "com.soturnna.fecho.control.lock") {
            ControlWidgetButton(action: LockPCIntent()) {
                Label("Bloquear PC", systemImage: "lock.fill")
            }
        }
        .displayName("Bloquear o PC")
        .description("Bloqueia o PC pareado no Fecho.")
    }
}

struct FechoMediaControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: "com.soturnna.fecho.control.media") {
            ControlWidgetButton(action: PlayPauseIntent()) {
                Label("Mídia do PC", systemImage: "playpause.fill")
            }
        }
        .displayName("Reproduzir/pausar no PC")
        .description("Controla a mídia do PC pareado no Fecho.")
    }
}

struct FechoSleepControl: ControlWidget {
    var body: some ControlWidgetConfiguration {
        StaticControlConfiguration(kind: "com.soturnna.fecho.control.sleep") {
            ControlWidgetButton(action: SleepPCIntent()) {
                Label("Suspender PC", systemImage: "moon.fill")
            }
        }
        .displayName("Suspender o PC")
        .description("Suspende o PC pareado no Fecho.")
    }
}

// ---------- Bundle ----------

@main
struct FechoWidgetBundle: WidgetBundle {
    var body: some Widget {
        FechoQuickWidget()
        FechoLockWidget()
        FechoLockControl()
        FechoMediaControl()
        FechoSleepControl()
    }
}
