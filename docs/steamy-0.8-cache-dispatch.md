# Cache und Tool-Seiten ohne blockierende Vorarbeit — 23. Oktober 2026

## Änderungen

Ein bereits dekodiertes öffentliches Cache-Bild wartet nicht mehr auf `TrackDisk`: Verzeichniszählung, Zeitstempel und Verdrängung laufen für Cache-Treffer über einen einzelnen Hintergrundarbeiter. Die Warteschlange enthält ausschließlich höchstens 128 unterschiedliche Dateipfade und keine Bilder oder komprimierten Bilddaten. Zugriffszähler sind nachgelagerte Verwaltung; die bestehende Speicherung neuer Downloads mit Budgetprüfung bleibt erhalten. Auch ein bereits festgeschriebener Download muss seine Budgetprüfung beenden, wenn zwischen Schreiben und Verwaltung die Pipeline beendet wird. Dekodierter Cache bleibt bei 32 MiB, öffentlicher Dateicache bei 512 MiB.

Die Steamless-Statusprüfung kann synchron Dateien und Versionsangaben lesen, bevor sie überhaupt ein Task zurückgibt. `RefreshStatusAsync` startet diese Vorarbeit jetzt auf einem Hintergrundthread. Bei Aufruf auf dem UI-Kontext wird die Fortsetzung wieder dort ausgeführt. Neue Pfade invalidieren die alte Prüfung; verspäteter Erfolg und verspäteter Fehler dürfen weder den neuen Status noch dessen Version überschreiben. Ein geänderter, noch nicht geprüfter Pfad wird als „Not checked“ angezeigt.

UC Online liest und parst die optionale Einstellungsdatei nicht mehr im Konstruktor auf dem UI-Thread. Die begrenzte Leseoperation läuft im Hintergrund; bei vorhandenem UI-Kontext erfolgt die Veröffentlichung auf dem Eigentümerthread. Hat der Nutzer inzwischen Ordner, Spiel-ID oder Netzwerk-ID geändert oder einen Vorgang begonnen, wird der alte gespeicherte Stand verworfen. UTF-8 mit und ohne BOM bleibt kompatibel. Fehlende, ungültige oder mindestens 8192 Byte große Dateien bleiben optional und werden ignoriert. Die regulären Schreib-/Backup-/Installationsvorgänge bleiben unverändert.

Schwarzer Grund, die wiederhergestellte direkte Tools-Auswahl und gemeinsame Glasleisten bleiben erhalten.

## Evidenz

`checks/cache-dispatch-before.trx` führt zwei neue Regressionen gegen die tatsächlich zuvor gelieferte Assembly aus `portable-black-responsive` aus. Diese Assembly wurde in ein isoliertes Testverzeichnis kopiert; die Steamy-Anwendung selbst wurde nicht gestartet. Ihr SHA-256 bleibt `B0F226F0F9BBBB3E82861854CA3EDF82C4FBADC0E34C2261BB51D6795092FAD1`.

Beide Fälle scheitern dort: Das dekodierte Cache-Bild wartet auf den absichtlich gehaltenen Verwaltungs-Lock; die Steamless-Prüfung hält den aufrufenden STA-Thread bis zur Sicherungs-Zeitgrenze des Testproviders fest. Das sind kontrollierte Fehlerfälle und keine Messungen typischer App-Startzeiten oder echter Scroll-FPS.

Dieselben Fälle bestehen im geänderten Code. Zusätzlich geprüft werden alte Erfolgs-/Fehlerrückläufe, Pfadänderung ohne Folgeprüfung, Cache-Verdrängung beim Beenden sowie UC-Initialisierung mit laufendem Input-Callback, Eigentümerthread-Veröffentlichung, Erhalt neuer Eingaben und echte UTF-8-/BOM-/Größenlimit-Dateien. Die UC-Tool-Regressionen werden vollständig fensterlos mitgeprüft.

- `checks/cache-dispatch-release.trx`: 237 ausgewählte UI-/Modelltests bestanden.
- `checks/cache-dispatch-core-release.trx`: alle 418 Core-Tests bestanden.
- Zusammen 655 unterschiedliche Tests in den zwei Berichten; der absichtlich fehlgeschlagene Vorher-Bericht zählt nicht als Erfolg.

Portable: `artifacts/performance-refresh/portable-cache-dispatch/Steamy.exe`. Den gesamten Ordner zusammenlassen. `release-cache-dispatch-validation.json` erfasst Paket, Runtime, Quellen, Berichte und erhaltenes Discord-Backup. Die bestehenden 48 fensterlosen Pixelfälle prüfen den unveränderten Dashboard-Zeichenpfad der veröffentlichten Assembly; sie sind keine vollständige visuelle Abnahme.

Keine Steamy-/Steam-Instanz und kein sichtbares Testfenster wurde geöffnet. Die gesamte App ist damit weder als lagfrei noch als unter 220 MiB nachgewiesen. Die echte Oberflächen-, Framezeit-, Start- und Gesamt-RAM-Abnahme bleibt offen; das Gesamtziel wird nicht als erreicht markiert.
