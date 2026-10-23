# Weniger Zeichenarbeit beim Scrollen — 22. Oktober 2026

Home und das darüberliegende Glas zeichnen den gemeinsamen Hintergrund über `DashboardBackdropView`. Bisher wurden seine Verläufe, Masken und Bildpinsel für jeden aufgezeichneten Frame neu erzeugt und eingefroren. Sie werden jetzt als kleine unveränderliche Zeichenmaterialien für die aktuelle Größe, Bannerhöhe, Theme und Ambient-Einstellung wiederverwendet. Änderungen verwerfen diese Materialien; ersetzte Bilder und geschlossene Vorschauen bleiben dadurch nicht zusätzlich in den Pinseln hängen. Eine vollständig weiße Überblendung über der bereits weißen Grundfläche entfällt ebenfalls.

Es kommt kein zusätzlicher Bitmap- oder Rastercache für eine ganze Seite hinzu. Rechtecke und Masken bleiben auf den angefragten sichtbaren Ausschnitt begrenzt. Der vorhandene Bildcache behält sein 32-MiB-Limit. Das weiße Design und der Basisspiel-Filter der vorherigen Lieferung bleiben erhalten.

## Hover-Ambient bleibt im Ausschnitt

Mit aktiver Hover-Vorschau wurde die Ausschnittsbegrenzung bisher explizit übersprungen, weil sich deren relative Masken sonst beim Zuschneiden verschoben. Eine kleine Glasfläche konnte deshalb Zeichenbefehle für die volle 2.400-DIP-Testseite aufzeichnen. Vorschau und Maske verwenden nun den gleichen absoluten Seitenbereich, während nur die sichtbare Schnittfläche aufgezeichnet wird. Dadurch bleiben die Farben und die Position des Effekts erhalten.

Zwei neue Ausschnittfälle reproduzierten das Problem vor der Korrektur (`checks/backdrop-preview-bounds-before.trx`); sie bestehen jetzt. Die beiden neuen Allokationsbudgets scheiterten vorher bei 7.864 beziehungsweise 8.952 Byte je Aufzeichnung und bestehen nachher mit 1.264 Byte im einfachen fensterlosen Test. Die Prüfungen zum Freigeben von Bildquellen sichern den neuen Materialcache ab; sie belegen keinen zuvor bestätigten allgemeinen App-Speicherverlust.

## Vergleich der tatsächlichen Release-Assemblies

Fünf gepaarte Durchläufe pro Version, seriell und nach abgeschlossenen Builds: `portable-white-base-games` gegen `portable-render-materials`. Ein separater Diagnoseprozess lädt ausschließlich den Hintergrund-Zeichencode aus der jeweiligen Assembly und verwendet kontrollierte eingefrorene, teilweise transparente Kunstbilder. Er erstellt weder `Application` noch `Window`, startet keine `Steamy.exe`, liest keine Nutzer-/Kontodaten und verwendet kein Netzwerk.

Jede Zeit-/Allokationsprobe zeichnet nach 40 Aufwärmframes **480 Ausschnitte** auf. Medianwerte aus `checks/backdrop-materials-comparison.json`:

| Modus | Allokationen vorher | Nachher | Aufzeichnungszeit vorher → nachher |
|---|---:|---:|---:|
| Dark, ohne Hover | 3.982.120 Byte | 606.760 Byte | 18,97 → 1,99 ms |
| Dark, mit Hover | 4.285.480 Byte | 910.120 Byte | 18,01 → 2,50 ms |
| Light, ohne Hover | 4.561.960 Byte | 606.760 Byte | 41,05 → 2,64 ms |
| Light, mit Hover | 4.807.720 Byte | 910.120 Byte | 37,83 → 3,47 ms |

Das sind rund **79–87 % weniger Allokationen in diesem Zeichenpfad**, keine entsprechende Verringerung des gesamten App-RAM. Mit Hover sinkt die Höhe der aufgezeichneten Zeichenbefehle in der 620-DIP-Probe von 2.400 auf 620 DIP. Das misst weder GPU-Speicher noch die tatsächliche WPF-Compositor-Laufzeit.

48 Kombinationen aus Theme, Ambient, Hover, Bannerüberblendung und Bildausschnitt werden je Durchlauf als Speicherbitmap gerendert. Die SHA-256-Werte der vollständigen und der zugeschnittenen Aufnahmen stimmen zwischen beiden Versionen exakt überein: **240 identische Bildproben-Paare** über fünf Durchläufe. Das ist eine Pixelprüfung des Hintergrunds mit Kunstbildern, keine vollständige Abnahme der laufenden App.

Die Probe liegt in `tests/Steamy.CatalogProbe/BackdropFrameProbe.cs`; Aufruf nach dem CLI-Build:

```powershell
.\.cache\dotnet10\dotnet.exe tests/Steamy.CatalogProbe/bin/Release/net10.0-windows/Steamy.CatalogProbe.dll <portable-folder> <report.json> backdrop
```

Die Rohberichte `checks/backdrop-probe-{white-base-games,render-materials}-{1..5}.json` enthalten Paket-Hashes, Zahlen und Bild-Hashes. Minima und Maxima der Zeiten stehen ebenfalls in der Zusammenfassung. Hintergrundaktivität des PCs war nicht vollständig kontrolliert; die Zahlen ersetzen keinen echten Bedienungs-/FPS-Test.

## Prüfungen und Lieferung

- `checks/render-materials-release.trx`: **142/142 ausgewählte fensterlose Prüfungen**. Darin sieben Material-/Lebensdauer-/Neuzeichnungsfälle und sechs Ausschnittfälle. Sie prüfen unter anderem das Freigeben ersetzter/geschlossener Bilder, künstliches Unload, Größenwechsel bei 980/1440/1920 DIP und Speicherbitmap-DPI 96/144. Das ist keine vollständige Fenster-/Monitor-DPI-Abnahme.
- Die **22/22** Suchindex-Prüfungen aus `checks/white-search-core-release.trx` gelten weiterhin für den unveränderten Suchcode. Insgesamt **164 gezielte Prüfungen**, keine vollständige interaktive Testreihe.

Portable: `artifacts/performance-refresh/portable-render-materials/Steamy.exe`, eigenständig mit .NET 10.0.13; den gesamten Ordner zusammenlassen. Quellen-, Paket-, Prüfbericht- und Vergleichs-Hashes stehen in `release-render-materials-validation.json`. Die vorherige EXE wird nicht überschrieben; Discord bleibt deaktiviert und das Backup wird unverändert geprüft.

Es wurde kein Nutzerfenster geöffnet, bedient oder neu gestartet. Das breite Ziel bleibt offen: aktueller Leerlauf-RAM samt Hilfsprozessen, echte Start-/Navigationszeiten, Input-bis-Rendering, Scroll-FPS und die vollständige visuelle Abnahme sind weiterhin nicht als erreicht nachgewiesen.
