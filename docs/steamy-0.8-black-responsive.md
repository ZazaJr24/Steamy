# Schwarzer Grund und kürzere Ladeabhängigkeiten — 22. Oktober 2026

## Darstellung

Im dunklen Theme ist die allgemeine Arbeitsfläche jetzt ein eingefrorener schwarzer Pinsel statt einer Fläche mit zusätzlichen Farbverläufen. Bibliothek und Downloads erzeugen im dunklen Theme ebenfalls keinen bildabhängigen Farbnebel mehr. Die Bibliothek registriert dort keine Artwork-Beobachter für die Hintergrundanalyse und startet keinen Palette-Worker. Helles Theme, hoher Kontrast und explizit konfigurierte statische Hintergründe bleiben erhalten; die eigene Spielszene und das Home-Banner behalten ihre Inhalte.

Der gemeinsame Lichtreflex von Navigation und Steamy-Schalter liegt jetzt hinter Text und Symbolen. Die Navigationsbeschriftung verwendet 13 DIP und Medium. Standardmäßige `SurfaceMotion`-Einblendungen skalieren Text nicht mehr: StartScale ist eins; ausdrücklich konfigurierte Skalierungen bleiben möglich. Bereits vorhandene Display-/ClearType-/DPI-Einstellungen bleiben aktiv. SmoothScroll setzt nur geänderte, auf physische Displaypixel gerundete Offsets und erhält die exakte Endposition einer Seite auch bei ungeraden Ausmaßen. Das soll Text-Zoom und Subpixelbewegung reduzieren, ist keine Messung der tatsächlichen LCD-Schärfe.

Die Schriftauflösung enumeriert beim ersten Laden nicht mehr zweimal sämtliche installierten Schriftfamilien. WPF löst die benannte SF-Pro-Familie und die gebündelte Ersatzschrift verzögert auf. Der schnelle Pfad für lokal bereitgestellte SF-Pro-Dateien bleibt erhalten.

## Laden und Navigation

Spiel-Cover und Header erscheinen unabhängig voneinander, sobald das jeweilige Bild bereit ist. Dies gilt für lokale Steam-Spiele, Spotlight-Karten und Kataloganfragen mit Header. Der langsamere Partner blockiert die Veröffentlichung des fertigen Bilds nicht mehr. Auch die Ersatzbilder aus aktueller Steam-Metadatenauflösung werden parallel veröffentlicht. Ein abgebrochener Auftrag veröffentlicht kein nachträglich fertig gewordenes Bild. Die gemeinsame Bildpipeline und ihre 32-MiB-/512-MiB-Grenzen bleiben bestehen.

Auch normale Tool-/Fixes-Seiten können im fenstereigenen Seitencache wiederverwendet werden. Es bleiben höchstens drei Ansichten insgesamt, einschließlich der aktuellen Seite; die am längsten nicht verwendete wird freigegeben. Spiel-Detailansichten bleiben wegen ihres gewählten Spiels transient. Zwei Fenster teilen keine Seiteninstanz. Ein erneuter Klick auf die bereits angezeigte Route baut ihre Ansicht nicht neu auf. Die Modelldaten und laufenden Dienste bestehen unabhängig vom Cache weiter.

## Prüfung und Grenzen

- `checks/black-responsive-release.trx`: 208 erfolgreiche ausgewählte fensterlose UI-/Modelltests, einschließlich vorhandener Bildcache-/Abbruch-/Virtualisierungs-/Theme-Prüfungen und Steam-Artwork-Tests.
- `ProgressiveCoverTests`: sechs Fälle mit tatsächlich blockiertem Header oder Cover. Das fertige Gegenstück muss bereits veröffentlicht sein, während der Gesamtauftrag noch blockiert ist. Ein zusätzlicher Fall verhindert die Veröffentlichung nach Abbruch.
- `NavigationReuseTests`: Erhalt von Ansicht und ungespeichertem Text, tatsächliche LRU-Verdrängung, Freigabe, Drei-Seiten-Grenze und getrennte Fensterbereiche; ohne Fensterinstanziierung.
- Vier zusätzliche Scroll-Fälle für 100/125/150/200 % DPI und die unveränderte exakte Endposition. Ein zusätzlicher Bibliotheksfall prüft sämtliche gerenderten Pixel auf opakes Schwarz, während der Palette-Worker blockiert ist.
- `checks/black-responsive-core-release.trx`: alle 418 Core-Tests erfolgreich. Zusammen 626 unterschiedliche Prüfungen in den zwei Berichten.

Die Portable wird unter `artifacts/performance-refresh/portable-black-responsive/Steamy.exe` geliefert; den gesamten Ordner zusammenlassen. `release-black-responsive-validation.json` enthält die Paket-/Quellen-/Bericht-Hashes. Der unveränderte Dashboard-Zeichenpfad wird zusätzlich mit den bestehenden 48 fensterlosen Pixel-Fällen an der veröffentlichten Assembly geprüft; dies ist keine vollständige aktuelle Oberflächenaufnahme.

Es wurden keine App, kein Steam und kein sichtbares Diagnosefenster gestartet. Aktuelle reale FPS, alle Seitenwechsel-/Startzeiten, LCD-Schärfe und gesamter App-RAM sind damit nicht als erreicht belegt. Netzwerk- und Serverzeiten sind von den beseitigten lokalen Abhängigkeiten zu unterscheiden. Die gesamte App wird nicht pauschal als lagfrei oder unter 220 MiB gemeldet.
