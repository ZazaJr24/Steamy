# Direkte Tools-Auswahl und kurze Übergänge — 22. Oktober 2026

Tools behält seine bestehende Auswahl oben in der Navigation. Ein Klick auf ein Werkzeug führt direkt auf dessen Seite. Der bisherige Blur über dem gesamten Arbeitsbereich, dessen Radius-Animation und die Deaktivierung der darunterliegenden Oberfläche entfallen. Das Auswahlfeld selbst verwendet die vorhandene Glasfläche des Themes ohne eigenen Live-Page-Capture, ohne Dimmer und ohne separaten ganzflächigen Hintergrund. Escape, erneuter Klick auf denselben Tab, ein Klick außerhalb und Fenster-Deaktivierung schließen die Auswahl. Die acht bestehenden Tools-Ziele und beide Fixes-Ziele bleiben erhalten.

Die Auswahl blendet sich über 150 ms mit vier DIP vertikaler Bewegung ein. Die gewählte Werkzeugseite erhält einen 160-ms-Übergang mit sechs DIP Bewegung. Beide verwenden eine kubische Ease-out-Kurve ohne Textskalierung; sie verzögern weder Auswahl noch Bedienbarkeit durch eine Wartezeit. Reduzierte Bewegung deaktiviert die Animationen weiterhin. Die bisherigen Standardwerte anderer `SurfaceMotion`-Verwendungen bleiben unverändert.

Navigationsanfragen werden mit Eingabepriorität statt Hintergrundpriorität eingestellt. Bei UC Online werden die reflektierten erweiterten Optionsfelder erst beim Öffnen des normalerweise geschlossenen Settings-Abschnitts erzeugt. Ein ausgetauschtes Optionsmodell wird beim Öffnen erneut aufgebaut; bestehende Bindungen innerhalb desselben Modells bleiben erhalten. Dadurch entfällt dieser Aufbau beim ersten Anzeigen der Seite.

Die Bibliothek verwendet echte getrennte Layout-Zeilen für Überschrift, Filter und Galerie. Der Filter-Hintergrund ist transparent, nur die Galerie scrollt. Cover behalten ihr 3:4-Verhältnis und Titel/Genre darunter; vertikale Abstände sind verkleinert. Die künstliche 184-DIP-Headerhöhe im virtuellen Galerie-Panel und die verschobenen überlagerten Header-/Filterflächen entfallen. Basisspiel-Prüfung und bestehende Such-/Filter-/Ladefunktionen bleiben erhalten.

## Bildanalyse im Zeichenpfad

Die Kartenbeleuchtung zeichnet jetzt ausschließlich vorbereitete, eingefrorene Pinsel. `ArtworkHalo.OnRender` analysiert keine neuen Quellbilder und erzeugt keine Pinsel. Der bestehende STA-Worker berechnet die kleinen Farbsamples; Quellen-/Aktivierungs-/Fensterwechsel brechen veraltete Vorbereitungen ab. Die zwölf Lichtpunkte und ihre bisherigen Farben/Koordinaten bleiben erhalten. Trailer-Poster bereiten ihre Kontrast-Samples asynchron vor; die Toolbar liest nur fertige Samples und verwendet bis dahin ihre bestehende Hintergrund-Kontrastinformation.

## Nachweise und Lieferung

- `checks/halo-preparation-before.trx`: acht Fälle am synchronen Ausgangspfad, vier Fehler. Der neue Async-Einstieg bildete für diesen Vorher-Nachweis zunächst den alten synchronen Aufruf als erfülltes Task ab; der Abbruchfall definiert die neue API-Semantik.
- `HaloPreparationTests`: zehn fensterlose Fälle für den zeichnungsfreien Cache-Miss, Worker/Abbruch, unveränderte Crop-/Farbwerte, schwache Bildhaltung und eingefrorene, beim Unload freigegebene Paint-Pinsel. Der Paint-Test bereitet die tatsächliche Paint-Phase direkt vor, weil ein Element ohne Fenster nicht `IsVisible` ist; das ist keine vollständige reale Hover-/Fensterereignisprüfung.
- `ToolsSelectionTests`: Struktur der direkten Auswahl ohne Capture/Dimmer, erhaltene Ziele, kurze unskalierte Motion-Konfiguration, Bibliotheks-Zeilen und verdrahteter, geschlossener UC-Settings-Abschnitt. Diese Tests belegen keine reale Ende-zu-Ende-Tab-Latenz.
- `checks/tools-direct-release.trx`: **184** ausgewählte fensterlose UI-/Modelltests.
- `checks/workspace-core-all-release.trx`: **418** vollständige Core-Tests. Zusammen **602**, ohne doppelte Zählung enthaltener Suchindex-Tests.

Portable: `artifacts/performance-refresh/portable-tools-direct/Steamy.exe`, eigenständig mit .NET 10.0.13. Den gesamten Ordner zusammenlassen. Paket-/Quellen-/Bericht-Hashes: `release-tools-direct-validation.json`. Discord bleibt deaktiviert, das bisherige Backup bleibt geprüft erhalten.

Es wurden keine Steamy-/Steam-Instanz und kein sichtbares Diagnosefenster gestartet. Aktuelle reale Scroll-FPS, Navigation-/Eingabezeiten, Gesamt-RAM und die vollständige visuelle Abnahme bleiben offen. Das vollständige Ziel ist damit noch nicht als erreicht belegt.
