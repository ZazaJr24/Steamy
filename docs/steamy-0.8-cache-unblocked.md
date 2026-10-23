# Gespeicherte Bilder warten nicht auf CDN-Downloads

Stand: 23. Oktober 2026. Aktuelles Paket: `artifacts/performance-refresh/portable-cache-unblocked/Steamy.exe`, ganzer Ordner erforderlich. Die portable EXE wurde nicht gestartet.

## Ursache und Korrektur

Die zentrale ImagePipeline prüft zuerst den Arbeitsspeicher und dann auf einem Worker den lokalen Bildcache. Ein fehlendes Bild aus einer reinen CacheOnly-Anfrage wurde bisher in die gemeinsame Bildwarteschlange gelegt. Waren die vier Netzwerk-Worker belegt, konnte selbst dieses negative Ergebnis nicht fertig werden. Der Katalog wartete deshalb auf den ersten fehlenden Wunsch-Cover, bevor er das bereits gespeicherte Ersatzbild überhaupt prüfen konnte.

Öffentliche CacheOnly-Anfragen enden jetzt direkt nach einer negativen Dateiprüfung, ohne Download-Worker zu belegen. Bei privaten MemoryOnly-Bildern kann eine reine CacheOnly-Anfrage nach der Speicherprüfung direkt leer zurückkommen. Vorhandene Speicherbilder werden weiterhin zuerst geprüft und zurückgegeben. Dateibasierte Quellen behalten ihren eigenen Probe-/Dekodierweg. Die Prioritäten und Grenzen für echte Downloads und Dekodierung bleiben unverändert.

Das behebt eine Blockade sowohl beim Durchlaufen vorhandener Cover-Fallbacks als auch bei sonstigen reinen Cache-Abfragen. Es erhöht keine Cachebudgets, startet keine zusätzlichen Netzwerktransfers und verändert die Gestaltung nicht.

## Nachweise

- `checks/cache-miss-before.trx`: drei reproduzierte Fehler. Öffentliche und private CacheOnly-Misses sowie ein gespeichertes Katalog-Ersatzbild warteten jeweils länger als zwei Sekunden auf vier absichtlich blockierte Transfers. Das Bild war im Fallback-Fall schon vorhanden.
- `checks/cache-miss-after.trx`: 31 Bildpipeline-/Katalogtests bestanden. Die neuen Fälle müssen fertig werden, während sämtliche vier Transfers weiterhin blockiert sind; beim Ersatzbild muss dasselbe Bitmap ohne weiteren HTTP-Aufruf erscheinen.
- `checks/cache-unblocked-release.trx`: 258 ausgewählte Tests einschließlich des zusätzlichen MemoryOnly-Cachetreffers. `checks/cache-unblocked-core.trx`: 418 Core-Tests. Insgesamt 676 verschiedene Testidentitäten.
- `checks/cache-unblocked-visual-comparison.json`: 20 von 20 PNGs sind byte-identisch zu den Bibliotheks-Renderings vor dieser Änderung. Das umfasst Dark/Light, 980/1440/1920, ausgeklappten und eingeklappten Kopf, Suchhinweis und Querbild-Fallback. Native Offscreen-Seiten ohne Window-Objekte; keine physischen DPI-/FPS-Aufnahmen.

Die Tests verwenden künstlich blockierte HTTP-Handler und lokale Fixtures; sie sind keine neue Live-CDN-Messung oder Bestätigung der 100-ms-Anzeigegrenze. Insbesondere beweist die Zwei-Sekunden-Timeoutprüfung den Wegfall der Abhängigkeit von den blockierten Transfers, nicht eine konkrete End-to-End-Renderzeit. Die globale Speichervorgabe und aktuelle reale Start-/Scrollmessungen bleiben offen. Die zuvor veröffentlichten Queue-Vergleichswerte gehören zur dort gemessenen Assembly und wurden für dieses Paket nicht erneut erhoben.

Die Vorgabe, Steamy nicht sichtbar zu öffnen, bleibt eingehalten. Eine gesonderte Frage nach einem vollständig verdeckten Messlauf ist noch kein erteiltes Einverständnis; ohne Antwort wurde kein solcher Lauf begonnen. Paket-, Quell-, Test- und Bildhashes stehen in `release-cache-unblocked-validation.json`.
