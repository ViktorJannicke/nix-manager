// German for the overview part of the interface.
export const overview: Record<string, string> = {
  // Overview
  "This computer": "Dieser Computer",
  "{count} update available": "{count} Aktualisierung verfügbar",
  "{count} updates available": "{count} Aktualisierungen verfügbar",
  "Restart to finish the update.": "Starten Sie neu, um die Aktualisierung abzuschließen.",
  "The system is still running kernel {booted}; the active configuration uses {current}.":
    "Das System läuft noch mit Kernel {booted}; die aktive Konfiguration verwendet {current}.",
  "{count} pending edit to your configuration": "{count} vorgemerkte Änderung an Ihrer Konfiguration",
  "{count} pending edits to your configuration": "{count} vorgemerkte Änderungen an Ihrer Konfiguration",
  "and {count} more": "und {count} weitere",
  "{count} edit cannot be applied to your configuration as it is now.":
    "{count} Änderung lässt sich auf Ihre Konfiguration in ihrem jetzigen Zustand nicht anwenden.",
  "{count} edits cannot be applied to your configuration as it is now.":
    "{count} Änderungen lassen sich auf Ihre Konfiguration in ihrem jetzigen Zustand nicht anwenden.",
  "Show edits": "Änderungen anzeigen",
  "Last change, {when}": "Letzte Änderung, {when}",
  "Undo goes back to how {what} was before{files}. You see what changes before anything is applied.":
    "„Rückgängig“ stellt den Zustand {what} von vorher wieder her{files}. Sie sehen, was sich ändert, bevor etwas angewendet wird.",
  "your user's setup": "Ihres Benutzers",
  "this computer": "dieses Computers",
  ", configuration files included": ", einschließlich der Konfigurationsdateien",
  "Preparing…": "Wird vorbereitet…",
  Undo: "Rückgängig",
  "Prepare your user and system together": "Ihren Benutzer und das System gemeinsam vorbereiten",
  "{step}, {duration} so far": "{step}, bisher {duration}",
  "Show output": "Ausgabe anzeigen",
  Cancel: "Abbrechen",
  "Recent activity": "Letzte Aktivität",
  "All activity": "Gesamte Aktivität",
  "your user and system": "Ihr Benutzer und das System",
  "your user": "Ihr Benutzer",
  system: "System",
  "Nothing has run yet. Prepared builds and updates appear here.":
    "Bisher wurde nichts ausgeführt. Vorbereitete Builds und Aktualisierungen erscheinen hier.",
  Detached: "Losgelöst",
  "Everything is saved": "Alles ist gespeichert",
  "{count} unsaved change in the folder": "{count} nicht gespeicherte Änderung im Ordner",
  "{count} unsaved changes in the folder": "{count} nicht gespeicherte Änderungen im Ordner",
  ", source versions edited": ", Quellversionen bearbeitet",
  "Reading…": "Wird gelesen…",
  "Disk space for software": "Speicherplatz für Software",
  "{size} free": "{size} frei",
  "of {total}": "von {total}",
  "Disk used": "Belegter Speicherplatz",
  "Free space by removing old restore points": "Speicherplatz durch Entfernen alter Wiederherstellungspunkte freigeben",
  Unknown: "Unbekannt",

  // Activity
  Filter: "Filter",
  All: "Alle",
  Problems: "Probleme",
  "{scope}, {when}": "{scope}, {when}",
  "Nothing to show.": "Nichts anzuzeigen.",
  "{scope} on {host}, started {started}, took {duration}": "{scope} auf {host}, gestartet {started}, Dauer {duration}",
  "Updates all flake inputs": "Aktualisiert alle Flake-Eingaben",
  "Updates {inputs}": "Aktualisiert {inputs}",
  "Applies on next boot": "Wird beim nächsten Start angewendet",
  "Finishing…": "Wird abgeschlossen…",
  "Try again": "Erneut versuchen",
  "Allow non-free software": "Unfreie Software erlauben",
  "What Nix reported": "Was Nix gemeldet hat",
  Steps: "Schritte",
  "No activity yet": "Noch keine Aktivität",
  "Operations you start appear here with their steps and output.":
    "Vorgänge, die Sie starten, erscheinen hier mit ihren Schritten und ihrer Ausgabe.",

  // Restore points
  "Keeps the newest {count} restore point of {scope}, and the one in use.":
    "Behält den neuesten Wiederherstellungspunkt {scope} sowie den aktuell verwendeten.",
  "Keeps the newest {count} restore points of {scope}, and the one in use.":
    "Behält die neuesten {count} Wiederherstellungspunkte {scope} sowie den aktuell verwendeten.",
  "your user and the system": "Ihres Benutzers und des Systems",
  "Removes {count} restore point of your user.": "Entfernt {count} Wiederherstellungspunkt Ihres Benutzers.",
  "Removes {count} restore points of your user.": "Entfernt {count} Wiederherstellungspunkte Ihres Benutzers.",
  "Removes {count} system restore point and their boot menu entries; you'll be asked for your password.":
    "Entfernt {count} Wiederherstellungspunkt des Systems und die zugehörigen Einträge im Startmenü; Sie werden nach Ihrem Passwort gefragt.",
  "Removes {count} system restore points and their boot menu entries; you'll be asked for your password.":
    "Entfernt {count} Wiederherstellungspunkte des Systems und die zugehörigen Einträge im Startmenü; Sie werden nach Ihrem Passwort gefragt.",
  "Frees about {size}, and more if other unused files are found.":
    "Gibt etwa {size} frei, mehr, wenn weitere ungenutzte Dateien gefunden werden.",
  "Deletes files nothing uses any more. You can't go back to removed restore points.":
    "Löscht Dateien, die nichts mehr verwendet. Zu entfernten Wiederherstellungspunkten können Sie nicht zurückkehren.",
  "Remove old restore points?": "Alte Wiederherstellungspunkte entfernen?",
  "Frees disk space by deleting older versions you then can't go back to.":
    "Gibt Speicherplatz frei, indem ältere Versionen gelöscht werden, zu denen Sie danach nicht mehr zurückkehren können.",
  "Clean up": "Aufräumen",
  "Every change you apply keeps the version before it, so you can go back.":
    "Jede Änderung, die Sie anwenden, bewahrt die Version davor auf, damit Sie zurückkehren können.",
  Profile: "Profil",
  "In use": "In Verwendung",
  "The computer started with this version; it changes at the next restart.":
    "Der Computer wurde mit dieser Version gestartet; sie wechselt beim nächsten Neustart.",
  "Running until restart": "Läuft bis zum Neustart",
  "Go back to this…": "Hierhin zurückkehren…",
  "No restore points": "Keine Wiederherstellungspunkte",
  "Your user has no restore points yet. They appear after the first change you apply.":
    "Ihr Benutzer hat noch keine Wiederherstellungspunkte. Sie erscheinen nach der ersten Änderung, die Sie anwenden.",
  "No system restore points were found on this computer.":
    "Auf diesem Computer wurden keine Wiederherstellungspunkte des Systems gefunden.",
  History: "Verlauf",
  "What changed in the configuration, newest first.": "Was sich in der Konfiguration geändert hat, die neueste zuerst.",
  "Applied here": "Hier angewendet",
  "No history yet": "Noch kein Verlauf",
  "With “Keep a history of changes” on in Settings, every change you apply is listed here.":
    "Ist in den Einstellungen „Verlauf der Änderungen führen“ eingeschaltet, wird hier jede angewendete Änderung aufgeführt.",
  "Remove old restore points and files nothing uses to free disk space.":
    "Entfernt alte Wiederherstellungspunkte und Dateien, die nichts verwendet, um Speicherplatz freizugeben.",
  Keep: "Behalten",
  "Keep fewer": "Weniger behalten",
  "Keep more": "Mehr behalten",
  newest: "neueste",
  "What to clean up": "Was aufgeräumt werden soll",
  "Your user and system": "Ihr Benutzer und das System",
  "Clean up…": "Aufräumen…",
  "No restore points will be removed; files nothing uses are still deleted.":
    "Es werden keine Wiederherstellungspunkte entfernt; Dateien, die nichts verwendet, werden trotzdem gelöscht.",
  "{count} restore point will be removed. Measuring the space this frees…":
    "{count} Wiederherstellungspunkt wird entfernt. Der dadurch frei werdende Speicherplatz wird gemessen…",
  "{count} restore points will be removed. Measuring the space this frees…":
    "{count} Wiederherstellungspunkte werden entfernt. Der dadurch frei werdende Speicherplatz wird gemessen…",
  "{count} restore point will be removed, freeing about {size}{note}.":
    "{count} Wiederherstellungspunkt wird entfernt und gibt etwa {size} frei{note}.",
  "{count} restore points will be removed, freeing about {size}{note}.":
    "{count} Wiederherstellungspunkte werden entfernt und geben etwa {size} frei{note}.",
  "(other restore points still use their files)": "(andere Wiederherstellungspunkte verwenden ihre Dateien weiter)",
  "Settings can remove your user's old restore points automatically.":
    "In den Einstellungen können alte Wiederherstellungspunkte Ihres Benutzers automatisch entfernt werden.",

  // Setup
  "running": "läuft",
  "done": "fertig",
  "failed": "fehlgeschlagen",
  "waiting": "wartet",
  "Nix Manager could not start": "Nix Manager konnte nicht gestartet werden",
  "It reads the configuration in {flake}. Fix the problem there and open Nix Manager again, or run {command} in a terminal to choose another configuration.":
    "Nix Manager liest die Konfiguration in {flake}. Beheben Sie das Problem dort und öffnen Sie Nix Manager erneut, oder führen Sie {command} in einem Terminal aus, um eine andere Konfiguration zu wählen.",
  "Set up this computer": "Diesen Computer einrichten",
  "Nix Manager keeps this computer's configuration in a folder you own, so it can change it for you and keep every version. This copies your configuration from /etc/nixos into that folder once; nothing on the running system changes.":
    "Nix Manager hält die Konfiguration dieses Computers in einem Ordner, der Ihnen gehört, damit es sie für Sie ändern und jede Version aufbewahren kann. Dabei wird Ihre Konfiguration einmalig aus /etc/nixos in diesen Ordner kopiert; am laufenden System ändert sich nichts.",
  "Looking at this computer…": "Dieser Computer wird untersucht…",
  "What is here": "Was vorhanden ist",
  You: "Sie",
  Flakes: "Flakes",
  "{count} file in /etc/nixos": "{count} Datei in /etc/nixos",
  "{count} files in /etc/nixos": "{count} Dateien in /etc/nixos",
  "None found": "Keine gefunden",
  On: "An",
  "Off for now; the new configuration turns them on": "Vorerst aus; die neue Konfiguration schaltet sie ein",
  "Only an administrator can read {files}, so it stays behind. If the configuration needs it, copy it into the new folder afterwards.":
    "Nur ein Administrator kann {files} lesen, daher bleibt die Datei zurück. Wenn die Konfiguration sie braucht, kopieren Sie sie anschließend in den neuen Ordner.",
  "Only an administrator can read {files}, so they stay behind. If the configuration needs them, copy them into the new folder afterwards.":
    "Nur ein Administrator kann {files} lesen, daher bleiben diese Dateien zurück. Wenn die Konfiguration sie braucht, kopieren Sie sie anschließend in den neuen Ordner.",
  "The configuration imports {imports} from channels. A flake cannot see channels, so the check below will fail until those imports become flake inputs.":
    "Die Konfiguration importiert {imports} aus Channels. Ein Flake kann keine Channels sehen, deshalb schlägt die Prüfung unten fehl, bis diese Importe zu Flake-Eingaben werden.",
  "Where your configuration lives": "Wo Ihre Konfiguration liegt",
  Folder: "Ordner",
  "A new, empty folder in your home folder. It becomes a Git repository, so every change you make is kept.":
    "Ein neuer, leerer Ordner in Ihrem Home-Ordner. Er wird zu einem Git-Repository, damit jede Ihrer Änderungen aufbewahrt wird.",
  "Create my configuration": "Meine Konfiguration erstellen",
  "Setting up": "Einrichtung läuft",
  "The folder was removed again, so you can try once more.": "Der Ordner wurde wieder entfernt, sodass Sie es noch einmal versuchen können.",
  "Opening Nix Manager…": "Nix Manager wird geöffnet…",
  "How changes work": "So funktionieren Änderungen",
  "Everything you change, from apps to settings, is written into those files and waits as a pending edit. Prepare builds the new version in the background while you keep working, and shows what will change. Apply switches the computer to it, and every version you applied stays as a restore point you can go back to.":
    "Alles, was Sie ändern, von Apps bis zu Einstellungen, wird in diese Dateien geschrieben und wartet als vorgemerkte Änderung. „Vorbereiten“ baut die neue Version im Hintergrund, während Sie weiterarbeiten, und zeigt, was sich ändern wird. „Anwenden“ stellt den Computer darauf um, und jede angewendete Version bleibt als Wiederherstellungspunkt erhalten, zu dem Sie zurückkehren können.",

  // GenerationRail
  "{count} restore point": "{count} Wiederherstellungspunkt",
  "{count} restore points": "{count} Wiederherstellungspunkte",
  "{title} restore points": "Wiederherstellungspunkte: {title}",
  "In use since {when}": "In Verwendung seit {when}",
  "No restore points yet": "Noch keine Wiederherstellungspunkte",
  "Building next": "Nächste Version wird gebaut",
  "Not active until you apply it": "Erst aktiv, wenn Sie sie anwenden",
  "Next, ready to apply": "Nächste Version, bereit zum Anwenden",
  "{count} update, {added} new, {removed} removed": "{count} Aktualisierung, {added} neu, {removed} entfernt",
  "{count} updates, {added} new, {removed} removed": "{count} Aktualisierungen, {added} neu, {removed} entfernt",
  "First build of this profile": "Erster Build dieses Profils",
  "Prepare the next version of your user": "Nächste Version Ihres Benutzers vorbereiten",
  "Prepare the next system version": "Nächste Systemversion vorbereiten",

  // StatusBadge
  Running: "Läuft",
  Done: "Fertig",
  Failed: "Fehlgeschlagen",
  Cancelled: "Abgebrochen",
  Interrupted: "Unterbrochen",
  Waiting: "Wartet",
  Skipped: "Übersprungen",

  // BuildProgress
  "Starting…": "Wird gestartet…",
  "Evaluating the configuration…": "Konfiguration wird ausgewertet…",
  "Built {done} of {count} package": "{done} von {count} Paket gebaut",
  "Built {done} of {count} packages": "{done} von {count} Paketen gebaut",
  "downloaded {done} of {expected}": "{done} von {expected} heruntergeladen",
  "{done} of {expected}": "{done} von {expected}",
  "{count} failed": "{count} fehlgeschlagen",
  "Everything is already built": "Alles ist bereits gebaut",
  "Build progress": "Build-Fortschritt",

  // LogViewer
  "Log copied": "Protokoll kopiert",
  "Could not copy the log": "Das Protokoll konnte nicht kopiert werden",
  "Filter log": "Protokoll filtern",
  "Errors only": "Nur Fehler",
  "{count} match": "{count} Treffer",
  "{count} matches": "{count} Treffer",
  "Wrap lines": "Zeilen umbrechen",
  "Copy log": "Protokoll kopieren",
  "Download log": "Protokoll herunterladen",
  "Operation output": "Ausgabe des Vorgangs",
  "Earlier output is omitted here; download the log for the full text.":
    "Frühere Ausgabe wird hier nicht angezeigt; laden Sie das Protokoll für den vollständigen Text herunter.",
  "Loading…": "Wird geladen…",
  "No output yet.": "Noch keine Ausgabe.",
  "Follow output": "Ausgabe verfolgen",
};
