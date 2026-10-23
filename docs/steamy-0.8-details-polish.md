# Spielseite: Vorschau, Banner und Scrollen — 21.10.2026

Portable Version: `E:\Steamy\artifacts\performance-refresh\portable-details-polish\Steamy.exe`.
Den gesamten Ordner behalten. App-DLL SHA-256:
`E76566B9E32FF718329F91426EF9470BCFBA38E49721C957D2C0527329B07AFE`.

## Änderungen

- **Preview-Bilder:** Der Button-ContentPresenter ignorierte die gewünschte Inhaltsausrichtung. Leere Bilder bekamen dadurch keine nutzbare Breite und konnten den sichtbarkeitsabhängigen Ladevorgang nicht beginnen. Der Presenter berücksichtigt jetzt seine Template-Einstellungen; Vorschaukacheln strecken den Inhalt. Der unnötige innere ScrollViewer ist entfernt. Der neue Test scheitert vorher am ausbleibenden Thumbnail und besteht nach der Korrektur mit vier tatsächlich geladenen öffentlichen Testbildern.
- **Banner und Fade:** Das scharfe Banner hat eine eigene, aus seinem Bildformat berechnete Höhe. Es wird nicht mehr über Banner und Ambient-Verlängerung gemeinsam vergrößert. Dadurch wird insbesondere beim breiten RDR2-Banner wesentlich weniger vom rechts liegenden Motiv abgeschnitten. Der Verlauf besitzt auf diese Bildhöhe abgestimmte absolute Koordinaten und endet vollständig transparent. Das Ambient darunter bleibt erhalten.
- **Scrollarbeit:** Der Home-Hintergrund behält seine Layout-/Scrollkoordinaten, zeichnet außerhalb des sichtbaren Viewports aber keine unnötigen Hintergrundmasken. ScrollChanged aktualisiert den Ausschnitt bei Sprüngen und Größenänderungen. Aktive Hover-Ambientübergänge behalten den bisherigen vollständigen Kompositionspfad. Die synchrone Kontrastaufnahme der Glasleiste wird bei fortlaufenden Scrollereignissen auf höchstens einen geplanten Aktualisierungszyklus je 80 ms gebündelt. Dies ist keine Zusicherung einer bestimmten Bildrate auf beliebiger Hardware.
- **Schrift:** ClearType, festes Hinting, Display-Formatierung und Pixel-/Layoutrundung für die Fenster- und Textstile. Die bestehende SF-Pro-/Google-Sans-Auswahl bleibt erhalten. Keine Windows-weiten Schrift- oder Rendering-Einstellungen geändert.

Die Reihenfolge bleibt **Game details → Preview → Beschreibung**. Der Bildbetrachter bleibt **Bildrahmen und X**.

## Prüfung

Artefakte liegen in `artifacts/performance-refresh/`:

- `checks/details-preview-before.trx`: neuer Thumbnail-Test reproduziert den bisherigen Fehler.
- `checks/details-preview-fixed-2.trx`: **6/6**, einschließlich pixelbasierter Hintergrundprüfungen und Scrollsprünge/Resize ohne transparente Lücken.
- `checks/details-polish-full-2.trx`: **306/306 UI-/Integrationstests**.
- `checks/details-polish-core.trx`: **412/412 Kernprüfungen**, insgesamt **718 eindeutige Tests**.
- `checks/details-polish-acceptance.trx`: **1/1** zusätzliche Abnahme, **114 gehashte WPF-Aufnahmen**, Dark/Light, 980/1440/1920 Pixel und zusätzliche Render-Skalierung.
- `startup-details-polish/startup-report.json`: **5/5**, PerMonitorV2, Bibliothek/Notizen/Einstellungen sowie synthetische verschlüsselte Credential-Bytes erhalten, Tracker Exit 0.

Die gezielte Thumbnail-Prüfung verwendet den wirklichen AsyncArtwork-Ladeweg und lokale Kopien öffentlicher Spielbilder; die Testpipeline kann diese Dateien jetzt korrekt lesen. Sie prüft Bildquellen und sichtbare Größen vor dem Öffnen des Viewers. Sichtgeprüft: neuer Bannerübergang, vier geladene Screenshot-Kacheln und Betrachter. Die tatsächliche Monitoreinstellung beträgt weiterhin 100 %; Render-Skalierung ersetzt keine physische DPI-Abnahme. Discord-/Controller-Prüfungen verwenden Fixtures, keine echte Sitzung oder Hardware.

Zwei zusätzliche Fehlversuche bleiben erhalten: Der neue Scrolltest musste nach dem Schließen sein WPF-Unloaded-Ereignis abarbeiten, damit keine Test-Dispatcher-Abonnements zurückbleiben. Außerdem erwartete eine bisherige Ambient-Prüfung auch außerhalb des Viewports gerenderte Pixel. Sie prüft jetzt ausdrücklich das vollständige logische Hintergrundbild; die neue Viewport-Prüfung kontrolliert separat die tatsächliche sichtbare Abdeckung. Die Produktoptimierung wurde dafür nicht zurückgenommen.

## Leistungsgrenze

`after-details-polish-controlled-v8/run-0.json` wurde vom strengen Mess-Launcher als **nicht kontrolliert zurückgewiesen**. Der echte Steam-Scan hat gegenüber dem älteren Fixture eine andere Arbeitslast erzeugt, darunter zusätzliche Anfragen für App 284160. Die Antworten waren nicht vollständig im aufgezeichneten HTTP-Bestand enthalten. Der Lauf wird nicht als gültiger Vorher-/Nachher-Vergleich verwendet; fehlende Antworten wurden nicht erfunden und die Medienkontrolle wurde nicht abgeschaltet.

Für diese Version gibt es deshalb **keine neue abgenommene FPS-, RAM- oder Navigationsverbesserung**. Der ältere 220-MiB-Zielwert gilt weiterhin nicht als erreicht. Die funktional geprüften Fehlerkorrekturen sind davon getrennt dokumentiert. Die frameworkabhängige UI-Test-App hat DLL-Hash `BB024B1BC66F31563EC3F0572CBE7AFF0FF3C9B4DA644EA2FD522382F666BFC6`; die portable Version verwendet dieselben Produktquellen und die eigenständige .NET-10.0.13-Laufzeit.
