# Einklappender Bibliothekskopf und Startpfad – 23. Oktober 2026

Die Bibliothek verbraucht die ersten 64 Pixel der Scrollbewegung für den Titelbereich. Titel und Zähler gleiten aus dem sichtbaren Bereich und blenden aus; Suche und Filter rücken direkt darunter nach oben und bleiben anschließend erreichbar. Beim Zurückscrollen zum Anfang kehrt der Titel zurück. Die Bewegung nutzt denselben Pixeloffset wie SmoothScroll und benötigt keine zusätzliche Animationsuhr oder eine Animation von Zeilenhöhen.

Das virtuelle Panel hat 64 Pixel LeadingHeight. Die Galerie erhält einen festen oberen Abstand entsprechend Filterhöhe plus 24 Pixel; ihre Viewporthöhe ändert sich beim Einklappen nicht. Dadurch muss die Kopfbewegung nicht ständig die komplette Liste neu vermessen. Bei Suchhinweisen oder Ladefeedback wird deren tatsächliche Höhe separat berücksichtigt, damit Cover und Filter sich nicht überdecken. Suchtexte, Commands und Galerie-Scrollposition bleiben in ihren bisherigen Modellen/Views.

`JsonSettingsService.Load()` durchsucht keine Tool-Verzeichnisse und prüft keine Tool-Hashes mehr. Die bisherige automatische Tool-Erkennung bleibt erhalten, startet jedoch einmal nach ContentRendered auf einem Worker. Die Suche teilt parallele Aufrufer, respektiert manuelle Pfadänderungen und verwirft das Ergebnis nach einem Settings-Reset. Gespeicherte gültige Pfade bleiben unverändert. Die vorhandenen Hashprüfungen für Steamy-spezifische Tool-Fähigkeiten wurden nicht entfernt. Es wird dadurch kein Tool-Prozess gestartet.

## Nachweise

- `checks/compact-library-verified.trx`: 250 ausgewählte UI-/Modelltests, darunter zwei neue Settings-Starttests mit einer absichtlich blockierten Suchfunktion und der Offscreen-Bibliotheksablauf.
- `checks/compact-library-core.trx`: 418 Core-Tests. Insgesamt 668 verschiedene Testidentitäten.
- Die erste Regression (`checks/compact-library-release.trx`) hatte einen Fehler im HTTP-Testzähler: parallele Header-/Coveranfragen inkrementierten einfache Integer ohne Synchronisierung. Die Zähler im Testhandler verwenden jetzt Interlocked; die Forderung nach beiden aktuellen Bildern und einem persistent reparierten Cache bleibt unverändert. Der vollständige ausgewählte Lauf wurde danach wiederholt.
- 18 Offscreen-PNGs in `checks/compact-library-verified`: Anfang, eingeklappter Zustand und sichtbarer Suchhinweis bei 980/1440/1920 Pixeln, jeweils Dark/Light. 1.000 Fixture-Einträge mit wiederholten lokalen Covermotiven; keine echten Kontodaten, keine Netzwerkabrufe, kein Window-Objekt und kein App-Start. Die Statusanzahl ist Teil dieser künstlich eingesetzten Testdaten.
- Für Scrollpositionen 0, 10, 24, 64 und 400 werden Filterposition, Titelopacity, unveränderte Viewporthöhe, Rückkehr zum Anfang und weniger als 50 realisierte Container geprüft. Suchhinweise lassen weiterhin 24 Pixel Abstand zwischen Filterunterkante und Galerie.

Die portable EXE liegt unter `artifacts/performance-refresh/portable-compact-library/Steamy.exe`. Der gesamte Ordner gehört zum Paket. Der Validator prüft Runtime 10.0.13, Hilfsprogramme, deaktiviertes Discord und das unveränderte Discord-Backup sowie Quell-, Report- und Bildhashes.

Die Startpfadänderung entfernt nachweisbar die Tool-Suche aus dem synchronen Laden der Einstellungen. Sie ist keine Messung der gesamten App-Startzeit. Aufgrund der Vorgabe, die App nicht sichtbar zu öffnen, wurden weder dieses Paket gestartet noch aktuelle FPS, Eingabe-Latenzen oder Gesamt-RAM gemessen. Die umfassenden Leistungsziele bleiben offen.
