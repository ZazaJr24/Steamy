# Spielseite: Hintergrundarbeit und sichere Entwürfe — 22. Oktober 2026

Lokale Datei-Prüfungen, Steam-Profil-Ermittlung und das Entschlüsseln gespeicherter Zugangsdaten können vor dem ersten `await` einer asynchronen Methode laufen. Ein bereits erfülltes Await wechselt den Thread nicht automatisch. Auf der Spielseite konnte solche Arbeit deshalb noch den Dispatcher beanspruchen.

Die erste Store-Metadaten-Abfrage, optionale News-/Review-Starts und die Achievement-Abfrage beginnen nun im Hintergrund. Der schnelle Overview-Speichercache bleibt direkt verfügbar. Spielzeit-, Aktivitäts- und Notiz-Lesezugriffe der Spielseite starten ebenfalls außerhalb des Dispatchers. Die Oberfläche übernimmt die Ergebnisse weiter über ihre normalen Await-Fortsetzungen. Abbruch beendet das Warten auf diese lokalen Leseaufgaben; verspätete Fehler eines nicht kooperativen Anbieters werden beobachtet.

## Spielzeit beim Zurückkehren

Der bisherige Schutz gegen doppelte Spielzeit-Abfragen verwendete nur die Auswahlrevision des Spiels. Beim Weggehen und Zurückkehren zum selben Spiel konnte eine alte ausstehende Abfrage dadurch den neuen Abruf unterdrücken. Sie las außerdem nach ihren Awaits erneut den inzwischen ausgetauschten Seiten-Token.

Jetzt gehört jede Abfrage zur konkreten Aktivierung und verwendet durchgehend deren erfassten Token. Eine neue Aktivierung darf neue Werte lesen, auch wenn der alte Anbieter noch nicht fertig ist. Ein altes Ergebnis kann die neue Anzeige nicht ersetzen. Spielzeit und letzter Start bleiben neben Play sichtbar.

## Notizen und Startoptionen

Asynchrones Lesen darf keine neueren Eingaben ersetzen. Deshalb wird der Bearbeitungsstand vor dem Lesen erfasst; inzwischen geänderte oder nach einem Fehler noch ungespeicherte Entwürfe gewinnen gegen einen alten Dateistand. Das Einsetzen geladener Werte wird nicht als neuer Benutzer-Edit behandelt und löst keine unnötige Speicherung aus.

Schreibvorgänge starten ebenfalls im Hintergrund, behalten aber ihren erfassten Spiel-/Notiz-/Optionsstand und werden beim Weggehen nicht abgebrochen. Die bestehende serielle Speicherung und die Prüfung der Auswahl-/Bearbeitungsrevision vor einer Statusänderung bleiben erhalten. Der Schutz für fehlgeschlagene Speicherungen bezieht sich hier auf das Wiederöffnen desselben noch im Modell gehaltenen Spiels; eine allgemeine ausfallsichere Wiederherstellung beliebiger ungespeicherter Entwürfe nach Prozessende wird nicht behauptet.

## Nachweise

- `checks/game-read-lifetime-before.trx`: **sechs reproduzierte Fehler vor der Korrektur**. Betroffen waren die Aufrufer-Threads von Metadaten, Achievement-Vorbereitung, Spielzeit, Notiz-Lesen/-Schreiben sowie die unterdrückte neue Spielzeit-Abfrage bei derselben Spielauswahl.
- `checks/game-preference-reload-before.trx`: **drei reproduzierte Notiz-Fehler**: ein verspäteter Dateistand ersetzte eine neuere bereits gespeicherte Eingabe; reine Reloads wurden erneut gespeichert; eine fehlgeschlagene Speicherung wurde beim Wiederöffnen durch den Dateistand ersetzt.
- Alle neun Fälle bestehen jetzt. Ein weiterer Test blockiert den synchronen Beginn eines Notiz-Anbieters absichtlich und bestätigt, dass ein fensterloser STA-Dispatcher währenddessen weiterhin einen Callback mit Eingabepriorität ausführt. Das ist eine gezielte Reaktionsprüfung, kein echter Input-bis-Rendering-/FPS-Test.
- `checks/game-read-lifetime-release.trx`: **152/152 ausgewählte fensterlose UI-/Modellprüfungen**, einschließlich aller neuen Fälle, vorhandener Bild-/Such-/Scroll-/Material- und Lebensdauerprüfungen.
- `checks/game-read-core-all-release.trx`: **418/418 Tests der vollständigen plattformunabhängigen Core-Testreihe**. Darin sind die bisherigen 22 Suchindex-Tests bereits enthalten. Der Windows-Junction-Test startet seinen CLI-Helfer ausdrücklich ohne Fenster.

Zusammen **570 Prüfungen**, ohne doppelte Zählung der Suchindex-Fälle. Die vollständige interaktive WPF-Testreihe wurde wegen der weiterhin geltenden Vorgabe, keine App-Fenster zu öffnen, nicht ausgeführt. Neu erzeugte Material-/Hintergrundpixel werden zusätzlich aus der veröffentlichten Assembly fensterlos geprüft; die fünf gepaarten Zeitmessungen des vorherigen Render-Releases bleiben historische, klar zugeordnete Messungen des unveränderten Zeichenpfads.

## Lieferung und Grenzen

Portable: `artifacts/performance-refresh/portable-game-read-lifetime/Steamy.exe`, eigenständig mit .NET 10.0.13; den gesamten Ordner zusammenlassen. Die vorherigen Portables werden nicht überschrieben. Weißes Glasdesign, dezentes Ambient, Basisspiel-Filter und die Spielseiten-Reihenfolge **Game details → Preview → Beschreibung** bleiben erhalten. Der Bildbetrachter bleibt Bildrahmen und X. Discord bleibt deaktiviert mit unverändert geprüftem Backup.

`release-game-read-lifetime-validation.json` enthält Paket-, Quellen- und Prüfbericht-Hashes. Es wurde keine `Steamy.exe`, kein Steam und kein sichtbares Diagnosefenster gestartet. Reale Start-/Navigationszeiten, Scroll-FPS, Eingabe-bis-Rendering, aktueller normaler Leerlaufverbrauch samt Hilfsprozessen und die vollständige visuelle Abnahme sind weiterhin offen. Das Gesamtziel ist damit noch nicht als abgeschlossen belegt.
