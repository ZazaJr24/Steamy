# Steamy 0.8 — kompletter Design-, Discord- und Controller-Umbau

> **Nutzerkorrektur vom 14.10.2026:** Der Design-Umbau gilt nur für die Discord-/Freunde-Seite. Der bisherige App-Look, Home, Bibliothek, Downloads, Tools, Einstellungen und die schwebende obere Navigation werden wiederhergestellt. Die mittlere Navigation soll dasselbe dezente Glas wie das Steamy-Element links erhalten. Diese Korrektur ersetzt die unten beschriebenen Änderungen am globalen Design und der festen Seitenleiste; die übrigen Funktionswünsche bleiben bestehen.

> **Ergänzte Referenzen vom 14.10.2026:** Downloads ausdrücklich als kompakte horizontale Zeilen mit Cover, Fortschritt und Aktionen gestalten; das Spiele-Cover-Grid dicht und ruhig halten. Den Chat kompakter nach der angehängten Epic-Referenz gestalten. Die schwebende obere Navigation und Home bleiben erhalten. GIF-Anbieter: KLIPY als gewünschte Alternative, GIPHY optional; direkte GIF-Links funktionieren auch ohne Such-Key.

> **Ergänzung vom 15.10.2026:** Dunkles Design nach der Epic-Referenz mit neutralem Schwarz/Anthrazit statt farbigem Hintergrund in Bibliothek, Downloads und Friends. Home-Artwork und die schwebende Glas-Navigation bleiben erhalten. Für die Hardware-Abnahme steht laut Nutzer kein Controller zur Verfügung; USB-/Bluetooth-, Rumble- und virtuelle Ausgabe-Tests werden deshalb als nicht durchgeführt ausgewiesen.

## 1. Ziel und feste Entscheidungen

Steamy bekommt eine durchgehend neue Oberfläche im Stil **Discord × macOS**: dunkel, aufgeräumt, dezentes Glas und schnelle Bedienung. Der Umbau umfasst Home, Bibliothek, Downloads, Freunde, Partys, Tools, Einstellungen und Einrichtung.

- **Kein Discord-Bot, kein eigener Webdienst, kein Hosting.**
- Community: deinem Discord-Server beitreten sowie Steamy-Partys über Einladungen und Party-Codes betreten.
- GIF-Suche über **GIPHY**.
- Voice für Partys und private Gespräche.
- Eigenes integriertes DS4Windows-Backend als separater quelloffener Hilfsprozess.
- WPF bleibt bestehen. App und Hilfsprozess werden auf die stabile **.NET-10-LTS-Basis** gebracht; bestehende Einstellungen, Spielbibliothek und gespeicherte Discord-Sitzung bleiben erhalten. [.NET-Support](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)

## 2. Neues Design und weniger Delay

### Aufbau

- Feste linke Navigation: Home, Bibliothek, Downloads, Freunde, Community, Controller, Tools und Einstellungen.
- Seitenleiste mit 224 Pixeln; unter 1280 Pixel Fensterbreite kompakte Symbolleiste mit 72 Pixeln.
- Gemeinsame Kopfzeile mit Seitentitel, Benachrichtigungen und Fenstersteuerung.
- Freunde: Kontaktliste links, Unterhaltung in der Mitte, Party rechts. Unter 1040 Pixel Inhaltsbreite öffnet sich die Party als ausklappbarer Bereich.
- Aktive Voice-Verbindung bleibt beim Seitenwechsel als kompakte Leiste erreichbar.

### Gestaltung

- Anthrazitfarbener Hintergrund, ruhige Flächen und violettblaue Akzente.
- Glas hauptsächlich für Seitenleiste und Popups; Nachrichten und Einstellungen erhalten gut lesbare Hintergründe.
- Einheitliche Segoe-UI-Typografie, 15-Pixel-Nachrichten, klare Überschriften und ein gemeinsames Abstands- und Kontrollsystem.
- Bibliothek und Home stellen Spielbilder stärker heraus. Downloads zeigen Fortschritt und Aktionen kompakt. Tools und Einstellungen verwenden dieselben Oberflächen.
- Heller Modus, hoher Kontrast, Tastaturfokus und reduzierte Effekte werden mitgestaltet.

### Geschwindigkeit

Zuerst werden Startzeit, Seitenwechsel, Eingaben, Scrollen, Chat-Laden und SDK-Wartezeiten getrennt gemessen.

- Discord erhält einen eigenen SDK-Thread mit eigener Callback-Verarbeitung.
- Langsames Laden einer Unterhaltung darf Senden, Voice oder andere Unterhaltungen nicht über eine gemeinsame Warteschlange blockieren.
- Bildverarbeitung, GIF-Laden und Dateiarbeit laufen außerhalb des UI-Threads.
- Anwesenheitsänderungen werden gebündelt; Listen werden gezielt aktualisiert.
- Aufwendige Hintergrundeffekte und Animationen ohne sichtbaren Nutzen entfallen.
- Ziel auf dem Test-PC: sichtbare Eingaberückmeldung innerhalb von 100 ms, gewöhnliche Eingaben im 95. Perzentil unter 50 ms. Netzwerkwartezeiten werden separat ausgewiesen.

## 3. Discord, GIFs, Voice und Beitreten

### Konto, Freunde und Nachrichten

- Gespeicherte Anmeldung im Hintergrund wiederherstellen; Seiten bleiben währenddessen bedienbar.
- Verbindungsabbruch und Wiederverbinden bekommen verständliche Zustände.
- Freunde, Anfragen, Suche, Blockieren, Entwürfe und ungelesene Nachrichten bleiben in einem gemeinsamen Workspace.
- Nachrichten erhalten die Zustände „Wird gesendet“, „Gesendet“ und „Fehlgeschlagen“. Wiederholen darf keine doppelten Nachrichten erzeugen.
- Scrollposition und Eingabefeld bleiben beim Laden, bei neuen Nachrichten und beim Größenwechsel stabil.

### GIFs und Medien

- GIFs animieren im Chat und im vergrößerten Viewer.
- Wiedergabe pausiert außerhalb des sichtbaren Bereichs und bei minimierter App; reduzierte Bewegung bietet eine manuelle Wiedergabe.
- GIF-Picker mit Suche, Trending und Favoriten anhand von Anbieter-IDs.
- GIPHY-Aufrufe erfolgen direkt vom Client mit konfigurierbarem App-Key, Anbieterkennzeichnung und verständlicher Behandlung von Kontingentfehlern. Medien werden entsprechend den Anbietervorgaben nicht dauerhaft gecacht. [GIPHY-Vorgaben](https://developers.giphy.com/docs/api/)
- Suchanfragen werden kurz entprellt und bei neuer Eingabe abgebrochen. Der Picker verwendet kleine Vorschauen.
- GIF-Größe und Wiedergaberessourcen werden begrenzt, damit Medien keinen neuen Delay verursachen.

### Voice

- Party-Voice verwendet die bestehende Discord-Lobby.
- Private Gespräche erhalten eine separate Zwei-Personen-Lobby und eine annehmbare Einladung im Steamy-Chat. Das SDK unterstützt Voice zwischen Lobby-Mitgliedern. [Discord-Voice-Dokumentation](https://docs.discord.com/developers/discord-social-sdk/development-guides/managing-voice-chat)
- Mikrofonaktivierung erst nach ausdrücklichem Beitreten.
- Mute, Deafen, Push-to-talk, Mikrofon-/Ausgabewahl, Lautstärke und Sprecheranzeigen.
- Eine aktive Voice-Sitzung gleichzeitig; Wechsel erfolgt über eine eindeutige Aktion.
- Seitenwechsel unterbrechen Gespräche nicht. Nach App-Neustart wird kein Gespräch automatisch mit offenem Mikrofon betreten.
- Logout, Verlassen und Fehler räumen Audioressourcen zuverlässig auf.

### Community und Partys

- Community-Karte für **Steamy** mit `https://discord.gg/eYyaFABabC`, Serverinformationen und „In Discord öffnen / Beitreten“.
- Keine vorgetäuschte Mitgliederliste oder globale öffentliche Partysuche.
- Partys bekommen kopierbare Codes und ein „Mit Code beitreten“-Feld.
- Lobby-Metadaten unterscheiden Spielpartys von privaten Voice-Sitzungen. Ungültige Codes dürfen keine scheinbar erfolgreiche leere Party anzeigen.
- Spiel, Mitglieder, Einladungen und Voice-Zustand sind gemeinsam sichtbar.
- Beitreten startet kein Spiel automatisch; gestartet werden ausschließlich passende installierte Spiele.

Die Services werden um klar getrennte Schnittstellen für SDK-Ausführung, Voice, GIF-Suche und Party-Code-Verarbeitung ergänzt.

## 4. Integriertes Controller-Backend

- Basis: eigener Fork von **DS4Windows 3.9.9**, fest auf Commit `722c561c3d7aa65dcf063e716c04ca69e4ca33bd` gebunden.
- Backend läuft als `Steamy.ControllerHost`; sämtliche Bedienung erfolgt in Steamy.
- Kommunikation über versionierte lokale Named Pipes. Controller-Verarbeitung und virtuelle Ausgabe bleiben außerhalb des WPF-Threads.
- Backend-Quellcode, Änderungen, Lizenzhinweise und reproduzierbarer Build werden separat mitgeliefert. Das DS4Windows-Projekt verwendet GPL-3.0 und ist inzwischen archiviert. [Upstream](https://github.com/schmaldeo/DS4Windows)
- Erste Hardware-Abnahme: DualShock 4 und DualSense über USB/Bluetooth sowie Xbox-/XInput-Controller für die App-Bedienung.
- Controller-Seite mit Verbindung, Akku, Eingabetest, Mapping, Deadzones, Stickkurven, Rumble, Lightbar und Spielprofilen.
- App-Navigation mit D-Pad/Stick, Bestätigen, Zurück, Schultertasten und sichtbaren Tastenhinweisen; Texteingabe über Bildschirmtastatur.
- Spielprofile werden vor dem Start angewendet und beim Wechsel zurückgesetzt.
- Fehlende Treiber werden im Einrichtungsassistenten erkannt. Installation erfolgt ausdrücklich über signierte Originalpakete; fremde Controller-Konfigurationen werden nicht automatisch überschrieben.
- ViGEm-Kompatibilität wird ausdrücklich geprüft, da das Projekt eingestellt wurde. [ViGEm-Status](https://docs.nefarius.at/projects/ViGEm/End-of-Life/)

## 5. Reihenfolge, Tests und Lieferung

**Umsetzungsreihenfolge**

1. Performance-Baseline, Laufzeitbasis und neuer App-Rahmen.
2. Vollständige Hauptseiten einschließlich Freunde, Party und Community.
3. GIF-Wiedergabe, Suche und verbesserte Nachrichten.
4. Party-Codes, private Voice-Einladungen und Party-Voice.
5. Controller-Backend, Profile und vollständige Controller-Navigation.
6. Gemeinsame Abnahme und Windows-Paket.

**Abnahme**

- Screenshots echter WPF-Seiten in beiden Themes bei 980×620 und 1440×900 sowie mit Windows-Skalierung.
- Lange Chats, mehrere GIFs, langsames Netzwerk und Verbindungsabbrüche testen.
- Voice auf zwei PCs prüfen: Einladen, Annehmen, Gerätewechsel, Mute, Wiederverbinden und Verlassen.
- Controller-Hotplug, USB/Bluetooth, Profile, doppelte Eingaben und Backend-Absturz prüfen.
- Bestehende automatisierte Tests weiterführen und neue Funktionsregressionen ergänzen.
- Start und Update mit vorhandenem Benutzerprofil testen; keine Bibliotheks- oder Sitzungsdaten zurücksetzen.
- Abschließend eine neue portable Windows-x64-Version samt ZIP, Backend-Quellpaket und Prüfbericht liefern. Nicht durchgeführte Hardware- oder Live-Tests werden ausdrücklich ausgewiesen.
