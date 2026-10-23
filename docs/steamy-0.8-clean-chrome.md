# Ruhigere obere Glasleiste — 23. Oktober 2026

Launcher und Navigation haben dieselbe 36-DIP-Höhe, einen gemeinsamen neutralen Glasgrund und eine dünne, zurückhaltende Kontur. Der starke Glanzverlauf und die zusätzliche Rundfassung am Menüknopf entfallen. Die Navigation ist bei deaktiviertem Discord maximal 456 DIP breit. Beschriftungen verwenden 13 DIP; die vier Hauptoptionen bleiben gleich breit. Die vorhandene Hintergrundabtastung bleibt lokal auf die Glasflächen begrenzt, hinter den unverwischten Texten.

Die aktive Route bleibt beim Überfahren und Fokussieren anderer Optionen markiert. Aktivieren eines Tabs bewegt die Auswahl mit 140 ms Cubic-Ease-Out, ohne Überschwingen. Die bisherige Pointer-Funktion anderer PillMotion-Verwendungen bleibt standardmäßig aktiv. Weniger Bewegung wird weiterhin respektiert. Der linke Menüknopf behält den von NavigationView benötigten Template-Namen und öffnet/schließt den vorhandenen Drawer.

## Prüfung

Nach ausdrücklicher Freigabe des Nutzers wurde ein echtes MainWindow geöffnet, mit den kompilierten Anwendungsressourcen und isolierten Offline-Testdiensten. STEAMY_DATA_DIR zeigte auf artifacts/performance-refresh/checks/chrome-after-profile; echte Nutzerdaten, Steam und Tool-Installationen wurden nicht verwendet.

- checks/chrome-before/chrome-dark-1440.png: Aufnahme vor der Korrektur.
- checks/chrome-after/: aktuelle Dark-/Light-Aufnahmen bei 980, 1440 und 1920 DIP sowie Home und geöffnetem Tools-Menü. Die 2x-Aufnahmen sind zusätzliche Renderauflösungen, kein Test verschiedener physischer Monitor-DPI.
- checks/chrome-after.trx: ein echter Fenster-Integrationstest, mit sechs Größen-/Theme-Kombinationen, Ausrichtung und Kollisionsprüfung, Route via Click, stabiler Auswahl bei Hover/Fokus, Tools-Menü und Wiederherstellung, Drawer-Toggle sowie High-Contrast-Ressourcen und Rückkehr zum normalen Design.
- checks/chrome-regression.trx: 237 ausgewählte UI-/Modelltests bestanden.
- checks/chrome-core.trx: alle 418 Core-Tests bestanden.

Die Aufnahmen von Dark 1440, Home Dark 1440 und Light 980 wurden visuell geprüft. Die portable Release-EXE wurde gebaut und das Paket auf Runtime, Helfer, Quellen, Discord-Deaktivierung und Backup geprüft. Das Produktions-Startup der EXE wurde nicht separat gestartet. Die vollständigen Leistungsziele für RAM, Start, Scrollen und Eingabezeit bleiben offen; diese Layoutprüfung ersetzt deren Messung nicht.

Portable: artifacts/performance-refresh/portable-clean-chrome/Steamy.exe. Den gesamten Ordner zusammenlassen. Prüfmanifest: release-clean-chrome-validation.json.
