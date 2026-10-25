import AppIntents

/// Frases da Siri e atalhos prontos. Só no app (um provedor por app).
struct FechoShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: LockPCIntent(),
                    phrases: ["Bloquear o PC no \(.applicationName)", "Trancar o PC no \(.applicationName)"],
                    shortTitle: "Bloquear o PC", systemImageName: "lock.fill")
        AppShortcut(intent: SleepPCIntent(),
                    phrases: ["Suspender o PC no \(.applicationName)"],
                    shortTitle: "Suspender o PC", systemImageName: "moon.fill")
        AppShortcut(intent: PlayPauseIntent(),
                    phrases: ["Pausar a música no \(.applicationName)", "Tocar a música no \(.applicationName)"],
                    shortTitle: "Reproduzir ou pausar", systemImageName: "playpause.fill")
        AppShortcut(intent: NextTrackIntent(),
                    phrases: ["Próxima faixa no \(.applicationName)"],
                    shortTitle: "Próxima faixa", systemImageName: "forward.fill")
    }
}
