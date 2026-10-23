# Spielseite: weicher Fade, fester Zurück-Pfeil und Renderkosten

Umgesetzt am 23. Oktober 2026. Das neue Paket liegt separat unter
`artifacts/gamepage-fade/portable/Steamy.exe`; den gesamten Portable-Ordner
beziehungsweise die zugehörige ZIP verwenden.

## Darstellung

Das Banner läuft mit einer glatten, am Ende vollständig transparenten Kurve in die
Seite aus. Die letzten fünf Prozent liegen bei höchstens einem Alpha-Schritt;
die letzte Pixelzeile ist komplett transparent. Dadurch entsteht keine sichtbare
Abschlusslinie. Der Fade wird einmal auf dem vorhandenen Artwork-Worker vorbereitet,
eingefroren und im begrenzten Bildcache wiederverwendet. Die große laufende
OpacityMask über dem Banner entfällt. Der Bildausschnitt bleibt vom restlichen
Seitenhintergrund getrennt und passt sich dem Fenster an.

Der Zurück-Pfeil liegt außerhalb des ScrollViewers, oberhalb der Seite und unterhalb
von Screenshot-/Installationsdialogen. Er bleibt bei langem Scrollen erreichbar.
Im echten Seitentest blieb seine Y-Position in beiden Themes bei 76 Pixeln; vorher
wanderte sie von 65 auf −585 Pixel.

## Performance

Die Glasleiste und die Kontrastprüfung hatten jeweils ein VisualBrush auf die
komplette Spielseite angelegt. Der neue Pfad zeichnet für diese schmalen Flächen
nur die vorbereiteten Hintergrundschichten. Karten, Text und Bedienelemente werden
dafür nicht erneut als Seitenbaum erfasst. Ereignisse für diese Darstellung werden
beim Verlassen wieder abgemeldet; sie aktualisieren nur die betreffenden Glasflächen.
Unveränderte Seitenbreiten lösen keine neue Größen-/Ressourcenrunde aus.

Die Banner-Vorbereitung läuft abbrechbar außerhalb des UI-Threads. Kalte Bildwechsel
führen nicht zu synchroner Pixelarbeit auf dem Fensterthread; spätere Ergebnisse
einer verlassenen Seite werden verworfen. Die Skalierung nutzt eine quantisierte,
begrenzte Bildgröße und höchstens drei schwache Cachevarianten je Bild. Die
Pixelmultiplikation verwendet einen einmal pro Zeile berechneten Ganzzahlfaktor.

## Kontrollierte Messung

Eine echte WPF-Spielseite wurde mit festem Artwork und Test-Metadaten in einem
1280×900-Fenster außerhalb des Bildschirms dargestellt. Vorher und nachher wurden
je Theme 20 aufgewärmte RenderTargetBitmap-Aufnahmen gemessen. Zusätzlich wurden
der ursprüngliche VisualBrush-Algorithmus und die neue Kontrastaufnahme abwechselnd
auf derselben neuen Seite gemessen.

| Arbeitsschritt, Median | Dunkel vorher | Dunkel nachher | Hell vorher | Hell nachher |
|---|---:|---:|---:|---:|
| Ganze Seite als Speicherbitmap | 113,37 ms | 105,27 ms | 211,40 ms | 199,45 ms |
| Kontrastaufnahme der Leiste | 2,571 ms | 0,428 ms | 1,750 ms | 0,385 ms |

Die Kontrastaufnahme benötigte in diesem Vergleich **78–83 % weniger Zeit**.
Die ganzen Seitenaufnahmen waren etwa 6–7 % günstiger. Das sind unterschiedliche
Messgrößen; daraus folgt keine vier- oder sechsfache Geschwindigkeit der gesamten
App und keine Aussage über Bildschirm-FPS auf anderer Hardware. Die anschließende
Ganzzahloptimierung betrifft nur die kalte Vorbereitung, nicht diese warmen Werte.

Die Rohwerte stehen in `artifacts/gamepage-fade/performance-comparison.json` und
den jeweiligen `before/`- und `final-check/`-Unterordnern.

## Prüfungen und Grenzen

- 104 ausgewählte Prüfungen bestanden: echte Spielseite in Hell/Dunkel,
  unverrückbarer Zurück-Pfeil, Bildvorbereitung, Spielseiten-Lebensdauer,
  Scrollbewegung, virtuelle Galerie und Providerpakete/DepotBox.
- Nach der Ganzzahloptimierung wurden die drei Fade-Regressionen erneut bestanden.
- Pixelprüfungen decken transparentes Ende, stetigen Alpha-Verlauf,
  vormultiplizierte Farbwerte, Cachewiederverwendung und Abbruch ab.
- Die echte Seite wurde ausschließlich offscreen ohne Aktivierung/Taskleisteneintrag
  geöffnet. Der Test prüfte, dass sie nicht den Vordergrund übernahm.
- Bilder aus `final-check/` wurden visuell in beiden Themes und gescrollt geprüft.
  Ihre Texte/Erfolge sind ausdrücklich kontrollierte Layout-Testdaten.
- Der neue Portable-Build bekommt zusätzlich den vorhandenen Hintergrund-Starttest
  mit eigenem Datenprofil. Sein Ergebnis steht unter `portable-background-test/`.

Die bereits vorher dokumentierten vollständigen, älteren UI-Smoke-Szenarien mit
abweichenden Farb-/Ambient-Erwartungen gehören nicht zu diesen 104 grünen Prüfungen.
Eine vollständige manuelle Abnahme aller App-Seiten und DPI-Kombinationen bleibt
ein eigener Schritt. Der VPS-/API-Schlüssel-Pfad wurde bei diesen Änderungen nicht
angepasst; Zugangsdaten gehören weiterhin nicht ins App-Paket.
