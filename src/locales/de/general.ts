// The window, navigation, settings and the formats shared by every page.
export const general: Record<string, string> = {
  // Navigation and window
  Overview: "Übersicht",
  Software: "Software",
  Computer: "Computer",
  Changes: "Änderungen",
  Updates: "Aktualisierungen",
  "Restore points": "Wiederherstellungspunkte",
  Activity: "Aktivität",
  Settings: "Einstellungen",
  Sections: "Bereiche",
  "Skip to content": "Zum Inhalt springen",
  ", running": ", läuft",
  "Targeting {host}": "Ziel: {host}",
  Reconnecting: "Verbindung wird wiederhergestellt",
  Show: "Anzeigen",
  "Reading this computer's state": "Zustand dieses Computers wird gelesen",
  "Open Nix Manager from the app menu or tray": "Öffnen Sie Nix Manager über das Anwendungsmenü oder die Kontrollleiste",
  "This address needs the private session link Nix Manager creates when it starts. Run {command} to open a new window.":
    "Diese Adresse braucht den privaten Sitzungslink, den Nix Manager beim Start erstellt. Führen Sie {command} aus, um ein neues Fenster zu öffnen.",
  Notifications: "Benachrichtigungen",
  "Error:": "Fehler:",
  Dismiss: "Schließen",

  // Settings
  Appearance: "Erscheinungsbild",
  "Follow the desktop or choose a theme.": "Wie die Arbeitsumgebung oder ein eigenes Farbschema.",
  Theme: "Farbschema",
  System: "System",
  Light: "Hell",
  Dark: "Dunkel",
  Language: "Sprache",
  "Follow the desktop or choose a language.": "Wie die Arbeitsumgebung oder eine eigene Sprache.",
  "How much to show": "Wie viel angezeigt wird",
  "One button applies your changes, with a short summary first when they need a look.":
    "Ein Knopf wendet Ihre Änderungen an, mit einer kurzen Zusammenfassung vorab, wenn sie einen Blick brauchen.",
  "Every step: prepare, then review the full comparison, then apply.":
    "Jeder Schritt: vorbereiten, dann den vollständigen Vergleich prüfen, dann anwenden.",
  Simple: "Einfach",
  Detailed: "Ausführlich",
  "Prepare updates in the background": "Aktualisierungen im Hintergrund vorbereiten",
  "When the tray finds updates, they are downloaded and built while the computer is plugged in and not busy. A notification says when they are ready. Nothing is applied until you choose.":
    "Findet das Symbol in der Kontrollleiste Aktualisierungen, werden sie heruntergeladen und gebaut, während der Computer am Strom hängt und nicht ausgelastet ist. Eine Benachrichtigung meldet, wenn sie bereit sind. Angewendet wird nichts, bevor Sie es entscheiden.",
  "Clean up automatically": "Automatisch aufräumen",
  "Once a week, while the computer is plugged in and not busy, older restore points of your apps are removed and files nothing uses are deleted. The newest five are kept. System restore points are only removed when you choose.":
    "Einmal pro Woche, während der Computer am Strom hängt und nicht ausgelastet ist, werden ältere Wiederherstellungspunkte Ihrer Apps entfernt und Dateien gelöscht, die nichts mehr verwendet. Die neuesten fünf bleiben. Wiederherstellungspunkte des Systems werden nur entfernt, wenn Sie es wählen.",
  "Keep a history of changes": "Verlauf der Änderungen führen",
  "Every change you apply is saved with a short description, so the Restore points page shows what changed and when. It is kept with Git in the configuration folder.":
    "Jede angewendete Änderung wird mit einer kurzen Beschreibung gespeichert, damit die Seite „Wiederherstellungspunkte“ zeigt, was sich wann geändert hat. Der Verlauf liegt mit Git im Konfigurationsordner.",
  "Back up after each change": "Nach jeder Änderung sichern",
  "The history is sent to {upstream} after each change you apply.": "Der Verlauf wird nach jeder angewendeten Änderung an {upstream} gesendet.",
  "The configuration folder has no remote copy to back up to yet.": "Der Konfigurationsordner hat noch keine entfernte Kopie, in die gesichert werden kann.",
  "Show app details from Flathub": "App-Details von Flathub anzeigen",
  "Names, descriptions, categories and icons of apps come from Flathub's public catalogue, downloaded about once a week. The apps themselves are still installed from your nixpkgs.":
    "Namen, Beschreibungen, Kategorien und Symbole der Apps kommen aus dem öffentlichen Katalog von Flathub, der etwa einmal pro Woche heruntergeladen wird. Die Apps selbst werden weiterhin aus Ihrem nixpkgs installiert.",
  "Target host": "Zielrechner",
  "Detected as {host}.": "Erkannt als {host}.",
  "{host} (this computer)": "{host} (dieser Computer)",
  Reset: "Zurücksetzen",
  "Changes are now built and applied with the {host} configuration. Applying another computer's configuration here can break this system; use this only to test builds.":
    "Änderungen werden jetzt mit der Konfiguration von {host} gebaut und angewendet. Die Konfiguration eines anderen Computers hier anzuwenden, kann dieses System beschädigen; nutzen Sie das nur, um Builds zu testen.",
  Configuration: "Konfiguration",
  "Home profile": "Benutzerprofil",
  "Home configuration": "Benutzerkonfiguration",
  "NixOS configuration": "NixOS-Konfiguration",
  "Changes are prepared without special rights and shown for review. Only applying a reviewed system change, going back to an earlier system version and removing system restore points ask for your password.":
    "Änderungen werden ohne besondere Rechte vorbereitet und zur Prüfung angezeigt. Nur das Anwenden einer geprüften Systemänderung, das Zurückkehren zu einer früheren Systemversion und das Entfernen von Wiederherstellungspunkten des Systems fragen nach Ihrem Passwort.",

  // Formats
  never: "nie",
  "just now": "gerade eben",
  unknown: "unbekannt",
  "{seconds}s": "{seconds} s",
  "{minutes}m {seconds}s": "{minutes} min {seconds} s",
  "{hours}h {minutes}m": "{hours} h {minutes} min",
  "Your user and system": "Ihr Benutzer und das System",
  "Your user": "Ihr Benutzer",
  unversioned: "ohne Version",
  // Tray
  "Nix Manager needs attention": "Nix Manager braucht Ihre Aufmerksamkeit",
  "Open Nix Manager": "Nix Manager öffnen",
  "Updates are ready": "Aktualisierungen sind bereit",
  "{summary} Nothing changes until you apply them.": "{summary} Es ändert sich nichts, bis Sie sie anwenden.",
  "Prepare all updates for review…": "Alle Aktualisierungen zur Prüfung vorbereiten…",
  "Quit Nix Manager": "Nix Manager beenden",
  "Some update checks failed": "Einige Suchen nach Aktualisierungen sind fehlgeschlagen",
  "No updates reported": "Keine Aktualisierungen gemeldet",
  "An operation failed. Open the dashboard for its log.": "Ein Vorgang ist fehlgeschlagen. Öffnen Sie Nix Manager, um seine Ausgabe zu sehen.",
  "Space freed": "Speicherplatz freigegeben",
  "{size} were freed.": "{size} wurden freigegeben.",
  "Older restore points were removed.": "Ältere Wiederherstellungspunkte wurden entfernt.",
  "The newest {count} restore points of your apps are kept.": "Die neuesten {count} Wiederherstellungspunkte Ihrer Apps bleiben erhalten.",
  "Changes are ready to review": "Änderungen sind bereit zur Prüfung",
  "The build finished. Open Nix Manager to see what changes and apply it.":
    "Der Build ist fertig. Öffnen Sie Nix Manager, um zu sehen, was sich ändert, und es anzuwenden.",
  "Nix Manager finished": "Nix Manager ist fertig",
  "{action} completed for {host}.": "{action} für {host} abgeschlossen.",
};
