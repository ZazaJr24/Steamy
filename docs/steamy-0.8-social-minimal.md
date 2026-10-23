# Freunde und Scrollen — 21.10.2026

Neue portable Version: `E:\Steamy\artifacts\performance-refresh\portable-social-minimal\Steamy.exe`.
Den gesamten Ordner behalten; die vorherige Version bleibt separat erhalten.

## Änderungen

- Freunde: kompakte Kontaktzeilen mit Avatar, Status und direktem Einstieg in den Chat. Ein zurückhaltendes Glaspanel ersetzt große Illustrationen und Spielekacheln. Party und Nachrichten bleiben direkt erreichbar. Im schmalen Fenster stehen Kontakte untereinander, bei mehr Platz in zwei Spalten.
- Spiele zum gemeinsamen Spielen sind standardmäßig eingeklappt. Cover und zusätzliche Vorschaubilder werden erst beim Aufklappen angefordert.
- Rechtes Freunde-/Chatpanel: weniger verschachtelte Rahmen, kleinere Kontaktabstände, einheitliche runde Nachrichten, dezente violette eigene Nachrichten und ein kompakteres Eingabefeld. Discord-Verbindung, Anrufe, GIFs, Party, Entwürfe und Tastaturbedienung bleiben erhalten.
- Scrollen: synchrone Kontrastaufnahmen der Glasleiste werden während fortlaufender Scrollbewegung zurückgestellt. 140 ms nach der letzten Bewegung wird die endgültige Position abgetastet. Reine Größen-/Inhaltsänderungen setzen diesen Scrollaufschub nicht in Gang. Ausstehende Aufnahmen werden bei weiterer Bewegung abgebrochen; beim Schließen wird der Timer gestoppt.

Die Spielseite bleibt Game details → Preview → Beschreibung. Der Betrachter bleibt Bildrahmen und X.

## Prüfhinweise

Der neue Regressionstest bewegt die wirkliche WPF-Spielseite in 16 Schritten und prüft, dass der Zeitpunkt der letzten Kontrastaufnahme währenddessen unverändert bleibt und nach dem Stillstand weiterläuft. Er scheitert mit dem vorherigen 80-ms-Takt (`checks/social-minimal-scroll-before.trx`) und besteht mit dem Scrollaufschub (`checks/social-scroll-diagnostic.trx`). Eine Prüfung der Kontrastwerte bleibt zusätzlich bestehen.

Die Oberflächenprüfungen verwenden isolierte Daten und Discord-Fixtures. Sie ersetzen keinen echten Anruf oder einen Hardwaretest. Aufnahmen sind echte WPF-Renderings mit öffentlichen Testbildern; die Freunde darin sind Testkontakte.

Für diese Änderung wird keine bestimmte Bildrate oder RAM-Grenze als erreicht gemeldet. Die vorherigen Leistungsziele, insbesondere 220 MiB, sind weiterhin nicht abgenommen. Startup-Prüfungen sind funktionale Prüfungen mit isolierten Profilen, keine fünf kontrollierten Leistungsmessungen.

## Abnahme

- `checks/social-minimal-core.trx`: 412/412 Kernprüfungen.
- `checks/social-minimal-diagnostic-2.trx`: zusätzliche Abnahme bestanden, einschließlich echter Scrollschritte, Vorschau-Bilder, Fenstergrößen, Theme-Wechsel, Chatentwürfe, Party, Voice-Steuerung und GIF-Lebensdauer.
- `screenshots/social-minimal-acceptance-final/capture-provenance.json`: 114 echte WPF-Aufnahmen mit SHA-256. Dark/Light, 980/1180/1920 Pixel für das rechte Panel und zusätzliche 1440-Pixel-/Render-Skalierungsprüfungen in der Gesamtabnahme.
- `startup-social-minimal/startup-report.json`: 5/5 funktionale Startprüfungen mit isolierten Profilen, PerMonitorV2, erhaltenen Einstellungen/Bibliothek/Notizen und synthetischen Credential-Bytes sowie Tracker Exit 0.

Frühe Fehlversuche bleiben als TRX erhalten. Die neue 140-ms-Ruhephase erforderte, dass die Kontrastprüfung ihr endgültiges Ergebnis nach dem Scrollen abwartet. Weitere frühe Abnahmeläufe meldeten ausbleibende Preview-Bilder; der endgültige Abnahmelauf prüft geladene Bildquellen und speichert bei erneutem Fehler Bildpositionen/Prioritäten und eine Aufnahme. Ein erster vollständiger Lauf bestand 305/306 Prüfungen; die Fokusprüfung des Fixes-Dialogs wurde durch parallel laufende Starttest-Fenster gestört. Der abschließende vollständige Lauf findet ohne parallele Starttest-Fenster statt.

Portable App-DLL SHA-256: `5A34F6083080EE6DBD7A39F2041032B45689A75BE8675D45ED0D0FF11A191954`.

Die wiederholte Gesamtabnahme meldete außerdem Timeouts in der Trailer-Testsequenz: Produkt-Trailer brechen ihre Hover-Anfrage bei Fenster-Deaktivierung absichtlich ab. Die Prüfungen für späte Antworten und fehlende Medien laufen jetzt mit einem geladenen, unsichtbaren HwndSource statt im aktivierungsabhängigen Hauptfenster. Beide Assertions und die echte 450-ms-Hover-Verzögerung bleiben erhalten. Die eigentlichen Video-, Lautstärke-, Fensteraktivierungs- und Wiedergabeprüfungen verwenden weiterhin das normale WPF-Fenster. Dies ist eine Testisolation; die Trailer-Produktlogik wurde hierfür nicht geändert.

**Abschluss:** `checks/social-minimal-verified.trx` besteht vollständig mit **306/306 UI-/Integrationstests**. Zusammen mit **412/412 Kernprüfungen** sind das **718 eindeutige Tests**, zusätzlich zur gesonderten Abnahme und **5/5 Startprüfungen**. Die komplette Prüfreihenfolge wurde nach der Testisolation wiederholt; keine Produkt-Trailer-Prüfung wurde deaktiviert. Der Release-Nachweis mit Quell-/Test-/DLL-Hashes steht in `artifacts/performance-refresh/release-social-minimal-validation.json`. Die frameworkabhängige Test-App hat DLL-Hash `C863C3D5F191CE3A40DA3425E3AE9DE682E36B2E69CB5B84B24286142497F283`; die portable App verwendet dieselben Produktquellen und .NET 10.0.13 als eigenständige Laufzeit.
