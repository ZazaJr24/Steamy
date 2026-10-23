# Spiel-Fade, Freunde und SF Pro — 16.10.2026

Der Banner-Fade reicht 150 Pixel über den ursprünglichen Hero-Bereich hinaus. Die Bildebene liegt direkt im gemeinsamen Seitenhintergrund, damit der Hero-Layoutbereich sie nicht an seiner Unterkante abschneidet. Der Übergang ist länger und läuft hinter der Spielidentität aus. Titel verwenden die Display-Schrift und die tatsächlichen Genres als Unterzeile. Die kompakten Aktionen mit Spielzeit und zuletzt gespielt bleiben erhalten.

Die Freunde-Übersicht enthält echte Kontakte aus dem verbundenen Discord-Snapshot mit Avataren, Status und direktem Chat-Einstieg. Die Vorschau ist auf acht echte Freunde begrenzt und sortiert Online-Kontakte zuerst; Requests und blockierte Personen werden hier nicht als Freunde angezeigt. Ein Kontakt öffnet den bestehenden rechten Chat-Dock, ohne den Hauptbereich zu wechseln. Die vollständige Liste bleibt über All friends zugänglich. Beim Verlassen der Seite werden die Snapshot-Abonnements entfernt. Vorhandene Kontakt-Elemente werden bei unveränderten Snapshots wiederverwendet.

Der Dock verwendet rundere Außenkanten, eine segmentierte Freunde-/Chat-Leiste, ein kompaktes Konto mit Avatar und ruhigere Nachrichtenblasen. Hoher Kontrast verwendet weiterhin Systemfarben. Entwürfe, Leserposition, Nachrichten-/Party-/Voice-/GIF-Funktionen bleiben an den bisherigen Modellen und Diensten angebunden.

SF Pro Text und SF Pro Display aus dem vom Nutzer genannten Repository https://github.com/sahibjotsaggu/San-Francisco-Pro-Fonts wurden für diesen PC nach `%LOCALAPPDATA%/Steamy/Fonts/SFPro` geladen. `AppTypography` verwendet zuerst diesen lokalen Ordner, dann installierte SF-Pro-Familien, anschließend die bereits gebündelte Google-Sans-Schrift. Titel und normaler Text verwenden die entsprechenden Display-/Text-Familien. Die SF-Pro-Dateien sind nicht Bestandteil des Repositorys oder der öffentlichen Installationspakete. Ein anderer PC kann seine eigenen SF-Pro-Dateien im gleichen benutzerbezogenen Ordner verwenden und die App neu starten; andernfalls greift der Fallback. Es gibt keinen automatischen Schrift-Download beim App-Start.

Prüfungen unter `artifacts/steamy-0.8/social-polish/checks/`:

- `details-social-final.trx`: 263 Tests bestanden, einschließlich Spielseite, SF-Pro-Bindungen, verlängerter Fade und Pixelprüfung auf eine Kante am ehemaligen Bannerende.
- `windowed-social-final.trx`: echter WPF-Test für drei Fenstergrößen und beide Themes; Kontaktklick öffnet den passenden Chat, Hauptseite/Entwurf bleiben erhalten, volle Seitenbreite kehrt nach dem Schließen des Docks zurück.
- `discord-social-final.trx`: Discord-/GIF-/Voice-UI-Prüfungen mit Offline-Fixtures bestanden.
- Screenshots in `ui/` zeigen die kompilierte Oberfläche mit Testdaten, keine generierten Designbilder.

Die Ausgabe liegt separat unter `artifacts/steamy-0.8/social-polish/release/`. Frühere Ausgaben und laufende Nutzerfenster werden nicht ersetzt.

Finale Verifikation: Auch die vollständige UI-/Modell-Suite bestand mit der neuen Typografie alle 263 Tests (`full-social-font.trx`). Fünf isolierte Start-Szenarien bestanden; Bibliothek, Notizen und synthetisch verschlüsselte Sitzungsdaten blieben erhalten. Online- und Vollsetup bestanden 32 bzw. 36 Selbstprüfungen. Die erzeugten ZIP-Dateien stimmen mit den SHA-256-Hashes des lokalen Manifests überein. Es wurde kein GitHub-Release veröffentlicht und kein echter Discord-Account zum Senden von Testnachrichten verwendet.
