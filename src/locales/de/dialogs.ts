// German for the dialogs part of the interface.
export const dialogs: Record<string, string> = {
  // Shared by the dialogs
  Close: "Schließen",
  "Something went wrong": "Etwas ist schiefgelaufen",
  "Request failed ({status})": "Anfrage fehlgeschlagen ({status})",

  // BundleDialog
  "Could not read the apps": "Die Apps konnten nicht gelesen werden",
  "{title}: {count} item pending": "{title}: {count} Element vorgemerkt",
  "{title}: {count} items pending": "{title}: {count} Elemente vorgemerkt",
  "Non-free software will be allowed too. Prepare your pending edits to build them.":
    "Unfreie Software wird ebenfalls erlaubt. Bereiten Sie Ihre vorgemerkten Änderungen vor, um sie zu bauen.",
  "Include {name}": "{name} einschließen",
  "Untick what you do not want; everything else becomes one pending edit.":
    "Entfernen Sie das Häkchen bei allem, was Sie nicht möchten; der Rest wird zu einer vorgemerkten Änderung.",
  "The package index could not be built. {error}": "Der Paketindex konnte nicht erstellt werden. {error}",
  "Reading the package index…": "Paketindex wird gelesen…",
  "Apps, installed for you": "Apps, für Sie installiert",
  "{names} is not free software; allowing non-free software is part of the edit.":
    "{names} ist keine freie Software; das Erlauben unfreier Software gehört zur Änderung.",
  "{names} are not free software; allowing non-free software is part of the edit.":
    "{names} sind keine freie Software; das Erlauben unfreier Software gehört zur Änderung.",
  "{names} needs a file chosen by hand. Add it from Discover on its own.":
    "{names} braucht eine von Hand gewählte Datei. Fügen Sie es einzeln über „Entdecken“ hinzu.",
  "{names} need a file chosen by hand. Add them from Discover on their own.":
    "{names} brauchen eine von Hand gewählte Datei. Fügen Sie sie einzeln über „Entdecken“ hinzu.",
  "Add {count} item to pending edits": "{count} Element zu den vorgemerkten Änderungen hinzufügen",
  "Add {count} items to pending edits": "{count} Elemente zu den vorgemerkten Änderungen hinzufügen",
  "Everything is there": "Alles ist bereits vorhanden",

  // OneStepDialog
  Installing: "Wird installiert",
  "Ready for your review": "Bereit zur Prüfung",
  "Downloaded and checked": "Heruntergeladen und geprüft",
  "Downloading and checking": "Wird heruntergeladen und geprüft",
  "Only your apps changed. You can undo it until you clean up old versions.":
    "Es haben sich nur Ihre Apps geändert. Sie können es rückgängig machen, bis Sie alte Versionen aufräumen.",
  "It was prepared but not applied. {reason}": "Es wurde vorbereitet, aber nicht angewendet. {reason}",
  "It could not be applied, so everything went back to how it was. Your change is still in your pending edits.":
    "Es konnte nicht angewendet werden, daher ist alles wieder wie zuvor. Ihre Änderung ist weiterhin in Ihren vorgemerkten Änderungen.",
  "It could not be applied, and only part of it could be undone. Your change is still in your pending edits.":
    "Es konnte nicht angewendet werden, und nur ein Teil ließ sich rückgängig machen. Ihre Änderung ist weiterhin in Ihren vorgemerkten Änderungen.",
  "It could not be applied, and going back did not work. The Restore points page can take you back.":
    "Es konnte nicht angewendet werden, und das Zurückkehren hat nicht funktioniert. Auf der Seite „Wiederherstellungspunkte“ können Sie zurückkehren.",
  "It could not be applied before anything changed. Your change is still in your pending edits.":
    "Es konnte nicht angewendet werden, bevor sich etwas geändert hat. Ihre Änderung ist weiterhin in Ihren vorgemerkten Änderungen.",
  "It could not be prepared, so nothing changed. Your change is still in your pending edits.":
    "Es konnte nicht vorbereitet werden, daher hat sich nichts geändert. Ihre Änderung ist weiterhin in Ihren vorgemerkten Änderungen.",
  "It waits for your review when it is ready.": "Es wartet auf Ihre Prüfung, sobald es bereit ist.",
  "Only your apps change, so it is applied as soon as it is ready.":
    "Es ändern sich nur Ihre Apps, daher wird es angewendet, sobald es bereit ist.",
  "Download and check": "Herunterladen und prüfen",
  Install: "Installieren",
  "Apps you just installed can take a moment to appear in your menu.":
    "Gerade installierte Apps können einen Moment brauchen, bis sie in Ihrem Menü erscheinen.",
  Details: "Details",
  "Show the full log": "Vollständiges Protokoll anzeigen",
  Hide: "Ausblenden",
  "Review it first": "Erst prüfen",
  Stop: "Anhalten",
  "Open the review": "Prüfung öffnen",

  // ExperienceDialog
  "{title}: edit pending": "{title}: Änderung vorgemerkt",
  "{title}: {count} edits pending": "{title}: {count} Änderungen vorgemerkt",
  "Turned on": "Eingeschaltet",
  "Turned off": "Ausgeschaltet",
  "System setting for every user of this computer.": "Systemeinstellung für alle Benutzer dieses Computers.",
  "Setting for your user.": "Einstellung für Ihren Benutzer.",
  "Set in {file}.": "Festgelegt in {file}.",
  "Turn {title} on": "{title} einschalten",
  "{option}, set in {files}": "{option}, festgelegt in {files}",
  "Write new settings to": "Neue Einstellungen schreiben nach",
  "Not used by {host}": "Nicht von {host} verwendet",
  "{host} does not import this file, so settings written here have no effect on it. Prepare will refuse them.":
    "{host} bindet diese Datei nicht ein, daher haben hier geschriebene Einstellungen keine Wirkung darauf. Beim Vorbereiten werden sie abgelehnt.",
  "Prepare checks that every setting takes effect before anything is built.":
    "Beim Vorbereiten wird geprüft, dass jede Einstellung wirkt, bevor etwas gebaut wird.",
  "{title} is not free software": "{title} ist keine freie Software",
  "This computer only installs software with a free license. Turning it on also allows non-free software, in {file}.":
    "Dieser Computer installiert nur Software mit freier Lizenz. Beim Einschalten wird auch unfreie Software erlaubt, in {file}.",
  "This computer only installs software with a free license. Allow non-free software first, then turn it on.":
    "Dieser Computer installiert nur Software mit freier Lizenz. Erlauben Sie zuerst unfreie Software und schalten Sie es dann ein.",
  "No changes": "Keine Änderungen",
  "Turning it off writes {value} into {file}.": "Beim Ausschalten wird {value} in {file} geschrieben.",

  // PackageDialog
  "{name} will be installed": "{name} wird installiert",
  "Non-free software will be allowed too. Prepare your pending edits to build it.":
    "Unfreie Software wird ebenfalls erlaubt. Bereiten Sie Ihre vorgemerkten Änderungen vor, um es zu bauen.",
  "Prepare your pending edits to build it.": "Bereiten Sie Ihre vorgemerkten Änderungen vor, um es zu bauen.",
  "Installing {name}": "{name} wird installiert",
  "{name} is installed": "{name} ist installiert",
  "Removing {name}": "{name} wird entfernt",
  "{name} is removed": "{name} ist entfernt",
  "{name} will be removed": "{name} wird entfernt",
  "Prepare your pending edits to build without it.": "Bereiten Sie Ihre vorgemerkten Änderungen vor, um ohne es zu bauen.",
  Category: "Kategorie",
  Attribute: "Attribut",
  Command: "Befehl",
  License: "Lizenz",
  "{licenses} (not free)": "{licenses} (nicht frei)",
  Website: "Website",
  "Marked as broken in nixpkgs; building it will probably fail.": "In nixpkgs als defekt markiert; der Build schlägt wahrscheinlich fehl.",
  "Waiting to be installed in your pending edits ({file}).": "Wartet in Ihren vorgemerkten Änderungen auf die Installation ({file}).",
  "Waiting to be removed in your pending edits ({file}).": "Wartet in Ihren vorgemerkten Änderungen auf die Entfernung ({file}).",
  "Installed for every user": "Für alle Benutzer installiert",
  "Listed in {file}": "Aufgeführt in {file}",
  "Added by {origin}": "Hinzugefügt von {origin}",
  "Install for": "Installieren für",
  "Just you": "Nur Sie",
  "Every user": "Alle Benutzer",
  "Add it to": "Hinzufügen zu",
  "{category}: {file} (not used by this host)": "{category}: {file} (von diesem Rechner nicht verwendet)",
  "The package is added to this file's package list, next to the others.":
    "Das Paket wird in die Paketliste dieser Datei eingetragen, neben die anderen.",
  "{name} is not free software": "{name} ist keine freie Software",
  "Your user only installs software with a free license. Adding it also allows non-free software for you, in {file}.":
    "Ihr Benutzer installiert nur Software mit freier Lizenz. Beim Hinzufügen wird unfreie Software auch für Sie erlaubt, in {file}.",
  "This computer only installs software with a free license. Adding it also allows non-free software, in {file}.":
    "Dieser Computer installiert nur Software mit freier Lizenz. Beim Hinzufügen wird auch unfreie Software erlaubt, in {file}.",
  "Your user only installs software with a free license. Allow non-free software first, then add it.":
    "Ihr Benutzer installiert nur Software mit freier Lizenz. Erlauben Sie zuerst unfreie Software und fügen Sie es dann hinzu.",
  "This computer only installs software with a free license. Allow non-free software first, then add it.":
    "Dieser Computer installiert nur Software mit freier Lizenz. Erlauben Sie zuerst unfreie Software und fügen Sie es dann hinzu.",
  "Allow non-free software…": "Unfreie Software erlauben…",
  "Remove now": "Jetzt entfernen",
  "Install now": "Jetzt installieren",

  // app.svelte.ts
  "Could not save the setting": "Die Einstellung konnte nicht gespeichert werden",
  "Could not load the log": "Das Protokoll konnte nicht geladen werden",
  "Could not start: {action}": "{action} konnte nicht gestartet werden",
  "Could not stop it": "Es konnte nicht angehalten werden",
  "Could not cancel": "Das Abbrechen ist nicht gelungen",
  "Prepared changes discarded": "Vorbereitete Änderungen verworfen",
  "Could not discard the changes": "Die Änderungen konnten nicht verworfen werden",
  "Could not refresh": "Das Neuladen ist nicht gelungen",
  "{label} cancelled": "{label} abgebrochen",
  "{label} failed: {reason}": "Fehlgeschlagen: {label} – {reason}",
  "{label} failed": "Fehlgeschlagen: {label}",
  "View log": "Protokoll ansehen",
  "Changes are ready to review": "Änderungen sind bereit zur Prüfung",
  "Nothing on the computer has changed yet.": "Auf dem Computer hat sich noch nichts geändert.",
  "Will apply on next boot": "Wird beim nächsten Start angewendet",
  "Changes applied": "Änderungen angewendet",
  "The new system becomes active after a restart.": "Das neue System wird nach einem Neustart aktiv.",
  "{label}: pin saved": "{label}: Pin gespeichert",
  "Prepare your user's changes to build and apply the new version.":
    "Bereiten Sie die Änderungen Ihres Benutzers vor, um die neue Version zu bauen und anzuwenden.",
  "{label} finished": "{label} abgeschlossen",

  // software.svelte.ts
  "Search failed": "Die Suche ist fehlgeschlagen",
  "Could not read the app list": "Die App-Liste konnte nicht gelesen werden",
  "Could not build the package index": "Der Paketindex konnte nicht erstellt werden",
  "Could not read option documentation": "Die Dokumentation der Optionen konnte nicht gelesen werden",
  "Could not save the edit": "Die Änderung konnte nicht gespeichert werden",
  "Applying your changes": "Ihre Änderungen werden angewendet",
  "Your changes are applied": "Ihre Änderungen sind angewendet",
};
