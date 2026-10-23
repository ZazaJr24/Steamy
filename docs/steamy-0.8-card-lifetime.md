# Verwaiste Karten halten keine Bibliotheksseiten mehr fest

23. Oktober 2026. Aktuelle Vorgabe: keine weiteren Steamy-Fenster öffnen. Die zuvor ausdrücklich erlaubten sichtbaren Messläufe sind beendet. Nach dieser Korrektur wurden nur fensterlose Tests, Konsolenanalyse und Builds ausgeführt.

## Befund aus der zuvor gestarteten Release-App

Fünf kontrollierte Läufe der portable-clean-chrome mit je 1.000 lokalen Testspielen und zehn Navigationsrunden bestätigen rund 17,7–18,5 MiB zusätzlich verwalteten Speicher. Die Navigation assoziiert drei Seiten, aber 13 besuchte Seiten sind noch erreichbar. Der angepasste Messablauf berücksichtigt die inzwischen entfernte Discord-Integration: Bibliothekssuche als Eingabeprobe, fünf Hauptseiten, keine erfundenen Chat-Erfolge. Die Profile enthalten nun AppearanceDesignVersion=1 und schwarzen Hintergrund; die vorherigen Pilotprofile hatten die einmalige Light-Migration ausgelöst.

Die separate Diagnose zählt zehn LibraryPage-Instanzen. Die bereits vorhandene Heap-Aufnahme zeigt für verlassene Instanzen einen starken Pfad vom globalen MotionPreferences-Ereignis über den CardMotion-Callback zu einer Kachel und über deren Accessibility-/Kontextmenüstruktur zur Bibliotheksseite. Das ist eine konkrete starke Referenz, kein aus Private Bytes abgeleiteter Leak-Verdacht. Der Diagnoseablauf hat zusätzlich selbst die erste Seite über das Eingabefeld festgehalten; diese Referenz wird im künftigen Beobachter ausdrücklich freigegeben.

## Korrektur

CardMotion empfängt globale Einstellungen jetzt über den WPF WeakEventManager. Das aktive Element besitzt seinen Callback weiterhin selbst. Eine verwaiste Kachel kann freigegeben werden, auch wenn beim Recycling eines Template-Teils kein Unloaded mehr eintrifft. Reguläres Unloaded meldet weiterhin ab und setzt Transformationen und Animationen zurück. Sichtbare, gehaltene Kacheln reagieren weiterhin auf reduzierte Effekte und erneutes Laden.

## Prüfung und Grenzen

- card-lifetime-before.trx: der neue Collectability-Test scheitert am vorherigen Code. Der zweite Test scheitert anschließend an genau dessen verbliebenem Callback auf dem bereits beendeten STA-Thread; kein unabhängiger zweiter Produktfehler wird daraus abgeleitet.
- card-lifetime-after.trx: beide Tests bestehen, ohne Application-/Window-Erzeugung oder Show-Aufruf.
- card-lifetime-regression.trx: 239 ausgewählte fensterlose UI-/Modelltests bestanden.
- card-lifetime-core.trx: alle 418 Core-Tests bestanden. Zusammen 657 unterschiedliche aktuelle Tests; die zwei Einzeltests sind bereits in den 239 enthalten.
- card-lifetime-root-analysis.txt dokumentiert den starken Produktpfad und die zusätzliche Referenz des alten Beobachters. card-lifetime-baseline-summary.json fasst ausschließlich die Vorher-Messung zusammen.

Die Vorher-Messung nutzt die unveränderte eingefrorene Hook-Assembly unter hooks/v9-benchmark. Ihre fünf HTTP-kontrollierten Läufe sind vollständig; der separate Heap-Diagnoselauf mit fehlendem Warm-Bericht ist kein erfolgreicher Leistungslauf. Der Beobachter wurde danach um Seitentypen und die Freigabe seines Eingabefelds ergänzt. Neue Messungen müssten beide Vergleichsversionen mit derselben neuen Hook-Assembly starten.

Die neue Portable wurde nicht geöffnet. Eine gemessene Reduktion des gesamten App-RAM oder Erfüllung von 220 MiB, 15 MiB Wachstum, Start- oder Framezeitzielen wird nicht behauptet. Die neue obere Leiste bleibt erhalten; ihre Aufnahmen stammen von portable-clean-chrome, nicht von einem nachträglich gestarteten Fenster dieser Version.

Portable: artifacts/performance-refresh/portable-card-lifetime/Steamy.exe. Den gesamten Ordner zusammenlassen. Discord bleibt deaktiviert, das Backup erhalten.
