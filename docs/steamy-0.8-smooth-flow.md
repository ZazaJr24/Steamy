# Spielseite, Dashboard und Controller – 23. Oktober 2026

Die Controller-Seite verwendet jetzt drei direkt anwählbare Schritte: Connect, Test und Customize. Einrichtung und Treiberaktionen liegen unter PlayStation setup; Stickkurve, Deadzones, Lightbar, vollständige Mappings und Spielzuordnungen bleiben aufklappbar. Die bisherigen Commands, gespeicherten Profile und Navigationseinstellungen bleiben erhalten. Kein Schritt startet automatisch den Backend-Prozess oder einen Installer.

Die Spielseite richtet Cover, Titel und Details an gemeinsamen Rändern aus. Der Bannerbereich nimmt weniger Leerraum ein; die Aktionen mit Play, Plus, Steam, Spielzeit und zuletzt gespielt bleiben kompakt. Game details → Preview → Beschreibung sowie der Bildbetrachter bleiben erhalten. Das Banner erhält beim Eintreffen einen kurzen Opacity-Fade statt des großen Zooms. Parallaxbewegung und die zusätzlichen Eintrittsanimationen des gesamten Overview-Bereichs und jeder Detailkarte entfallen.

Das Dashboard wechselt seine Spotlight-Auswahl nicht mehr, während der Banner vollständig aus dem sichtbaren Bereich gescrollt ist. Scrollereignisse untergeordneter Listen lösen keine erneute Aktualisierung der Shell-Viewportdaten aus. Die bereits vorhandene zentrale SmoothScroll-Steuerung der horizontalen Spieleleiste bleibt aktiv; eine ungenutzte ältere MouseWheel-Hilfsmethode wurde entfernt.

Die Discovery-Bildarbeit besitzt einen abbrechbaren Aktivitätsbereich: Verlassen, Deaktivieren und Minimieren stoppen ausstehende Konsumenten, ignorieren verspätete Ergebnisse und starten benötigte Arbeit beim Wiederkehren neu. Vorschaubild und großes Banner können unabhängig ankommen. Die neuen Lifetime-Checks verwenden absichtlich Provider, die Cancellation ignorieren, um verspätete Ergebnisse zu prüfen.

Controller-Polling veröffentlicht nur geänderte Eingabe- und Geräteinformationen; unveränderte native Abfragen invalidieren nicht mehr das gesamte Formular oder alle Commands. Nur der Test-Schritt fragt ein vorhandenes Gerät mit 100 ms Intervall ab, die anderen Schritte mit 1 s. In unsichtbaren oder inaktiven Fenstern wird keine native Abfrage gestartet.

## Prüfung

- `checks/smooth-flow-release.trx`: 248 ausgewählte Tests einschließlich Controller-Lifecycle und dem neuen Offscreen-Seitenablauf.
- `checks/smooth-flow-core.trx`: 418 Core-Tests. Zusammen 666 unterschiedliche Testidentitäten.
- `checks/smooth-flow-discovery.trx`: derselbe PageSmoke-Test in einem zweiten, separaten Discovery-Lifetime-Modus; nicht erneut als zusätzliche Testidentität gezählt.
- 25 PNGs in `checks/smooth-flow-release`: Spielseite in Dark/Light bei 980/1440/1920 Pixeln, die drei Controller-Schritte in derselben Matrix und ein Dashboard-Layout. Native WPF Measure/Arrange/RenderTargetBitmap ohne Window-Objekt und ohne Production-App-Start. Das Ambient wird im abgetrennten Spielseiten-Rendering explizit mit dem Produktionsrenderer eingesetzt, da die Page ohne PresentationSource kein Loaded erhält. Fixtures, keine Benutzerkonten oder Netzwerkdownloads. Diese Bilder sind keine Screenshots eines laufenden App-Fensters und keine physischen DPI-Messungen.
- Der Controller-Test prüft 100 identische native Abfragen ohne PropertyChanged-/Command-Invalidierungen; eine anschließende Stickänderung muss weiterhin InputText veröffentlichen. Hotplug, Rumble-Command und Backend-Stop-Verhalten bleiben abgedeckt.

Die portable EXE befindet sich unter `artifacts/performance-refresh/portable-smooth-flow/Steamy.exe`; der ganze Ordner gehört zum Paket. Discord bleibt deaktiviert und sein vorhandenes Backup erhalten. Das Paket wurde nicht gestartet. Paket-, Runtime-, Quell-, Report- und Bildhashes stehen in `release-smooth-flow-validation.json`.

Keine neue Aussage zu tatsächlichen FPS, End-to-End-Latenz oder Gesamt-RAM: Der Nutzer möchte aktuell keine sichtbaren App-Fenster. Die bisherigen Leistungsziele, insbesondere 220 MiB im normalen Leerlauf und maximal 15 MiB bleibendes Wachstum, sind durch diese gezielten Tests nicht als erreicht bestätigt.
