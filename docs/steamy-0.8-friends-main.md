# Freunde als Hauptansicht — 21.10.2026

Freunde und Chat nutzen jetzt den normalen Inhaltsbereich der App. Der separate rechte Einschub samt zusätzlichem Frame ist entfernt. Beide Freunde-Buttons, die Navigation, Einladungsstart und Benachrichtigungen führen in dieselbe Hauptansicht.

Links stehen Kontakte, Suche und Freundschaftsanfragen. Die Unterhaltung nutzt den übrigen Platz. Die Kontaktspalte ist je nach Fensterbreite 240–300 Pixel breit. Party wird bei Bedarf über einen Button geöffnet, statt dauerhaft eine dritte Spalte einzunehmen. Nachrichten, Eingabe und Flächen behalten das dezente Glasdesign. Entwürfe und Leseposition werden weiterhin unabhängig von der Ansicht gespeichert; laufende Anrufe bleiben beim Seitenwechsel aktiv.

Die bisherigen internen SocialDock-Methodennamen bleiben für vorhandene Benachrichtigungs-/Controller-Aufrufer kompatibel. Sie navigieren jetzt zur Hauptseite und erstellen keinen Einschub mehr. Die normale Seitenverwaltung begrenzt weiterhin die Anzahl gehaltener Hauptansichten.

Die Prüfung verwendet isolierte Daten und Discord-/Voice-/Controller-Fixtures. Screenshots sind echte WPF-Renderings mit Testkontakten. Physische Controller und echte Telefonate werden damit nicht bestätigt. Für diese Änderung wird keine neue FPS- oder RAM-Zielerreichung behauptet.

## Prüfung und Lieferung

Portable Version: `E:\Steamy\artifacts\performance-refresh\portable-friends-main\Steamy.exe`.
Den gesamten Ordner behalten. Die vorherige Version bleibt separat erhalten.

- `checks/social-main-windowed.trx`: Hauptansicht bei 980, 1180 und 1920 Pixeln in Dark/Light; kein separater Einschub im Fenster; Kontakte und Chat gleichzeitig sichtbar; Entwürfe über Seitenwechsel erhalten; beide Freunde-Buttons zeigen dieselbe Seite.
- `checks/social-main-discord.trx`: Chat/GIF, Anrufbedienung, Controller-Tastatur, Hauptnavigation, Einladungsstart und Benachrichtigungen bestanden. Die Prüfung mit 150 Nachrichten kontrolliert sichtbares Eingabefeld, begrenzte Zahl aufgebauter Nachrichten und erhaltene Leseposition beim Resize.
- `checks/social-main-full.trx`: **306/306 UI-/Integrationstests**.
- `checks/social-main-core.trx`: **412/412 Kernprüfungen**, zusammen **718 eindeutige Tests**.
- `checks/social-main-details-check.trx`: zusätzliche Spielseiten-Prüfung mit geladenen Preview-Bildern, Scroll-Aufschub und Glas-Kontrast bestanden. Ein früherer zusätzlicher Abnahmelauf (`social-main-acceptance.trx`) meldete noch eine nicht erfüllte Blau-Tönungsprüfung; der Fehler wurde nicht durch Entfernen der Assertion verdeckt. Der separate abschließende Spielseiten-Lauf besteht mit denselben Produktquellen.

Die Prüfungen der alten rechten Einschub-Geometrie wurden durch Hauptansichts-Prüfungen ersetzt. Die vorhandenen Entwurfs-, Benachrichtigungs-, Nachrichten-, Controller- und Anruffunktionen bleiben unter Prüfung. Die Produktänderung baut keine zweite parallele Chatansicht auf.

Portable App-DLL SHA-256: `F10B60D0E03E1016659E532A3A3CBA857CB26CD39271600C8585A9087511C5D0`.

**Abschluss:** Alle oben aufgeführten abschließenden Testläufe bestehen. Zusätzlich besteht `startup-friends-main/startup-report.json` mit **5/5** funktionalen Startprüfungen einschließlich erhaltenen Einstellungen, Bibliothek, Notizen und synthetischen verschlüsselten Credential-Bytes sowie Tracker Exit 0. Sechs echte Aufnahmen der Hauptansicht sind in `screenshots/social-main-windowed/friends-main-capture-provenance.json` gehasht. Der Release-Nachweis mit Quell-/Test-/DLL-Hashes steht in `artifacts/performance-refresh/release-friends-main-validation.json`. Die frameworkabhängige Test-App hat DLL-Hash `6DCEE9E8F5B0A59F43FE9C020C67E677CD0592CCC09AC855EE986B6E8A5A53BF`; die portable App verwendet dieselben Produktquellen mit eigenständiger .NET-10.0.13-Laufzeit.
