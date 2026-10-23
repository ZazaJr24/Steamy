# Sparsamere gemeinsame Suche — 22. Oktober 2026

Der Suchindex wird von Home, der öffentlichen Bibliothek und der lokalen Spielsuche verwendet. Der bisherige Index erzeugte bereits beim Aufbau die gesamte Tippfehler-Vokabeldatenbank. Außerdem erzeugte die Prüfung aller Suchbegriffe für jeden Katalogeintrag ein Closure und einen Delegaten. Beides belastete selbst normale, exakt passende Suchanfragen.

Jetzt werden zunächst nur normalisierte Titel/App-IDs aufgebaut. Die Tippfehler-Datenbank entsteht erst bei einer tatsächlich benötigten ungefähren Suche, wird einmal vollständig veröffentlicht und anschließend wiederverwendet. Gleichzeitig ankommende ungefähre Suchen teilen den Aufbau; normale Suchanfragen warten nicht auf dessen Gate. Ein abgebrochener Aufbau veröffentlicht keine teilweise Datenbank und kann erneut versucht werden.

Die Wort-Trefferlisten liegen anschließend in einem gemeinsamen Integer-Array mit Bereichen pro Wort. Die Gruppen ähnlich geformter Wörter verwenden passende Arrays statt übergroßer Listen. Wörter außerhalb der bereits vorher geltenden Kandidaten-Längen werden nicht zusätzlich gespeichert. Kandidaten-Grenzen, Tippfehler-Regeln, Akzent-/Satzzeichenbehandlung, App-ID-Suche, Berechtigungsfilter und Trefferreihenfolge bleiben erhalten. Die exakte Suche prüft Begriffe ohne Allokation pro Titel.

## Isolierter Vergleich der Release-Assemblies

Fünf gepaarte Durchläufe je Version in separaten Diagnoseprozessen, jeweils 20.000 künstliche Titel, gleiche Konfiguration. Vorher: `portable-library-hydration`; nachher: `portable-search-memory`. Der Probe-Prozess lädt ausschließlich den Suchindex aus der Assembly. Er startet weder `Steamy.exe` noch eine WPF-Application/Window. Erzwungene Garbage Collection betrifft nur diesen isolierten Diagnoseprozess.

Medianwerte aus `checks/search-memory-comparison.json`:

| Messung | Vorher | Nachher |
|---|---:|---:|
| Allokationen beim Indexaufbau | 25.219.048 Byte | 5.761.960 Byte |
| Nach GC gebundener Speicher nach Aufbau | 7.528.408 Byte | 3.201.224 Byte |
| Nach GC gebundener Speicher nach erster Tippfehler-Suche | 7.530.952 Byte | 5.483.192 Byte |
| Indexaufbau | 52,21 ms | 14,71 ms |
| Zehn weitere exakte Suchen, gesamte Allokationen | 17.605.976 Byte | 5.976 Byte |
| Zehn weitere exakte Suchen, gesamte Zeit | 10,49 ms | 4,48 ms |
| Erste Tippfehler-Suche bei vorhandenem Basisindex | 25,28 ms | 70,59 ms |

Die erste Tippfehler-Suche übernimmt nun den zuvor vorgezogenen Wörterbuch-Aufbau und ist deshalb in dieser Messung langsamer. Auch danach hält der neue Index weniger Speicher. Normale exakte und numerische Anfragen benötigen diese Datenbank nicht. Alle fünf Vergleiche liefern identische exakte und ungefähre Treffer samt Reihenfolge.

Die ebenfalls gemessene exakte Anfrage enthält im Diagnoseprozess Reflection-Aufwand (3.120 Byte nachher). Der direkte Regressionstest misst 488 Byte für dieselbe Suche über 20.000 Titel gegenüber 1.760.488 Byte zuvor. Die zwei neuen Allokationsbudgets scheiterten vor der Änderung (`search-allocation-before.trx`) und bestehen danach.

Diese Werte betreffen den Suchindex mit künstlichen Daten, nicht RAM, Startzeit, FPS oder Eingabe-zu-Rendering der realen Steamy-App. Sie ersetzen die noch offenen umfassenden Leistungsziele nicht.

## Passive Beobachtung des vorhandenen Nutzerprozesses

PID 7340 lief weiterhin aus `portable-library-reference`, also einer älteren Version als dieser Lieferung. Fünf numerische Prozessproben ergaben 88,75–99,64 MiB Working Set und 423,70–425,33 MiB Private Bytes; keine Steamy-Hilfsprozesse wurden dabei beobachtet. Ein zwölfsekündiger numerischer Runtime-Trace meldete rund 200 MB GC-Heap, 260,28 MB GC-Commit, 75,47 MB LOH und keinen GC während der Probe. Aktivität, Fensterzustand und zuvor durchgeführte Aktionen waren nicht kontrolliert. Working Set und Private Bytes sind unterschiedliche Größen; ein kleineres Working Set bestätigt keinen gesunkenen gebundenen Prozessspeicher.

Der Runtime-Trace enthält ausschließlich numerische `System.Runtime`-Counter. Kein Speicherabbild, keine Nachrichten-/Kontoinhalte, kein erzwungener GC, keine UI-Eingabe und kein Neustart im Nutzerprozess. Die Proben sind in `checks/passive-library-reference-processes.json` und `checks/passive-library-reference-runtime.json` gespeichert. Sie bestätigen weder das Leerlaufziel der neuen Version noch eine Ursache einzelner Speicherbereiche.

## Prüfungen und Paket

- `search-allocation-release.trx`: **22/22 Suchindex-Tests**, einschließlich bestehender Trefferregeln, Abbruch, acht gleichzeitiger erster Tippfehler-Anfragen, Reihenfolge und neuer Allokationsbudgets.
- `search-memory-ui-release.trx`: **107/107 ausgewählte fensterlose Regressionen** für Such-/Seiten-Lebensdauer, langsame Metadaten, Cache, Cover, Bild-/Logo-Verarbeitung, virtuelle Liste, Scrollbewegung und Steam-Backend.
- **129 aktuelle gezielte Prüfungen**, keine vollständige interaktive Testreihe. Die kompakte Bibliothek bleibt erhalten. Das danach gewünschte globale Pitch-Black-/Ambient-Theme wird separat umgesetzt.

Portable: `artifacts/performance-refresh/portable-search-memory/Steamy.exe`, eigenständig .NET 10.0.13; gesamten Ordner zusammenlassen. Paket-, Vergleichs- und Quellen-Hashes: `release-search-memory-validation.json`. Kein App-Fenster gestartet oder bedient. Discord bleibt deaktiviert mit unverändert geprüftem Backup. Normaler Leerlauf ≤220 MiB, aktuelle Start-/Navigations-/Renderzeiten, zehn Navigationsrunden und visueller/DPI-Abgleich sind weiterhin nicht als erreicht bestätigt.
