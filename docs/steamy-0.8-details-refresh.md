# Spielseite und Freunde — Referenz-Layout vom 16.10.2026

Die aktuelle Priorität des Nutzers ist die Anordnung aus den angehängten Spiel- und Freunde-Referenzen. Nach der Ergänzung vom 16.10.2026 bleibt oben ein großes Spielebanner; darunter folgt die Anordnung aus den Referenzbildern. Eine zusätzliche breite Haupt-Aktionsleiste entfällt.

- Responsives Spielebanner mit weichem Übergang in den Ambient-Hintergrund der gesamten Seite. Darunter kompakte zentrierte Identität mit quadratischem Artwork, Titel, Beschreibung, Steam-Rezensionen und einer weißen primären Aktion.
- Die obere Glasnavigation erhält ihre Schriftfarbe aus dem tatsächlich zusammengesetzten Hintergrund inklusive Auswahlfläche. Beim Scrollen schützt ein weicher Ambient-Saum die Leiste vor durchscheinendem Seitentext.
- Vier vollständig sichtbare Vorschauen; Anklicken öffnet das echte Bild.
- Ein durchgehender, weichgezeichneter Bildhintergrund aus dem tatsächlichen Banner. Mehrere Farben sowie helle und dunkle Bildregionen bleiben sowohl horizontal als auch vertikal erhalten. Ein begrenzter 320×320-Pixel-Cache ersetzt die vorherige einfarbige Flächentönung. Weißer Text erhält einen ausreichend dunklen Hintergrund; hoher Kontrast und deaktivierte Ambient-Effekte behalten ihren eigenen Modus.
- Beschreibungskarte, horizontale Erfolgs-Vorschau, Details, Entwickler-Link sowie Entwickler- und Genre-Empfehlungen, wenn entsprechende bekannte Steam-Katalogmetadaten vorliegen.
- Entwickler-Empfehlungen vergleichen vollständige Firmennamen; Kommas in Firmennamen erzeugen keine falschen Übereinstimmungen. Bilder der ausgewählten Empfehlungen werden gezielt geladen. Ein Wechsel zu einer Empfehlung aktualisiert die bereits geöffnete Seite und berücksichtigt vorhandene Installationen.
- Rezensionen stammen aus der öffentlichen Steam-Review-Zusammenfassung. Prozentwert und Stimmenzahl werden nicht in erfundene Sterne umgerechnet. Bei fehlenden Daten erscheint kein erfundener Wert.
- Notizen, Startoptionen, vollständige Erfolge und Spielzeit bleiben unter „Your game tools“ erreichbar.
- Neue Freunde-Übersicht mit vier Spielen aus der eigenen installierten Bibliothek und Einstieg in Discord/Party. Gameplay-Vorschauen werden bei vorhandenen Steam-Screenshots nachgeladen; die Bilddaten der übrigen App werden dabei nicht überschrieben. Die Karten behaupten keinen ungeprüften Multiplayer-Support.
- Downloads ist ein kleines separates Symbol rechts mit Aktivitätsanzeige und Queue-Tooltip. Freunde-Übersicht und aufklappbarer Chat sind getrennte Bedienelemente.
- Physische Scrollbereiche verwenden denselben SmoothScroll-Mechanismus; die Home-Kurzleiste scrollt horizontal über denselben Animationspfad. Logisch virtualisierte Listen behalten ihre Virtualisierung.
- Bibliothekscover werden mit 360 px statt bis zu 2048 px dekodiert, Header mit 600 px und Hero-Artwork mit 1600 px. Ein Cover mit 600×900 px belegt dadurch für seine dekodierten Pixel 36 % der vorherigen Fläche; dies ist keine Behauptung über den gesamten Prozess-RAM.

Noch aus den vorherigen Wünschen offen: vollständige Controller-Designrunde, angepinnte Kontakte, Game-Clipping der letzten 30–60 Sekunden und echte Tippindikatoren. Das aktuell eingebundene offizielle Discord Social SDK stellt keine öffentlichen Typing-Callbacks bereit; es werden keine künstlichen Tippereignisse oder versteckten Chat-Nachrichten erzeugt. Diese Punkte wurden mit „erstmal“ zugunsten dieser Designrunde zurückgestellt.

Verifikation und Paket: `artifacts/steamy-0.8/details-refresh/`.


Verifikation am 16.10.2026:

- Vollständige UI-/Modell-Suite: 263 Tests bestanden (`checks/full-final.trx`).
- Zusätzlicher echter WPF-Test für die Spielseite einschließlich Banner, Ambient, Glas-Kontrast, Scrollen, Aktionen und Fenstergrößen bestanden (`checks/details-banner-final.trx`).
- Freunde-Übersicht, Downloads und Chat-Dock in beiden Themes bei den Fenstergrößen 980, 1180 und 1920 geprüft (`checks/windowed-final.trx`); Testdaten bleiben offline.
- Fünf Start-Szenarien bestanden, einschließlich Upgrade/Neustart, Erststart und PlayTimeTracker. Notizen, Bibliothek und synthetisch verschlüsselte Zugangsdaten blieben erhalten (`startup/startup-report.json`). Diese Messungen liefen während des Paketbaus und sind kein Startzeit-Benchmark.
- Online-Setup: 32 Selbstprüfungen; vollständiges Setup: 36 Selbstprüfungen bestanden. Keine echte Installation und keine Treiberinstallation durch die Selbstprüfungen. ZIP-Hashes stimmen mit dem erzeugten lokalen Paketmanifest überein.

Aktuelle Dateien: `release/portable/Steamy.exe` und `release/Steamy-FullSetup.exe` unter dem oben genannten Artefaktordner. Die Ganzseitenaufnahme `ui/game-details-full-page.png` stammt aus der kompilierten WPF-Oberfläche mit ausdrücklich markierten Testdaten; sie ist kein generiertes Designbild. Der echte Programmstart wurde mit getrennten Testprofilen geprüft. Es wurde kein GitHub-Release veröffentlicht und keine laufende Nutzerinstanz ersetzt.

Die anschließende Ambient- und Button-Korrektur mit Spielzeit/zuletzt gespielt ist unter [Ambient-Follow-up](steamy-0.8-ambient-refresh.md) dokumentiert. Deren aktueller Build liegt separat in `artifacts/steamy-0.8/ambient-refresh/`.
