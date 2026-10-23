# Pitch Black mit Ambient — 22. Oktober 2026

Das gemeinsame Dark-Theme verwendet jetzt Schwarz als Grundfläche. Seitenleisten, Karten, Eingaben und Dialoge teilen dunkle transparente Materialien; Text und wichtige Aktionen behalten ihren Kontrast. Home, Bibliothek, Downloads und Spielseite führen die Farben ihrer Spielbilder als gedämpftes Ambient über Schwarz weiter. Helle Bildbereiche bleiben in den eigentlichen Bannern und Covern sichtbar.

Die Bibliothek und Downloads verwenden eingefrorene Zeichenpinsel mit wenigen Farbverläufen. Die Spielseite verarbeitet weiterhin ein kleines, zwischengespeichertes Hintergrundbild. Es kommt kein zusätzlicher laufender Blur oder Animationstimer hinzu. Die dunkle Navigation bleibt auch über hellen Bannern dunkel; ihre zuvor dafür nötige Hintergrundabtastung entfällt. Ein transparenter weißer Endpunkt im schwarzen Verlauf wurde durch transparentes Schwarz ersetzt, damit kein graues Band entsteht.

Light-Theme, hoher Kontrast und gespeicherte eigene Hintergrundfarben bleiben erhalten. Neue Einstellungen verwenden Schwarz als Standard. Die Spielseite behält Banner und langen Fade, kompakte Aktionen, Spielzeit und letzten Start sowie die Reihenfolge **Game details → Preview → Beschreibung**. Der Bildbetrachter bleibt Bildrahmen und X. Discord bleibt deaktiviert; das Backup ist unverändert.

## Nachweise

- `checks/pitch-black-release.trx`: **112/112 ausgewählte fensterlose Tests**, einschließlich vier neuer Theme-/Pixelprüfungen, Ambient-Verarbeitung, Bildpipeline, Seiten-/Suchlebensdauer, Virtualisierung und Steam-Backend.
- `checks/search-allocation-release.trx`: **22/22 Suchindex-Tests** für den unveränderten Suchcode dieser Lieferung.
- **134 gezielte Prüfungen**, keine vollständige interaktive Abnahme. Die Pixelprüfungen rendern nur Materialien und künstliche Bilder in Speicherbitmaps, ohne Application oder Window. Sie prüfen schwarze Flächen, gedämpfte Farben und deren Farberhalt, keine vollständigen App-Screenshots.
- Die vorherige Suchoptimierung bleibt enthalten. Ihre fünf gepaarten isolierten Vergleichsmessungen stehen in `checks/search-memory-comparison.json` und beziehen sich ausdrücklich auf `portable-search-memory`, die vorherige Release-Assembly. Details und der langsamere erste Aufbau der Tippfehler-Suche sind in `steamy-0.8-search-memory.md` dokumentiert.

## Lieferung und offene Abnahme

Portable: `artifacts/performance-refresh/portable-pitch-black/Steamy.exe`. Den gesamten Ordner zusammenlassen. Eigenständige .NET-10.0.13-Laufzeit und Hilfsprogramme werden statisch geprüft; Quellen-, Paket- und Prüfbericht-Hashes stehen in `release-pitch-black-validation.json`.

Wie angefordert wurde kein App-Fenster geöffnet oder bedient. Die tatsächliche Darstellung bei verschiedenen Fenstergrößen/DPI sowie FPS, Startzeit, Navigation, Eingabereaktion und Leerlaufspeicher dieser Version sind damit noch nicht vollständig gemessen. Das Gesamtziel einschließlich höchstens 220 MiB im normalen Leerlauf wird nicht als erreicht gemeldet.
