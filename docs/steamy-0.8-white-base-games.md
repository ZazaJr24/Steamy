# Weißes Design, Basisspiele und weniger blockierende Arbeit — 22. Oktober 2026

Die neueste Vorgabe ersetzt den bisherigen schwarzen Standard durch Weiß. Das anfängliche App-Theme und neue Einstellungen verwenden Light. Ältere Profile ohne `AppearanceDesignVersion` übernehmen Light und einen weißen Hintergrund einmal beim Laden; Sprache, Anmeldedaten und übrige Einstellungen bleiben erhalten. Eine anschließend gespeicherte Theme-/Hintergrundwahl wird bei späteren Starts respektiert. Es wurde keine laufende Nutzer-App umgestellt oder geöffnet.

## Darstellung

Die gemeinsamen Grundflächen sind weiß. Die zuvor lokal erzwungenen dunklen Flächen und weißen Texte in Bibliothek, Downloads, Spielseite und Installationsformular sind im Light-Theme korrigiert. Home blendet sein Banner in Weiß aus. Die Leiste verwendet weißes Glas mit dunklen Beschriftungen; die dafür bisher verwendete synchron gerenderte Bitmap-Abtastung entfällt in beiden Themes. Die echten Banner und Cover bleiben farbig. Ein kleiner eingefrorener Zeichenpinsel führt deren Farben als sehr schwaches Ambient über Weiß fort; dafür läuft kein zusätzlicher Blur oder Animationstimer.

Die Bibliothek heißt **Games** beziehungsweise **Installed games**, zeigt kleinere Spieltitel mit maximal zwei Zeilen und nutzt explizite Textformatierung für die Anzeige. Die Scrollfläche wird unterhalb der angehefteten Filterleiste beschnitten, damit Kartenbeschriftungen nicht durch den Spalt oberhalb der Filter laufen. Die Clip-Geometrie wird wiederverwendet. Die Zahl oben zählt die gefilterten Basisspiele, statt jede der rund 262.000 Steam-App-IDs als Spiel auszugeben. Einige neue statische Beschriftungen sind auch direkt auf Deutsch enthalten. Die Spielseite behält Banner, langen Fade, kompakte Play-/Steam-Aktionen, Spielzeit und letzten Start sowie **Game details → Preview → Beschreibung**. Dark und hoher Kontrast bleiben wählbar.

## Basisspiele statt DLCs

Die Galerie und ihre Suchvorschläge zeigen ausschließlich Einträge vom Typ `Game`. DLCs, Software und unbekannte Typen erscheinen nicht als Basisspiel. Der DLC-/App-Typ-Schalter wurde aus der Galerie entfernt; auch ältere interne Typ-Auswahlen können ihn nicht wieder aktivieren. Untypisierte Hubcap-Einträge und unbekannte Ryuu-Typen werden nicht mehr pauschal als Game eingestuft.

Die öffentliche Steam-App-ID-Liste enthält selbst keine Typinformation. Bei einer Titelsuche werden deshalb höchstens 24 passende unbekannte Einträge über ihre Metadaten geprüft und anschließend ausschließlich Basisspiele ergänzt. Die bekannte Galerie und lokale Suche warten währenddessen nicht auf diese optionalen Anfragen. Kann der Typ offline oder wegen eines Serverfehlers nicht geprüft werden, bleibt der Eintrag verborgen; das ist kein vollständiger Katalog aller existierenden Basisspiele. Eine engere Suche beziehungsweise App-ID kann weitere Einträge prüfen. Es werden weder massenhaft Details aller App-IDs heruntergeladen noch Typen anhand von Namens-Heuristiken erfunden. Korrigiert ein Anbieter einen angezeigten Typ später zu DLC, werden Karte, Suchvorschlag und Zählung nach der laufenden Metadatenrunde aktualisiert.

## Reproduzierte Hänger und Hintergrundarbeit

Die Entwickler-/Genre-Auswahl der Spielseite lief nach einem bereits fertigen Katalog-Await auf dem Aufrufer-/UI-Thread. Sie erzeugte ein komplettes Kandidatenarray und Genre-Arrays/HashSets pro geprüftem Spiel. Die Auswahl läuft jetzt in einem Worker, verwendet höchstens zwei kurze Ergebnislisten und vergleicht Genreabschnitte ohne Allokation pro Kandidat. Firmen-Namen mit Komma, ganze Genre-Tokens, Reihenfolge und je sechs Ergebnisse bleiben erhalten. Die Schleife prüft Abbruch während der Arbeit und unmittelbar vor Veröffentlichung.

Zwei neue Regressionen scheiterten vorher (`checks/related-selection-before.trx`): die Arbeit lief auf dem Aufrufer-Thread, und ein Abbruch während der Schleife verhinderte die spätere Veröffentlichung nicht. Beide bestehen jetzt. Ein direkter Test mit 20.000 künstlichen Kandidaten misst **392 Byte** für die Auswahl; das Budget liegt bei 8 KiB. Diese Zahl betrifft nur den Auswahlalgorithmus, nicht den Gesamtspeicher der App.

Galerie, Typprüfung und Bibliotheks-Empfehlungen teilen nun eine Metadaten-Grenze von vier aktiven logischen Anfragen. Nach dem Warten auf einen Platz wird Abbruch erneut geprüft. Empfehlungen stellen zudem nur vier Profile gleichzeitig an, statt bis zu achtzig Aufgaben vor eine neue Suche zu hängen. Abgebrochene Ansichten warten nicht auf unkooperative Anbieter weiter; späte Fehler werden beobachtet. Ein tatsächlich ignorierender externer Anbieter kann seine bereits gestartete Anfrage trotzdem erst später beenden.

## Prüfung und Lieferung

- `checks/white-base-games-release.trx`: **129/129 ausgewählte fensterlose Prüfungen**, darunter Weiß-/Schwarz-Materialien, Textkontrast, Ambient-Pixel, Einstellungsumstellung, Basisspiel-/DLC-Filter, begrenzte Typprüfung, nachträgliche DLC-Korrektur, Empfehlungsauswahl, Lebensdauer, Bildpipeline, virtuelle Liste und Scrollbewegung.
- `checks/white-search-core-release.trx`: **22/22 aktuelle Suchindex-Prüfungen**.
- Zusammen **151 gezielte Prüfungen**. Keine vollständige interaktive Abnahme. Die Materialprüfungen rendern nur kleine Speicherbitmaps ohne `Application` oder `Window`. Bestehende interaktive Erwartungen wurden an die neue Typ-/Farbpolitik angepasst, aber nicht ausgeführt.

Portable: `artifacts/performance-refresh/portable-white-base-games/Steamy.exe`. Den gesamten Ordner zusammenlassen; .NET 10.0.13 ist enthalten. Die vorherige portable Version wird nicht überschrieben. Datei-, Quellen- und Prüfbericht-Hashes stehen in `release-white-base-games-validation.json`. Discord bleibt deaktiviert; das vorhandene Backup wird geprüft.

Wie angefordert wurde kein Steamy-/Steam-Fenster gestartet oder bedient. Eine reale Prüfung des gesamten Weiß-Designs bei allen Größen/DPI, aktueller Start-/Renderzeiten, FPS und des normalen Leerlaufspeichers fehlt damit weiterhin. Die konkreten reproduzierten Ursachen wurden behoben; die Aussage, dass alle Hänger verschwunden seien oder das Gesamtziel von höchstens 220 MiB erreicht sei, ist noch nicht belegt.
