# Bibliothek nach Video-Vorlage — 22. Oktober 2026

Die Referenz `ssstwitter.com_1792334050214.mp4` zeigt eine Bibliothek mit Cover-Grid. Deshalb betrifft diese Änderung die Games-Übersicht (`LibraryPage`). Die einzelne Spielseite behält Game details → Preview → Beschreibung, ihren Bildbetrachter mit Bildrahmen/X und den BetterSteamTools-Dialog hinter dem +.

## Gestaltung und Verhalten

- Schwarze Fläche im dunklen Theme, zentrierte Inhaltsbreite bis 1408 DIP, große Überschrift und Anzahl rechts. Die Steamy-Navigation bleibt erhalten.
- Bis zu sechs Cover pro Reihe, Seitenverhältnis 3:4, Radius 9 DIP, horizontaler Abstand 26 DIP. Genre und zweizeiliger Titel stehen außerhalb des Bildes. Getrennte Abstände für Beschriftung und nächste Reihe entsprechen der Anordnung im Video.
- Suche, All/Installed/For you und Genre-Chips bleiben beim Scrollen oben stehen. Zusätzliche Quellen-, Typ-, Sortier- und Inhaltsfilter bleiben erreichbar. Die Genre-Chips verwenden vorhandene Metadaten; es werden keine Genre-Zahlen oder PlayStation-Plattformen erfunden. Die Cover zeigen STEAM beziehungsweise PC und bei vorhandenen Installationen ein Häkchen.
- 48 Titel im ersten Standard-Batch; „Show 48 more“ ergänzt weitere Titel, beim letzten Batch entsprechend weniger. Bereits erzeugte Container und Scrollposition bleiben beim Ergänzen erhalten. Ein Filterwechsel beginnt wieder mit einem begrenzten Batch.
- Die virtuelle Liste berücksichtigt Überschrift und Fußbereich in ihrem Scrollumfang. Ein Scrollschritt innerhalb derselben erzeugten Reihen verschiebt weiterhin nur die vorhandenen Karten. Überschrift, angeheftete Leiste und Nachlade-Knopf bewegen sich über Rendertransformationen. Auch nach mehreren Nachladevorgängen werden nur die sichtbaren Reihen samt kleiner Reserve aufgebaut.
- Metadaten-Hydrierung beim Durchsuchen des Katalogs bleibt auf den neuen Batch begrenzt. Cover laden weiter über die zentrale Bildpipeline und die Sichtbarkeits-Leases. Cover-Halos und der dauerhafte dunkle Titelverlauf über jedem Cover entfallen.

## Speicher

`UiObservableObject` erzeugt seine Synchronisierung und Benachrichtigungs-Sammlung jetzt erst für eine tatsächliche Benachrichtigung über einen anderen Dispatcher. Der überwiegende Teil des öffentlichen Katalogs benötigt diese Objekte nie.

Der isolierte Vergleich für 20.000 unveränderte Basismodelle ergab vorher **2.880.000 Byte**, nachher **800.000 Byte** (144 → 40 Byte je Basismodell). Die Array-Allokation liegt außerhalb der Messung. Das sind **2.080.000 Byte weniger Allokationen in diesem Test**, keine Aussage über den gesamten Prozessspeicher. Benachrichtigungen, die tatsächlich eine Warteschlange brauchen, legen diese weiterhin an und werden wie bisher gebündelt.

## Prüfung und Grenzen

`library-reference-release.trx`: **92/92 ausgewählte fensterlose Tests bestanden**. Darunter sind neue Layout-Prüfungen für 980/1440/1920 Pixel, Prüfung von Kopfbereich/Reihenabständen, Erhalt von Containern und Scrollposition beim Ergänzen, letztes Element, Filterwechsel, inaktive Ansicht und der Speichervergleich. Bestehende Bildpipeline-, Such-, Lifecycle-, Steam-Backend-, Spielinfo-, Logo- und Scroll-Regressionsprüfungen sind eingeschlossen.

Die unveränderte Suchindex-Implementierung behält zusätzlich die 18 bestandenen Core-Suchtests aus `library-search-core-release.trx`. Die Tests sind eine gezielte Auswahl, keine vollständige interaktive Oberflächenprüfung.

Steamy und Steam wurden für diese Lieferung nicht gestartet, sichtbare Fenster wurden nicht geöffnet. Das Referenzvideo wurde ausschließlich per FFmpeg in Dateien zerlegt. Ein sichtbarer 1:1-Abgleich, echte Eingabe-zu-Rendering-Zeiten, fünf Release-Vergleichsläufe und die ursprünglichen 220-MiB-/Start-/Navigationsziele sind für diese Version **nicht bestätigt**. Bereits vorhandene passive Prozessmessungen stammen aus früheren Builds und dienen nicht als Abnahme dieses Pakets.

## Portable Version

`artifacts/performance-refresh/portable-library-reference/Steamy.exe`, eigenständig mit .NET 10.0.13. Den gesamten Ordner zusammenlassen. Discord bleibt deaktiviert; das vorhandene Backup wird unverändert geprüft. Paket-, Quellen- und Testbericht-Hashes stehen in `artifacts/performance-refresh/release-library-reference-validation.json`.
