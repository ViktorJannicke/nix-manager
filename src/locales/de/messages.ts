// Text the server writes into operations and reviews in English, like failure
// explanations, and translated where it is shown. Keys with placeholders are
// matched against the stored text, as in labels.ts.
export const messages: Record<string, string> = {
  // Failure explanations (src/explain.ts)
  "The disk is full": "Der Datenträger ist voll",
  "Nix ran out of space. Remove old restore points on the Restore points page, then try again.":
    "Nix hat keinen Speicherplatz mehr. Entfernen Sie alte Wiederherstellungspunkte auf der gleichnamigen Seite und versuchen Sie es dann erneut.",
  "Authentication was cancelled": "Die Authentifizierung wurde abgebrochen",
  "Changing the system needs an administrator password. Try again and enter it when asked.":
    "Für Änderungen am System ist ein Administratorpasswort nötig. Versuchen Sie es erneut und geben Sie es ein, wenn Sie danach gefragt werden.",
  "{names} is not free software": "{names} ist keine freie Software",
  "{names} are not free software": "{names} sind keine freie Software",
  "Your configuration only allows software with a free license. Allow non-free software to install it, or remove it from your edits.":
    "Ihre Konfiguration erlaubt nur Software mit freier Lizenz. Erlauben Sie unfreie Software, um sie zu installieren, oder entfernen Sie sie aus Ihren vorgemerkten Änderungen.",
  "{name} has known security problems": "{name} hat bekannte Sicherheitsprobleme",
  "Nixpkgs marks {package} as insecure ({issues}). Choose another package, or allow this version in your configuration if you need it.":
    "Nixpkgs stuft {package} als unsicher ein ({issues}). Wählen Sie ein anderes Paket oder erlauben Sie diese Version in Ihrer Konfiguration, wenn Sie sie brauchen.",
  "Nixpkgs marks {package} as insecure. Choose another package, or allow this version in your configuration if you need it.":
    "Nixpkgs stuft {package} als unsicher ein. Wählen Sie ein anderes Paket oder erlauben Sie diese Version in Ihrer Konfiguration, wenn Sie sie brauchen.",
  "{name} does not run on this computer": "{name} läuft auf diesem Computer nicht",
  "The package is not made for this kind of processor or operating system. Remove it from your configuration.":
    "Das Paket ist nicht für diese Art von Prozessor oder dieses Betriebssystem gemacht. Entfernen Sie es aus Ihrer Konfiguration.",
  "{name} does not work in this version of nixpkgs": "{name} funktioniert in dieser Version von nixpkgs nicht",
  "It is marked as broken. Remove it for now; a later update may fix it.":
    "Es ist als defekt markiert. Entfernen Sie es vorerst; eine spätere Aktualisierung kann das beheben.",
  "{old} is now called {new}": "{old} heißt jetzt {new}",
  "Replace {old} with {new} in your configuration.": "Ersetzen Sie {old} in Ihrer Konfiguration durch {new}.",
  "{name} is no longer available": "{name} ist nicht mehr verfügbar",
  "It was removed from nixpkgs. Remove it from your configuration.":
    "Es wurde aus nixpkgs entfernt. Entfernen Sie es aus Ihrer Konfiguration.",
  "{reason} Remove it from your configuration.": "{reason} Entfernen Sie es aus Ihrer Konfiguration.",
  "{file} is not part of the flake yet": "{file} gehört noch nicht zum Flake",
  "Flakes only see files that Git tracks. Run git add {file} in your configuration folder, then try again.":
    "Flakes sehen nur Dateien, die Git verfolgt. Führen Sie in Ihrem Konfigurationsordner git add {file} aus und versuchen Sie es dann erneut.",
  "{option} no longer exists": "{option} gibt es nicht mehr",
  "{hint} Remove the setting.": "{hint} Entfernen Sie die Einstellung.",
  "{hint} Remove it from {file}.": "{hint} Entfernen Sie sie aus {file}.",
  "Remove the setting.": "Entfernen Sie die Einstellung.",
  "Remove it from {file}.": "Entfernen Sie sie aus {file}.",
  "The setting {option} does not exist": "Die Einstellung {option} gibt es nicht",
  "It may have been renamed or removed in this version, or it belongs to a module your configuration does not import.":
    "Sie wurde in dieser Version möglicherweise umbenannt oder entfernt, oder sie gehört zu einem Modul, das Ihre Konfiguration nicht einbindet.",
  "It may have been renamed or removed in this version, or it belongs to a module your configuration does not import. It is set in {files}.":
    "Sie wurde in dieser Version möglicherweise umbenannt oder entfernt, oder sie gehört zu einem Modul, das Ihre Konfiguration nicht einbindet. Sie ist in {files} festgelegt.",
  "{option} has a value it does not accept": "{option} hat einen Wert, den es nicht akzeptiert",
  "It expects {type}.": "Erwartet wird {type}.",
  "It expects {type}. It is set in {files}.": "Erwartet wird {type}. Der Wert ist in {files} festgelegt.",
  "{option} is set to different values": "{option} hat verschiedene Werte",
  "Only one value can apply. Remove all but one of them.": "Es kann nur ein Wert gelten. Entfernen Sie alle bis auf einen.",
  "Only one value can apply. It is set in {files}. Remove all but one of them.":
    "Es kann nur ein Wert gelten. Er ist in {files} festgelegt. Entfernen Sie alle bis auf einen.",
  "{option} needs a value": "{option} braucht einen Wert",
  "Nothing in your configuration sets it, and it has no default. Set it, or remove what depends on it.":
    "Nichts in Ihrer Konfiguration legt einen Wert fest, und es gibt keinen Standardwert. Legen Sie einen fest oder entfernen Sie, was davon abhängt.",
  "The configuration reports a problem": "Die Konfiguration meldet ein Problem",
  "The configuration reports {count} problems": "Die Konfiguration meldet {count} Probleme",
  "A file is in the way": "Eine Datei steht im Weg",
  "{count} files are in the way": "{count} Dateien stehen im Weg",
  "Home Manager manages {files}, but a file already exists there. Move it somewhere else or delete it, then apply again.":
    "Home Manager verwaltet {files}, aber dort existiert bereits eine Datei. Verschieben Sie sie an eine andere Stelle oder löschen Sie sie und wenden Sie die Konfiguration dann erneut an.",
  "Home Manager manages {files}, but files already exist there. Move them somewhere else or delete them, then apply again.":
    "Home Manager verwaltet {files}, aber dort existieren bereits Dateien. Verschieben Sie sie an eine andere Stelle oder löschen Sie sie und wenden Sie die Konfiguration dann erneut an.",
  "Two packages contain the same file": "Zwei Pakete enthalten dieselbe Datei",
  "{first} and {second} both provide {file}. Remove one of them.":
    "{first} und {second} liefern beide {file}. Entfernen Sie eines davon.",
  "Building {name} failed": "{name} konnte nicht gebaut werden",
  "Its build log shows why. When the package comes from nixpkgs, this is rarely caused by your settings: try again after an update, or remove it for now.":
    "Das Build-Protokoll zeigt, warum. Stammt das Paket aus nixpkgs, liegt es selten an Ihren Einstellungen: Versuchen Sie es nach einer Aktualisierung erneut oder entfernen Sie es vorerst.",
  "A download failed": "Ein Download ist fehlgeschlagen",
  "Check the internet connection and try again. If it keeps failing, the download server may be unavailable.":
    "Prüfen Sie die Internetverbindung und versuchen Sie es erneut. Schlägt der Download weiter fehl, ist der Download-Server möglicherweise nicht erreichbar.",
  "{name} does not exist": "{name} gibt es nicht",
  "Your configuration refers to {name}, which does not exist. Check the spelling.":
    "Ihre Konfiguration verweist auf {name}, das es nicht gibt. Prüfen Sie die Schreibweise.",
  "Your configuration refers to {name}, which does not exist. Check the spelling; similar names are {names}.":
    "Ihre Konfiguration verweist auf {name}, das es nicht gibt. Prüfen Sie die Schreibweise; ähnliche Namen sind {names}.",

  // Preparing and undoing edits (src/edits.ts)
  "There are no pending edits.": "Es gibt keine vorgemerkten Änderungen.",
  "{attr} is not a package from pkgs.": "{attr} ist kein Paket aus pkgs.",
  "{problem} (and {count} more problems)": "{problem} (und {count} weitere Probleme)",
  "The pending edits do not change any file.": "Die vorgemerkten Änderungen ändern keine Datei.",
  "{path} already exists.": "{path} existiert bereits.",
  "{path} is not tracked by Git, so Nix cannot read it. Add it with git add first.":
    "{path} wird nicht von Git verfolgt, daher kann Nix die Datei nicht lesen. Fügen Sie sie zuerst mit git add hinzu.",
  "{path} changed while the edits were being prepared.": "{path} hat sich geändert, während die Änderungen vorbereitet wurden.",
  "The pending edits change a configuration this review does not build.":
    "Die vorgemerkten Änderungen betreffen eine Konfiguration, die diese Prüfung nicht baut.",
  "Setting nixpkgs.config in {name} also changes {changed}.": "Das Setzen von nixpkgs.config in {name} ändert auch {changed}.",
  "This setting would change more than it says": "Diese Einstellung würde mehr ändern, als sie sagt",
  "In {name}, nixpkgs.config in a module makes the configuration import nixpkgs again, which also changes {changed}. Set {settings} where the flake imports nixpkgs instead, for example import nixpkgs { config.{setting} = true; } in flake.nix.":
    "In {name} sorgt nixpkgs.config in einem Modul dafür, dass die Konfiguration nixpkgs erneut importiert, was auch {changed} ändert. Setzen Sie stattdessen {settings} dort, wo der Flake nixpkgs importiert, zum Beispiel mit import nixpkgs { config.{setting} = true; } in flake.nix.",
  "{option} is not an option of {name}.": "{option} ist keine Option von {name}.",
  "{option} no longer evaluates after the edit.": "{option} lässt sich nach der Änderung nicht mehr auswerten.",
  "{option} is {now} instead of {wanted}. Another definition takes precedence, or {file} is not part of {name}.":
    "{option} ist {now} statt {wanted}. Eine andere Definition hat Vorrang, oder {file} gehört nicht zu {name}.",
  "{option} is also set elsewhere": "{option} wird auch an anderer Stelle festgelegt",
  "{files} sets {option} too, and that value wins. Change it there instead of adding a new definition.":
    "{files} legt ebenfalls {option} fest, und dieser Wert gilt. Ändern Sie ihn dort, statt eine neue Definition hinzuzufügen.",
  "{files} set {option} too, and that value wins. Change it there instead of adding a new definition.":
    "{files} legen ebenfalls {option} fest, und dieser Wert gilt. Ändern Sie ihn dort, statt eine neue Definition hinzuzufügen.",
  "{file} is not part of {name}, so {option} is unchanged.": "{file} gehört nicht zu {name}, daher bleibt {option} unverändert.",
  "{option} is set in {file}, but {name} takes its packages from a nixpkgs the flake imports itself.":
    "{option} ist in {file} festgelegt, aber {name} bezieht seine Pakete aus einem nixpkgs, das der Flake selbst importiert.",
  "This setting does not reach your packages": "Diese Einstellung erreicht Ihre Pakete nicht",
  "{name} gets its packages from a nixpkgs that the flake imports itself, so nixpkgs.config in modules has no effect. Set {setting} where nixpkgs is imported instead, for example import nixpkgs { config.{setting} = {value}; } in flake.nix.":
    "{name} bezieht seine Pakete aus einem nixpkgs, das der Flake selbst importiert, daher hat nixpkgs.config in Modulen keine Wirkung. Setzen Sie {setting} stattdessen dort, wo nixpkgs importiert wird, zum Beispiel mit import nixpkgs { config.{setting} = {value}; } in flake.nix.",
  "{option} is still set in {file}.": "{option} ist in {file} weiterhin festgelegt.",
  "{attr} cannot be added to {name}: {reason}.": "{attr} kann nicht zu {name} hinzugefügt werden: {reason}.",
  "{attr} does not exist in {name}'s packages.": "{attr} gibt es in den Paketen von {name} nicht.",
  "{attr} was added to {file}, but {name} does not include it. Is the file part of this configuration?":
    "{attr} wurde zu {file} hinzugefügt, aber {name} enthält es nicht. Gehört die Datei zu dieser Konfiguration?",
  "Removing {attr} from {file} does not remove it from {name}.": "Das Entfernen von {attr} aus {file} entfernt es nicht aus {name}.",
  "{path} was created after this review was prepared. Prepare the edits again.":
    "{path} wurde erstellt, nachdem diese Prüfung vorbereitet wurde. Bereiten Sie die Änderungen erneut vor.",
  "{path} changed after this review was prepared. Prepare the edits again.":
    "{path} hat sich geändert, nachdem diese Prüfung vorbereitet wurde. Bereiten Sie die Änderungen erneut vor.",
  "Write edited files: {error}": "Geänderte Dateien schreiben: {error}",
  "The earlier content of {path} is missing.": "Der frühere Inhalt von {path} fehlt.",
  "{path} changed after the change was applied, so it cannot be put back automatically.":
    "{path} hat sich geändert, nachdem die Änderung angewendet wurde, und kann daher nicht automatisch zurückgesetzt werden.",
  "{path} was edited since": "{path} wurde seitdem bearbeitet",
  "Undo puts back the files exactly as they were and would lose those edits. Go back on the Restore points page instead, which leaves your files as they are.":
    "„Rückgängig machen“ stellt die Dateien genau so wieder her, wie sie waren, und würde diese Änderungen verlieren. Kehren Sie stattdessen auf der Seite „Wiederherstellungspunkte“ zurück; dabei bleiben Ihre Dateien, wie sie sind.",
  "Restore files: {error}": "Dateien wiederherstellen: {error}",

  // Pending edits in words (src/changes.ts)
  on: "an",
  off: "aus",
  "not set": "nicht gesetzt",
  empty: "leer",
  "{option} on": "{option} an",
  "{option} off": "{option} aus",
  "{option} not set": "{option} nicht gesetzt",
  "{option} empty": "{option} leer",
  "{label}: {option} on": "{label}: {option} an",
  "{label}: {option} off": "{label}: {option} aus",
  "{label}: {option} not set": "{label}: {option} nicht gesetzt",
  "{label}: {option} empty": "{label}: {option} leer",
  "Install {name}": "{name} installieren",
  "Remove {name}": "{name} entfernen",
  "Reset {option} to its default": "{option} auf den Standardwert zurücksetzen",
  "Create {name}": "{name} erstellen",
  "{file}, imported from {importer}": "{file}, eingebunden von {importer}",
  "Use a path inside the flake ending in .nix, with letters, digits, dots, dashes and slashes.":
    "Verwenden Sie einen Pfad innerhalb des Flakes, der auf .nix endet, mit Buchstaben, Ziffern, Punkten, Bindestrichen und Schrägstrichen.",
  "This file exists already.": "Diese Datei gibt es bereits.",
  "There is nothing to install.": "Es gibt nichts zu installieren.",
  "It changes the computer itself, not just your apps.": "Es ändert den Computer selbst, nicht nur Ihre Apps.",
  "It changes settings, which can restart programs you are using.":
    "Es ändert Einstellungen, wodurch Programme, die Sie benutzen, neu starten können.",
  "It is not a change of your apps.": "Es ist keine Änderung Ihrer Apps.",

  // Operations (src/manager.ts, src/process.ts)
  "Update {names}": "{names} aktualisieren",
  "Update all inputs": "Alle Softwarequellen aktualisieren",
  "Undo: {changes}": "Rückgängig: {changes}",
  "Go back to restore point {generation} of your user": "Zum Wiederherstellungspunkt {generation} Ihres Benutzers zurückkehren",
  "Go back to restore point {generation} of the system": "Zum Wiederherstellungspunkt {generation} des Systems zurückkehren",
  "Apply the configuration": "Konfiguration anwenden",
  "Another nix-manager operation is running. Wait for it to finish.":
    "Ein anderer nix-manager-Vorgang läuft. Warten Sie, bis er beendet ist.",
  "Invalid operation ID.": "Ungültige Vorgangs-ID.",
  "This change cannot be undone: it recorded nothing to go back to.":
    "Diese Änderung lässt sich nicht rückgängig machen: Es wurde nichts aufgezeichnet, zu dem man zurückkehren könnte.",
  "An operation is already running.": "Es läuft bereits ein Vorgang.",
  "Save update report: {error}": "Aktualisierungsbericht speichern: {error}",
  "Some update checks failed. The report contains the successful results and errors.":
    "Einige Aktualisierungsprüfungen sind fehlgeschlagen. Der Bericht enthält die erfolgreichen Ergebnisse und die Fehler.",
  "{label} failed": "Fehlgeschlagen: {label}",
  "Applying failed": "Anwenden fehlgeschlagen",
  "{what} The files were put back, but there was no earlier restore point of your user to go back to, so the new version stays in use.":
    "{what} Die Dateien wurden zurückgesetzt, aber es gab keinen früheren Wiederherstellungspunkt Ihres Benutzers, zu dem man zurückkehren könnte, daher bleibt die neue Version in Gebrauch.",
  "{what} There was no earlier restore point of your user to go back to, so the new version stays in use.":
    "{what} Es gab keinen früheren Wiederherstellungspunkt Ihres Benutzers, zu dem man zurückkehren könnte, daher bleibt die neue Version in Gebrauch.",
  "{what} Everything went back to how it was before, files included.":
    "{what} Alles ist wieder so, wie es vorher war, einschließlich der Dateien.",
  "{what} Everything went back to how it was before.": "{what} Alles ist wieder so, wie es vorher war.",
  "{what} Going back did not work either: {reason} The Restore points page can take you back.":
    "{what} Auch die Rückkehr hat nicht funktioniert: {reason} Die Seite „Wiederherstellungspunkte“ kann Sie zurückbringen.",
  "It could not be applied: {reason}": "Es konnte nicht angewendet werden: {reason}",
  "You chose to review it first.": "Sie haben gewählt, es zuerst zu prüfen.",
  "Cannot start {command}: {error}": "{command} kann nicht gestartet werden: {error}",
  "{label} failed (exit {code}). {output}": "Fehlgeschlagen: {label} (Exit-Code {code}). {output}",
};
