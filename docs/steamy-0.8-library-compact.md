# Kompakter Bibliothekskopf und Filter — 22. Oktober 2026

Der Screenshot der laufenden `portable-library-reference`-Version zeigt zu viel Abstand zwischen Navigation und Bibliotheksinhalt. Die Bibliothek wird deshalb gegenüber dieser Version wie folgt verdichtet:

- Überschrift: oberer Abstand 62 → 4 DIP, Höhe 82 → 64 DIP, Schrift 56 → 48 DIP (bei schmaleren Fenstern 40 DIP).
- Filterleiste: oberer Abstand 176 → 88 DIP. Suche, Ansichts-Auswahl, Genre-Pills, Filterknopf und Refresh erhalten abgestimmte Rundungen und eine Außenhöhe von 44 DIP. Die Filterleiste bleibt nach dem Scrollen weiterhin 16 DIP unter dem Seitenanfang stehen.
- Erste Cover-Reihe: 300 → 184 DIP unter dem Seitenanfang, also 116 DIP weiter oben. Die eigentlichen Covergrößen, Beschriftungen und Reihenabstände bleiben erhalten.
- Der ausgewählte Filter ist durch eine hellere Fläche und Kontur erkennbar. Der bisherige identische Hintergrund für Auswahl und umgebende Fläche entfällt. Die Auswahl berücksichtigt Systemfarben bei hohem Kontrast.
- Der erweiterte Filterknopf zeigt Icon und „Filters“. Genre-Pfeile erscheinen bei horizontalem Überlauf; sie blättern die Genre-Leiste. Das Mausrad über einer überlaufenden Genre-Leiste bewegt diese horizontal.

Die ursprüngliche `FilterFlyout`-Darstellung anderer Seiten bleibt über ihre unveränderten Standardwerte erhalten. Nur die Bibliothek setzt den neuen optionalen Trigger-Text und Trigger-Stil. Ein bei der letzten XAML-Bearbeitung entstandener Zeichenkodierungsfehler bei Nicht-ASCII-Zeichen wurde ebenfalls korrigiert; die Datei wird explizit als UTF-8 gelesen und geschrieben.

## Prüfung

`library-compact-release.trx`: **92/92 ausgewählte fensterlose Regressionstests bestanden**. Die Geometrie-Prüfungen für 980, 1440 und 1920 Pixel verwenden den neuen Kopfbereich und bestätigen weiterhin die begrenzte Anzahl erzeugter Karten, getrennte Caption-/Reihenabstände und den Erhalt von Containern/Scrollposition beim Nachladen. XAML wird im Release-Build kompiliert. Das neue Aussehen der Filter ist dadurch nicht als interaktiver oder visueller Vergleichstest abgenommen.

Keine Steamy-/Steam-Instanz wurde durch den Agenten gestartet, navigiert oder beendet. Die vom Nutzer gestartete vorherige Version wird nicht überschrieben. Ein sichtbarer Abgleich der neuen EXE bleibt offen. Die umfassenden Start-, RAM-, Navigations- und Eingabe-zu-Rendering-Ziele sind weiterhin nicht als erreicht bestätigt.

## Lieferung

`artifacts/performance-refresh/portable-library-compact/Steamy.exe`, mit eigenständiger .NET-10.0.13-Laufzeit. Den gesamten Ordner zusammenlassen. Paket-, Quellen- und Bericht-Prüfsummen: `release-library-compact-validation.json`. Discord bleibt deaktiviert; das vorhandene Backup bleibt unverändert erhalten. BetterSteamTools hinter dem + und die Reihenfolge der einzelnen Spielseite bleiben erhalten.
