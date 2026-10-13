// Text stored in English with values filled in, like step labels, and
// translated where it is shown by matching it against these keys. Only keys
// here are matched that way; anywhere else a placeholder could match words.
export const labels: Record<string, string> = {
  "Remove user {name}": "Benutzer {name} entfernen",
  "Add user {name}": "Benutzer {name} hinzufügen",
  "User {name}": "Benutzer {name}",
  // Actions
  "Build configuration": "Konfiguration bauen",
  "Build and switch": "Bauen und anwenden",
  "Apply on next boot": "Beim nächsten Start anwenden",
  "Go back to the previous version": "Zur vorherigen Version zurückkehren",
  "Remove old restore points": "Alte Wiederherstellungspunkte entfernen",
  "Check for updates": "Nach Aktualisierungen suchen",
  "Update T3 Code": "T3 Code aktualisieren",
  "Update OpenCode": "OpenCode aktualisieren",
  "Update PINCE": "PINCE aktualisieren",
  "Prepare changes": "Änderungen vorbereiten",
  "Apply changes": "Änderungen anwenden",

  // Steps
  "Create the folder for home generations": "Ordner für Wiederherstellungspunkte der Benutzerkonfiguration anlegen",
  "Select Home Manager generation": "Wiederherstellungspunkt der Benutzerkonfiguration auswählen",
  "Activate Home Manager": "Benutzerkonfiguration aktivieren",
  "Activate system": "System aktivieren",
  "Activate system on next boot": "System beim nächsten Start aktivieren",
  "Keep {count} home generations": "{count} Wiederherstellungspunkte der Benutzerkonfiguration behalten",
  "Keep {count} system generations and collect garbage": "{count} Wiederherstellungspunkte des Systems behalten und Unbenutztes löschen",
  "Collect unreferenced store paths": "Nicht mehr benutzte Dateien im Nix-Store löschen",
  "Retain home generation {generation}": "Wiederherstellungspunkt {generation} der Benutzerkonfiguration bereitstellen",
  "Retain system generation {generation}": "Wiederherstellungspunkt {generation} des Systems bereitstellen",
  "Stage the edited files": "Geänderte Dateien bereitstellen",
  "Check the edits": "Änderungen prüfen",
  "Resolve {input}": "{input} auflösen",
  "Resolve {count} inputs": "{count} Softwarequellen auflösen",
  "Resolve all flake inputs": "Alle Softwarequellen auflösen",
  "Build home configuration": "Benutzerkonfiguration bauen",
  "Build system configuration": "Systemkonfiguration bauen",
  "Compare with the active configuration": "Mit der aktiven Konfiguration vergleichen",
  "Write {file}": "{file} schreiben",
  "Write {count} edited files": "{count} geänderte Dateien schreiben",
  "Write the reviewed flake.lock": "Geprüfte flake.lock schreiben",
  "Restore {file}": "{file} wiederherstellen",
  "Restore {count} files": "{count} Dateien wiederherstellen",
  "Update flake inputs": "Softwarequellen aktualisieren",
  "Roll back home configuration": "Benutzerkonfiguration zurücksetzen",
  "Roll back system configuration": "Systemkonfiguration zurücksetzen",
  "Go back to system generation {generation}": "Zum Wiederherstellungspunkt {generation} des Systems zurückkehren",
  "Go back to home generation {generation}": "Zum Wiederherstellungspunkt {generation} der Benutzerkonfiguration zurückkehren",
  "Activate the earlier home generation": "Frühere Benutzerkonfiguration aktivieren",

  // Notes on a plan
  "No Home Manager profile at {path}; generation deletion is skipped.":
    "Unter {path} gibt es kein Home-Manager-Profil; es werden keine Wiederherstellungspunkte der Benutzerkonfiguration entfernt.",
  "Garbage collection deletes unreferenced store paths and older generations; these cannot be rolled back afterward.":
    "Beim Aufräumen werden nicht mehr benutzte Dateien und ältere Wiederherstellungspunkte gelöscht; zu ihnen kann man danach nicht mehr zurückkehren.",
  "Applying the undo also restores the configuration files the change wrote.":
    "Beim Anwenden werden auch die Konfigurationsdateien wiederhergestellt, die die Änderung geschrieben hat.",
  "Inputs are resolved into a separate lock file. The checkout's flake.lock changes only when you apply this review.":
    "Die Softwarequellen werden in eine eigene Sperrdatei aufgelöst. Die flake.lock im Konfigurationsordner ändert sich erst, wenn Sie diese Prüfung anwenden.",
  "Edits are built from a copy of the checkout. Your files change only when you apply this review.":
    "Die Änderungen werden aus einer Kopie des Konfigurationsordners gebaut. Ihre Dateien ändern sich erst, wenn Sie diese Prüfung anwenden.",
  "Nothing is activated. The build is kept until you apply or discard it.":
    "Es wird nichts aktiviert. Der Build bleibt erhalten, bis Sie ihn anwenden oder verwerfen.",
  "Home activates before system. If system activation fails, home remains active.":
    "Die Benutzerkonfiguration wird vor dem System aktiviert. Schlägt die Aktivierung des Systems fehl, bleibt die Benutzerkonfiguration aktiv.",
  "Home activates before system. If system activation fails, home may already be active.":
    "Die Benutzerkonfiguration wird vor dem System aktiviert. Schlägt die Aktivierung des Systems fehl, kann die Benutzerkonfiguration schon aktiv sein.",
  "The exact reviewed outputs are activated; the checkout is not evaluated again.":
    "Genau die geprüften Builds werden aktiviert; die Konfiguration wird nicht neu ausgewertet.",
  "Resolve inputs in a temporary copy and compare stable package releases. The checkout is unchanged.":
    "Löst die Softwarequellen in einer temporären Kopie auf und vergleicht stabile Paketversionen. Der Konfigurationsordner bleibt unverändert.",
  "Compare stable release and inherited recipes, discover hashes, then build before atomically replacing the pin.":
    "Vergleicht die stabile Version mit den geerbten Rezepten, ermittelt Prüfsummen und baut, bevor die festgelegte Version in einem Schritt ersetzt wird.",
  "Once the privileged step starts, cancellation waits for it to finish before stopping subsequent steps.":
    "Hat der Schritt mit Administratorrechten begonnen, wartet der Abbruch auf sein Ende und hält erst die folgenden Schritte an.",
  "The change to undo is unavailable.": "Die Änderung, die rückgängig gemacht werden soll, ist nicht mehr verfügbar.",
  "The change did not record the home generation it replaced.":
    "Die Änderung hat den Wiederherstellungspunkt der Benutzerkonfiguration, den sie ersetzt hat, nicht festgehalten.",
  "The change did not record the system generation it replaced.":
    "Die Änderung hat den Wiederherstellungspunkt des Systems, den sie ersetzt hat, nicht festgehalten.",
  "The selected review is unavailable.": "Die ausgewählte Prüfung ist nicht mehr verfügbar.",
  "Only a system configuration can be applied on next boot.": "Nur eine Systemkonfiguration kann beim nächsten Start angewendet werden.",
  "This review does not contain the selected configuration.": "Diese Prüfung enthält die ausgewählte Konfiguration nicht.",

  // Setup assistant
  "Copy the configuration from /etc/nixos": "Konfiguration aus /etc/nixos kopieren",
  "Add flake.nix and home.nix": "flake.nix und home.nix hinzufügen",
  "Start a Git history": "Git-Verlauf beginnen",
  "Pin nixpkgs to the version this computer runs": "nixpkgs auf die Version festlegen, die auf diesem Computer läuft",
  "Check that the configuration still describes this computer": "Prüfen, ob die Konfiguration weiterhin diesen Computer beschreibt",
  "Remember the folder": "Ordner merken",
  "This does not look like NixOS: nixos-version is missing.": "Das sieht nicht nach NixOS aus: nixos-version fehlt.",
  "There is no {file} to start from.": "Es gibt keine {file}, mit der begonnen werden kann.",
  "{folder} is a flake already. Copy it to a folder you own and add a homeConfigurations entry for your user, then choose that folder with --flake.":
    "{folder} ist bereits ein Flake. Kopieren Sie ihn in einen eigenen Ordner, fügen Sie für Ihren Benutzer einen Eintrag unter homeConfigurations hinzu und wählen Sie diesen Ordner dann mit --flake.",
  "Use a full path, such as ~/nixos-config.": "Geben Sie einen vollständigen Pfad an, etwa ~/nixos-config.",
  "Choose a folder inside your home folder, so you own the files.": "Wählen Sie einen Ordner in Ihrem persönlichen Ordner, damit die Dateien Ihnen gehören.",
  "Choose a new folder inside your home folder.": "Wählen Sie einen neuen Ordner in Ihrem persönlichen Ordner.",
  "This folder is not empty.": "Dieser Ordner ist nicht leer.",
  "Could not pin the inputs: {reason}": "Die Softwarequellen konnten nicht festgelegt werden: {reason}",
  "Check inputs and package releases": "Softwarequellen und Paketversionen prüfen",
  "Update the {name} package pin": "Festgelegte Version von {name} aktualisieren",
  "Put back the configuration files": "Konfigurationsdateien zurücklegen",
};
