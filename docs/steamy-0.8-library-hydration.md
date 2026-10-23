# Suchergebnisse unabhängig vom Nachladen — 22. Oktober 2026

Vier gezielte fensterlose Tests reproduzierten im vorherigen Stand diese Probleme (`library-hydration-before.trx`, 0/4 bestanden):

1. Die Suche und die lokalen Ergebnisse blieben auf zusätzliche Store-Details wartend, obwohl der passende Titel bereits feststand.
2. Nach dem Verlassen der Bibliothek wartete die Karten-Hydrierung weiter auf einen asynchronen Metadatenanbieter, der den Abbruch ignorierte.
3. Release-Daten wurden bei jedem Such-/Filterauftrag durch einen vollständigen Durchlauf des Katalogs auf dem aufrufenden Thread angewendet.
4. Ein bereits dekodiertes Fallback-Cover wurde übergangen, wenn dessen Datei im Festplattencache nicht mehr existierte. Stattdessen blockierte eine frühere, fehlende CDN-Adresse die Anzeige.

## Umsetzung

Such-/Filterergebnisse werden nach der lokalen Auswertung veröffentlicht. Das Nachladen der Karten läuft mit eigener Revision und Lebensdauer weiter; „Suche läuft“ und die lokalen Ergebnisse warten darauf nicht. Bekannte Titel werden weiterhin sofort angezeigt, fehlende Metadaten ergänzen sich anschließend.

Die Abfrage wertet ein eingefangenes Release-Dictionary zusammen mit dem eigentlichen Filter im Hintergrund aus. Sie verändert dabei keine gemeinsamen Katalogobjekte. Damit entfällt der separate vollständige Durchlauf auf dem aufrufenden Thread und ein überholter Suchauftrag kann keine neuen Release-Flags überschreiben. Nur die veröffentlichten Karten übernehmen die aktuellen Flags. Der Snapshot der installierten Titel wird pro Filterauftrag einmal statt zweimal erzeugt.

Bulk-Release-/Artwork-Aufrufe starten im Hintergrund. Die zusätzliche Detail-Hydrierung bearbeitet höchstens vier Titel parallel. Alle wartenden Aufrufe sind an den Abbruch der aktuellen Ansicht gebunden; der Konsument kann auch dann aufhören zu warten, wenn der asynchrone Anbieter den Token ignoriert. Spätere Fehler solcher Tasks werden beobachtet. Gemeinsam genutzte oder nicht abbrechbare Arbeit des Anbieters wird dadurch nicht gewaltsam beendet.

Der Cover-Fallback durchläuft zuerst die zentrale Cache-Only-Pipeline. Diese nutzt dekodierte Bilder direkt und prüft Dateien außerhalb des aufrufenden Threads. Die vorgeschalteten synchronen `File.Exists`-Abfragen im Cover-Lader entfallen. Ein warmes Fallback-Bild kann deshalb vor einer langsamen bevorzugten CDN-Adresse erscheinen, auch nach einer Löschung seiner Datei aus dem öffentlichen Cache.

Die kompakte Gestaltung mit erstem Cover bei 184 DIP, angehefteten Filtern und sichtbaren Auswahlzuständen bleibt unverändert. Die einzelne Spielseite, BetterSteamTools hinter dem + und das Discord-Backup bleiben erhalten.

## Verifikation

- `library-hydration-after.trx`: die vier ursprünglichen Reproduktionen bestehen (4/4).
- `library-hydration-release.trx`: **97/97 ausgewählte fensterlose Tests bestanden**. Zusätzlich prüft ein neuer Fall, dass ein Release-Overlay veraltete Flags beim Filtern überstimmt, ohne gemeinsame Modelle zu verändern. Bestehende Such-, Lifecycle-, Cover-, Speicher-, Scroll-, Logo-, Spielinfo- und Steam-Backend-Prüfungen sind enthalten.
- `library-hydration-catalog-regression.trx`: **10/10 weitere Cover-/Katalogtests bestanden**, einschließlich Offline-Wiederverwendung, fehlender Cover, langsamer paralleler Downloads, Metadaten-Cache und Coming-Soon-Filter.
- Die einsekündigen Grenzen der isolierten Tests dienen nur dazu, das Warten auf absichtlich blockierte Anbieter zu erkennen. Sie sind keine Messung der App-Navigationszeit oder der Rendering-Latenz.

Keine Steamy-/Steam-Instanz wurde gestartet, navigiert oder beendet. Keine sichtbaren Fenster und kein interaktiver Test wurden geöffnet. Die früheren 18 Core-Suchtests betreffen die unveränderte Suchindex-Implementierung; sie werden im Paketmanifest getrennt mit ihrem ursprünglichen Bericht geführt.

Die vollständigen Leistungsziele sind weiterhin **nicht bestätigt**: fünf kontrollierte Release-Vergleiche, normaler Leerlauf ≤220 MiB, Start-/Navigationszeiten, sichtbare Bildladezeit, Eingabe-zu-Rendering-P95, zehn Navigationsrunden sowie aktueller visueller/DPI-Abgleich bleiben offen. Der bestehende Zielumfang wird durch diese gezielten Regressionstests nicht ersetzt.

## Portable

`artifacts/performance-refresh/portable-library-hydration/Steamy.exe`, eigenständig mit .NET 10.0.13. Den gesamten Ordner zusammenlassen. Prüfungen und Hashes: `release-library-hydration-validation.json`. Die vorherige vom Nutzer gestartete Version wird nicht überschrieben.
