# Gemeinsames Glasmaterial und UI-Optimierung — 16.10.2026

Die gemeinsamen Flächen für Karten, Filter, Seitenleisten, Felder und Dialoge verwenden abgestufte transparente Verläufe mit klaren Lichtkanten. Die Palette gilt für Dark und Light. Freunde, Chatfelder und Nachrichten, Controller, Einstellungen, Tools, Downloads und Einrichtung nutzen dieselben Materialien. Feste deckende Sonderfarben im Download- und Chatbereich wurden ersetzt. Die dunkle Grundfläche bleibt neutral; schwaches stationäres Licht macht die Glaskanten sichtbar. Die Spielseite behält ihre echten Artwork-Farben und die zuletzt gewünschte Reihenfolge Game details → Preview → Beschreibung. Der Bildbetrachter bleibt ausschließlich Bildrahmen und X.

Glas wird über gemeinsame eingefrorene bzw. wiederverwendete Brushes dargestellt. Kleine Shell-Bereiche verwenden die bestehenden begrenzten Hintergrund-Samples; es gibt keinen neuen Live-Blur auf jeder Karte oder der ganzen Seite. Reduzierte Effekte und hoher Kontrast erhalten deckende, lesbare Ersatzflächen.

Die Kontrastprüfung der Leiste verwendet wiederverwendete RenderTargetBitmap-Puffer für die Navigation und den kleinen Ausschnitt der Spielseite. Mehrere Scroll-/Hintergrund-/Größenmeldungen werden auf einen ausstehenden Dispatcher-Aufruf zusammengefasst. Versteckte Menüelemente und ihre Ressourcen werden nur neu gesetzt, wenn sich der benötigte Farbmodus ändert. Beim Schließen werden ausstehende Aktualisierungen abgebrochen und die Samples freigegeben. Der Bild-Converter teilt weiterhin aktive Bilder, hält verlassene Bilder aber nur noch über schwache Referenzen; die Metadaten bleiben auf 96 Einträge begrenzt. Bildgrößen, virtuelle Listen und die Beschränkung des Bildbetrachters auf ein ausgewähltes Bild bleiben erhalten.

## Vergleichsmessung

`performance-before/responsiveness.json` und `performance-after/responsiveness.json` enthalten zwei getrennte WPF-Testläufe mit gleicher Szene (1440×900, 180 Chatnachrichten, 120 synthetische Eingaben). Der Vorher-Lauf enthält bereits das neue Glasmaterial und misst den Renderer vor den Optimierungen.

- 200 explizite Leistenaktualisierungen: 5.758.776 → 4.473.600 verwaltete Bytes (22,3 % weniger neue Zuweisungen); 1.207 → 1.035 ms Gesamtzeit.
- Eingabe bis zum nächsten WPF-Rendering-Ereignis, 95. Perzentil: 5,448 → 5,432 ms.
- Private Prozessspeicher: 335,2 → 324,0 MB; Working Set: 273,9 → 299,2 MB. Die einzelnen Prozessmessungen schwanken und belegen keine allgemeine RAM-Reduktion. Der Testprozess enthält außerdem den Testhost und seine Fixtures.

Die Werte sind keine Messung physischer Tastatur-/Bildschirmlatenz oder Discord-Netzwerkverzögerung. Es wurden keine echten Nachrichten gesendet, keine Controller-Treiber installiert und keine Nutzerdaten für die Tests verwendet.
