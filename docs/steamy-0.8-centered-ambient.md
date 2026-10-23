# Mittige Spielinfos, weichere Kanten und weniger Startarbeit

Stand: 23. Oktober 2026. Aktuelles Paket: `artifacts/performance-refresh/portable-centered-ambient/Steamy.exe`, ganzer Ordner erforderlich. Die EXE wurde nicht gestartet.

## Gestaltung und Katalog

Der Spiel-Identitätsblock mit Cover, Titel, Aktionen, Spielzeit und zuletzt gespielt steht wieder mittig in einer maximal 880 Pixel breiten Fläche. Die Detailkarten darunter behalten ihre breitere Inhaltsfläche. Game details → Preview → Beschreibung sowie der Bildbetrachter bleiben erhalten.

Der Banner-Fade beginnt etwas früher und erhält einen flacheren Auslauf. Die aus dem Bild gewonnenen Ambientfarben bleiben hinter den Informationen deutlicher sichtbar. Ein gemeinsamer Gain begrenzt die Helligkeit auf 80/255, ohne die Farbkanäle einzeln abzuschneiden; die eigentliche schwarze Workspace bleibt unverändert. Es gibt keinen zusätzlichen Blur pro Frame.

Eine 20 Pixel hohe Verlaufsfläche an der Bibliotheks-Viewportkante ersetzt die harte Schnittkante. Sie verwendet keinen OpacityMask-Puffer für die gesamte Liste. Hochformat-Cover füllen weiterhin ihre Fläche. Bei einem querformatigen Ersatzbild verwendet die Karte Uniform statt UniformToFill, sodass kein kleiner Ausschnitt mit riesigen Buchstaben entsteht. Das schwarze STEAM-Textschild wurde durch das kleine offizielle Steam-Vektorzeichen ersetzt; die Bildform stammt aus Valves Header-SVG und ist in THIRD-PARTY-NOTICES dokumentiert.

Die Basisspiel-Suche entfernt jetzt explizit benannte Call-of-Duty-Begleitanwendungen mit den Endungen „ - Multiplayer“ oder „ - OSX“, einschließlich Trademark-Schreibweisen. Diese können in Steam als Game klassifiziert sein und liefen deshalb durch den bisherigen reinen Typfilter. Die Regel wirkt vor der teuren Verifikation unbekannter Suchtreffer und auch beim nachträglichen Bereinigen. Es handelt sich um eine begrenzte Namensregel für diese Legacy-Begleiter, nicht um die Behauptung, sämtliche regionalen Varianten oder falschen Katalogdaten weltweit seien gelöst. Primärbeispiele: https://steamcommunity.com/app/42710 und https://steamcommunity.com/app/209660 .

Für Bibliothekskarten werden bevorzugt die normalen 600×900-Capsules angefragt; der Decoder bleibt auf 400 Pixel Breite begrenzt. Aufgelöste normale Capsules haben Vorrang vor 2x-Capsules, sofern beide verfügbar sind. Die 2x- und Header-Fallbacks sowie vorhandene Cachedateien bleiben verwendbar. Die Datenmenge kann damit sinken; eine neue End-to-End-CDN-Zeit wurde nicht gemessen.

## Start und Warteschlange

Beim Wiederherstellen der Warteschlange ersetzt eine ID-Menge die wiederholte vollständige Duplikatsuche. Die Produktions-Downloadsammlung unterstützt eine gebündelte Reset-Meldung. Bereits laufende Jobs behalten ihre Objektidentität und ihren aktuellen Fortschritt; gespeicherte fehlende Jobs werden angehängt. Abgebrochene Ladevorgänge veröffentlichen nichts, gewöhnliche ObservableCollections bleiben kompatibel. Die tatsächlichen Downloads, AutoResume-Policy und Speicherformate ändern sich nicht.

Die nach dem ersten Rendern gestartete Tool-Erkennung verarbeitet Dateien weiterhin auf einem Worker, veröffentlicht den gefundenen Pfad nun aber auf dem Synchronisierungskontext des Aufrufers. Direkte AppSettings-Abonnenten laufen dadurch nicht auf dem Worker. Ein blockierter Such-Worker verhindert im Dispatcher-Test keine Eingabeaufgabe.

Der isolierte Queue-Vergleich lädt die vorherige und die neue Release-Assembly in separaten Konsolenprozessen, jeweils mit fünf warmen Wiederherstellungen von 1.000 synthetischen Downloads und dem echten ShellDownloadViewModel als Beobachter. Setup und Datenbank-I/O sind ausgeschlossen. Vorher entstehen 1.000 CollectionChanged-Meldungen pro Lauf, danach eine. Zeiten und verwaltete Allokationen stehen in `checks/queue-restore-comparison.json`; sie sind weder Gesamtstartzeit noch Prozess-RAM. Es werden keine Application-/Window-Objekte, Konten, Netzwerkverbindungen oder Hilfsprozesse angelegt.

## Prüfungen und Grenzen

- `checks/catalog-polish-release.trx`: 254 ausgewählte Tests; `checks/catalog-polish-core.trx`: 418 Core-Tests, insgesamt 672 verschiedene Identitäten.
- `checks/centered-game.trx`: dieselbe PageSmoke-Identität in einem anderen Offscreen-Modus, nicht zusätzlich gezählt. 25 Renderings für Spielseite und Controller in Dark/Light bei 980/1440/1920 sowie Dashboard-Layout. Detached-Page-Ambient wird explizit mit dem Produktionsrenderer eingesetzt, weil kein echtes Loaded/Präsentationsfenster entsteht.
- `checks/catalog-polish-release`: 20 Offscreen-Bibliotheksbilder einschließlich Querbild-Fallbacks; feste Viewporthöhe, Titel-Rückkehr, Abstand zu Suchhinweisen und Virtualisierung geprüft.
- `checks/queue-startup-before.trx` reproduziert die 1.000 Meldungen und die Veröffentlichung des Toolpfads auf dem Worker. Neue Tests prüfen danach Live-Job-Erhalt, Wiederherstellung ohne Duplikate, Abbruch, neu abonnierte Jobs und Veröffentlichung auf dem Dispatcher.
- Die anfängliche Gestaltungsprüfung `checks/cover-polish.trx` zeigte zwei Fehler: eine ungeeignete leere Suchabfrage im neuen Test und ein zu helles gesättigtes Ambient. Der Suchtest verwendet nun einen echten nichtleeren Treffer; der Ambientrenderer hält weiterhin die bestehende Helligkeitsgrenze ein. Der vollständige ausgewählte Testlauf ist anschließend grün.

Die App bleibt auf Wunsch geschlossen. Aktuelle reale Startzeiten, Navigation, FPS, Eingabe-zu-Rendering, physische DPI und Gesamt-RAM sind damit weiterhin nicht vollständig überprüft. Das umfassende Performance-Goal ist nicht als erreicht bestätigt. Paket-, Quell-, Test-, Bild- und Vergleichshashes stehen in `release-centered-ambient-validation.json`.
