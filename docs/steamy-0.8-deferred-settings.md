# Einstellungen bei Bedarf aufbauen, Hover ohne festgehaltene Karten

23. Oktober 2026. Keine weiteren App-Fenster wurden geöffnet. Die vorhandenen Steamy-Prozesse wurden nicht gesteuert oder beendet.

## Änderungen

Die 13 Einstellungsabschnitte liegen jetzt in kompilierten DataTemplates. Beim ersten Aufruf entstehen nur die zwei Abschnitte der gewählten Kategorie „General“. Andere Kategorien werden beim Auswählen oder bei einem passenden Suchergebnis aufgebaut und innerhalb der Seite anschließend wiederverwendet. Die vorhandene Drei-Seiten-Grenze bleibt erhalten.

Die Suche verwendet unveränderliche Beschreibungen der Abschnitte, ohne deren Eingabefelder aufzubauen. Die Metadaten enthalten keine eingegebenen Werte oder Zugangsdaten. Bei neuen statischen Beschriftungen müssen die SearchLabels des jeweiligen Abschnitts mitgepflegt werden. Die ursprünglichen Namen tatsächlich erzeugter Steuerelemente bleiben im Namensbereich der Seite verfügbar. Zugangsdatenfelder werden nach Speicherung weiterhin geleert; noch nicht besuchte Felder müssen dafür nicht konstruiert werden. Suchtext und gewählte Kategorie bleiben beim erneuten Erstellen erhalten. Der deaktivierte Discord-Abschnitt bleibt unaufgebaut.

Zusätzlich verwendet DownloadHoverGlow denselben schwachen WPF-Ereignisweg wie CardMotion. Ein neuer fensterloser Regressionstest reproduziert vorher das Festhalten einer verlassenen Karte. Wiederholtes Loaded meldet den früheren Besitzer zunächst ab; Unloaded und Deaktivierung lösen die Verbindung und Animationsuhr. Aktivieren auf einem bereits geladenen Element wird sofort berücksichtigt.

## Prüfung

- deferred-settings-release.trx: 242 ausgewählte Tests, einschließlich der vier Hover-/Karten-Lebensdauertests und eines Settings-Integrationstests ohne Window.
- deferred-settings-core.trx: alle 418 Core-Tests. Zusammen 660 unterschiedliche aktuelle Tests. Die separaten Settings-Vorher-/Nachherberichte werden nicht noch einmal dazugezählt.
- Der Settings-Test prüft zunächst nicht erzeugte Kategorien, bedarfsgesteuerte Treffer bei „resume“, Zwei-Wege-Bindung eines Schalters, alle sechs exportierten Zugangsdatenfelder, Speicherung mit reinem Speicher-Testdienst, Leeren der Felder, verstecktes Discord, keine Treffer und Wiederherstellen der Navigationseingaben.
- Je sechs Kategorien in Dark und Light wurden mit den kompilierten Anwendungsressourcen ohne Window in RenderTargetBitmap gezeichnet. Alle zwölf 1440×800-RGBA-Ergebnisse sind pixelgleich zum vorherigen Aufbau. Keine Behauptung über native Monitor-DPI oder das vollständige App-Fenster.
- Die fünf wiederholten Konstruktoraufrufe im jeweiligen Testprozess ergeben beobachtet einen Median von rund 96,7 ms vorher und 40,4 ms nachher, rund 58 % weniger. Die medianen Allokationen dieses Threads sinken von rund 2,51 auf 2,21 Millionen Byte. Das sind keine vollständigen Seitenwechsel-/RAM-Messungen, keine fünf separaten App-Starts und keine zugesicherte Beschleunigung jedes Aufrufs. Andere Maschinenaktivität wurde nicht isoliert; der Nachherprozess lief zeitweise parallel zu einem Publish-Build. Rohwerte und Pixel-Hashes stehen in checks/settings-offscreen-comparison.json.

Der Vorher-Vergleich verwendete die gesicherte ursprüngliche SettingsPage.xaml und den zurückgesetzten bisherigen Filter-/Feldzugriff. Danach wurden beide aktuellen Dateien wiederhergestellt und mit erneuertem Änderungszeitstempel neu kompiliert. Der abschließende Integrationstest prüft explizit die 13 verzögerten Abschnitte; ein versehentlich weiterverwendetes Vorher-Binary würde scheitern.

Portable: artifacts/performance-refresh/portable-deferred-settings/Steamy.exe. Den gesamten Ordner zusammenlassen. Runtime, Helfer, Berichte, Quellen und Discord-Backup werden in release-deferred-settings-validation.json erfasst. Die veröffentlichte EXE wurde nicht gestartet. Die vollständigen RAM-, Start-, Framezeit- und Navigationsziele bleiben offen.
