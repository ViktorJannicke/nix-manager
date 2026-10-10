<script lang="ts">
  import BluetoothIcon from "@lucide/svelte/icons/bluetooth";
  import ClockIcon from "@lucide/svelte/icons/clock";
  import KeyboardIcon from "@lucide/svelte/icons/keyboard";
  import LanguagesIcon from "@lucide/svelte/icons/languages";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import LogInIcon from "@lucide/svelte/icons/log-in";
  import MonitorIcon from "@lucide/svelte/icons/monitor";
  import PrinterIcon from "@lucide/svelte/icons/printer";
  import ShieldIcon from "@lucide/svelte/icons/shield";
  import TriangleAlertIcon from "@lucide/svelte/icons/triangle-alert";
  import UserPlusIcon from "@lucide/svelte/icons/user-plus";
  import UsersIcon from "@lucide/svelte/icons/users";
  import Volume2Icon from "@lucide/svelte/icons/volume-2";
  import { Button } from "$lib/components/ui/button";
  import { Switch } from "$lib/components/ui/switch";
  import { app } from "$lib/app.svelte";
  import { parseDefault } from "$lib/fields";
  import { plural } from "$lib/format";
  import { local, NEW_FILE, showValue, software } from "$lib/software.svelte";
  import NewModuleFields from "$lib/components/app/NewModuleFields.svelte";
  import OptionField from "$lib/components/app/OptionField.svelte";
  import PendingEdits from "$lib/components/app/PendingEdits.svelte";
  import { ADMIN_GROUP, computerSections, inFlakeFile, keyboardLayouts, NEW_USER_GROUPS, userNameProblem, type UserAccount } from "../../src/computer";
  import type { Change } from "../../src/draft";

  const SCOPE = "system";
  const icons: Record<string, typeof MonitorIcon> = {
    monitor: MonitorIcon,
    clock: ClockIcon,
    languages: LanguagesIcon,
    keyboard: KeyboardIcon,
    "log-in": LogInIcon,
    "volume-2": Volume2Icon,
    printer: PrinterIcon,
    bluetooth: BluetoothIcon,
    shield: ShieldIcon,
  };
  const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);
  const userOption = (name: string, setting?: string) => `users.users.${name}${setting ? `.${setting}` : ""}`;

  $effect(() => {
    void app.host;
    void app.system?.repository.fingerprint;
    void software.load();
  });

  const inventory = $derived(software.inventory?.system);
  const sections = $derived(
    computerSections
      .map((section) => ({ ...section, settings: section.settings.filter((setting) => inventory?.options[setting.option]?.exists) }))
      .filter((section) => section.settings.length),
  );
  $effect(() => {
    if (inventory) void software.describe(SCOPE, sections.flatMap((section) => section.settings.map((setting) => setting.option)));
  });

  /** Edits made on this page and not yet added to the pending edits, by option. */
  let edits = $state<Record<string, { value: unknown } | { remove: true }>>({});

  /** The value now, with a pending edit taking precedence; `fallback` for settings the inventory does not read. */
  function current(option: string, fallback?: unknown) {
    const { value, pending } = software.value(SCOPE, option);
    if (pending) return value;
    if (value !== undefined) return value;
    return fallback !== undefined ? fallback : parseDefault(software.doc(SCOPE, option)?.df);
  }
  function shown(option: string, fallback?: unknown) {
    const edit = edits[option];
    return edit && "value" in edit ? edit.value : current(option, fallback);
  }
  function set(option: string, value: unknown, fallback?: unknown) {
    if (same(value, current(option, fallback))) delete edits[option];
    else edits[option] = { value };
  }

  // --- Users ------------------------------------------------------------------

  const NEW_USER = /^users\.users\.([a-z_][a-z0-9_-]*)\.isNormalUser$/;
  /** Users the configuration has, then users that pending edits or this page add. */
  const accounts = $derived.by(() => {
    const known = inventory?.users ?? [];
    const added = [...software.changes.flatMap((change) => (change.kind === "set-option" ? [change.option] : [])), ...Object.keys(edits)]
      .map((option) => option.match(NEW_USER)?.[1])
      .filter((name): name is string => Boolean(name) && !known.some((user) => user.name === name));
    return [
      ...known.map((user) => ({ ...user, added: false })),
      ...[...new Set(added)].map((name): UserAccount & { added: boolean } => ({ name, description: null, groups: [], files: [], added: true })),
    ];
  });
  const groups = (user: UserAccount) => (shown(userOption(user.name, "extraGroups"), user.groups) as string[] | undefined) ?? [];
  const isAdmin = (user: UserAccount) => groups(user).includes(ADMIN_GROUP);
  const removing = (user: UserAccount) => Boolean(edits[userOption(user.name)]);
  const admins = $derived(accounts.filter((user) => isAdmin(user) && !removing(user)));
  /** The one flake file a user is defined in, which edits of that user go to. */
  const userFile = (user: UserAccount) => (user.files.length === 1 && local(user.files[0]) ? user.files[0] : undefined);
  /** Where new users go: a file an existing user is defined in, preferring one named for users. */
  const usersFile = $derived(
    accounts.map(userFile).find(Boolean) ?? accounts.flatMap((user) => user.files.filter(local)).sort((a, b) => Number(/user/.test(b)) - Number(/user/.test(a)))[0],
  );
  const mutable = $derived(current("users.mutableUsers") !== false);

  function removeProblem(user: UserAccount) {
    if (user.name === app.config?.user) return "You cannot remove the user you are logged in as.";
    if (isAdmin(user) && admins.length === 1) return "Make another user an administrator first.";
    if (!userFile(user)) return `Set up in ${user.files.length ? user.files.join(", ") : "more than one place"}. Remove it in those files.`;
    return undefined;
  }
  function setAdmin(user: UserAccount, admin: boolean) {
    const next = admin ? [...groups(user), ADMIN_GROUP] : groups(user).filter((group) => group !== ADMIN_GROUP);
    set(userOption(user.name, "extraGroups"), next, user.groups);
  }
  function toggleRemove(user: UserAccount & { added: boolean }) {
    if (user.added) {
      // A user added on this page is simply forgotten again.
      for (const option of Object.keys(edits)) if (option.startsWith(`${userOption(user.name)}.`)) delete edits[option];
      return;
    }
    if (removing(user)) delete edits[userOption(user.name)];
    else {
      for (const option of Object.keys(edits)) if (option.startsWith(`${userOption(user.name)}.`)) delete edits[option];
      edits[userOption(user.name)] = { remove: true };
    }
  }

  let adding = $state(false);
  let newName = $state("");
  let newDescription = $state("");
  let newAdmin = $state(false);
  let created = $state<string>();
  const nameProblem = $derived(userNameProblem(newName, accounts.map((user) => user.name)));
  function addUser() {
    if (nameProblem) return;
    edits[userOption(newName, "isNormalUser")] = { value: true };
    if (newDescription.trim()) edits[userOption(newName, "description")] = { value: newDescription.trim() };
    edits[userOption(newName, "extraGroups")] = { value: [...NEW_USER_GROUPS, ...(newAdmin ? [ADMIN_GROUP] : [])] };
    adding = false;
    newName = newDescription = "";
    newAdmin = false;
  }

  // --- Saving -----------------------------------------------------------------

  const changed = $derived(Object.keys(edits));
  /** The file an edit of `option` goes to, when it already has one. */
  function home(option: string) {
    const name = option.match(/^users\.users\.([^.]+)/)?.[1];
    if (name) {
      const user = accounts.find((item) => item.name === name);
      return (user && userFile(user)) ?? usersFile;
    }
    return software.definedIn(SCOPE, option);
  }
  const homeless = $derived(changed.filter((option) => !home(option)));
  let target = $state("");
  let newFile = $state("");
  let importer = $state("");
  const modules = $derived(homeless.length ? software.moduleFiles(SCOPE, homeless[0]) : []);
  $effect(() => {
    if (!homeless.length || target) return;
    // Next to similar settings when the host has them, else a new file for the computer.
    target = modules.find((module) => module.active)?.file ?? software.featureFile(SCOPE, homeless[0], "computer");
    newFile = software.suggestFile(SCOPE, "computer", modules.find((module) => module.active !== false)?.file);
  });
  const creating = $derived(homeless.length > 0 && target === NEW_FILE);
  const ready = $derived(!homeless.length || (creating ? !software.newFileProblem(SCOPE, newFile) && Boolean(importer) : Boolean(target)));

  function label(option: string) {
    const name = option.match(/^users\.users\.([^.]+)/)?.[1];
    if (name) {
      const user = accounts.find((item) => item.name === name);
      return edits[userOption(name)] ? `Remove user ${name}` : user?.added ? `Add user ${name}` : `User ${name}`;
    }
    return computerSections.find((section) => section.settings.some((setting) => setting.option === option))?.title ?? "Computer";
  }

  /** A setting as the page names it: its label, or the user it belongs to. */
  function name(option: string) {
    const user = option.match(/^users\.users\.([^.]+)/)?.[1];
    if (user) return `user ${user}`;
    return computerSections.flatMap((section) => section.settings).find((setting) => setting.option === option)?.label.toLowerCase() ?? option;
  }

  async function save() {
    const file = creating ? newFile : target;
    const changes: Change[] = creating && homeless.length ? [{ kind: "create-module", scope: SCOPE, file, importer, label: "Computer" }] : [];
    for (const [option, edit] of Object.entries(edits)) {
      const where = home(option) ?? file;
      changes.push(
        "remove" in edit
          ? { kind: "unset-option", scope: SCOPE, file: where, option, label: label(option) }
          : { kind: "set-option", scope: SCOPE, file: where, option, value: edit.value as never, label: label(option) },
      );
    }
    if (homeless.length && !creating) software.remember(SCOPE, target);
    const newUsers = accounts.filter((user) => user.added && edits[userOption(user.name, "isNormalUser")]).map((user) => user.name);
    await software.stage(changes);
    created = newUsers[0];
    app.toast({ tone: "success", title: `${plural(changes.length, "edit")} pending`, body: "Prepare your pending edits to build them." });
    edits = {};
    target = "";
  }
</script>

<div class="max-w-4xl space-y-6 pb-24">
  <header>
    <h1 class="title">Computer</h1>
    <p class="mt-1 text-muted-foreground">Settings for everyone who uses {app.host}. Changes wait in your pending edits until you prepare and apply them.</p>
  </header>

  <PendingEdits />

  {#if created && mutable}
    <p class="flex items-start gap-2 rounded-lg bg-muted px-3 py-2 text-xs">
      <UserPlusIcon class="mt-0.5 size-4 shrink-0" />
      <span class="flex-1">After you apply, give {created} a password in your desktop's user settings, or run <code>sudo passwd {created}</code>. Until then {created} cannot log in.</span>
      <button class="focus-ring rounded text-muted-foreground hover:text-foreground" onclick={() => (created = undefined)}>Dismiss</button>
    </p>
  {/if}

  {#if software.error}
    <p class="flex gap-2 rounded-lg bg-destructive/10 px-3 py-2 text-destructive">
      <TriangleAlertIcon class="mt-0.5 size-4 shrink-0" />
      <span class="flex-1">Could not read {app.host}'s configuration. {software.error}</span>
      <Button variant="ghost" size="sm" onclick={() => software.load(true)}>Try again</Button>
    </p>
  {:else if !inventory}
    <p class="flex items-center gap-2 text-muted-foreground"><LoaderIcon class="size-4 animate-spin" /> Reading {app.host}'s settings</p>
  {:else}
    {#snippet section(item: (typeof sections)[number])}
      {@const Icon = icons[item.icon] ?? MonitorIcon}
      <section class="panel" aria-labelledby="computer-{item.id}">
        <header class="flex items-start gap-3 p-4 pb-2">
          <Icon class="mt-0.5 size-5 shrink-0 text-muted-foreground" />
          <div>
            <h2 id="computer-{item.id}" class="font-semibold">{item.title}</h2>
            <p class="text-xs text-muted-foreground">{item.summary}</p>
          </div>
        </header>
        <div class="divide-y px-4">
          {#each item.settings.filter((setting) => !setting.when || shown(setting.when) === true) as setting (setting.option)}
            {@const files = (inventory.options[setting.option]?.files ?? []).filter(local)}
            {@const fixed = (inventory.options[setting.option]?.files ?? []).find((file) => inFlakeFile(file) || (local(file) && !inventory.modules.some((module) => module.file === file)))}
            <div class="flex flex-wrap items-center gap-x-4 gap-y-2 py-3">
              <div class="min-w-48 flex-1">
                <p class="font-medium">
                  {setting.label}
                  {#if edits[setting.option]}<span class="ml-2 text-xs font-normal text-primary">changed</span>
                  {:else if software.value(SCOPE, setting.option).pending}<span class="ml-2 text-xs font-normal text-prepared-foreground">pending</span>{/if}
                </p>
                {#if setting.help}<p class="text-xs text-muted-foreground">{setting.help}</p>{/if}
                {#if fixed}<p class="text-[0.7rem] break-all text-muted-foreground">{inFlakeFile(fixed) ? "Set directly in flake.nix" : `Set in ${fixed}, which Nix Manager cannot edit`}. Change it there.</p>
                {:else if files.length}<p class="text-[0.7rem] break-all text-muted-foreground">Set in {files.join(", ")}</p>{/if}
              </div>
              {#if fixed}<span class="max-w-56 truncate text-xs">{setting.input === "keyboard" ? (keyboardLayouts.find((layout) => layout.code === shown(setting.option))?.label ?? shown(setting.option)) : showValue(shown(setting.option)).replace(/^"(.*)"$/, "$1")}</span>
              {:else}<OptionField
                label={setting.label}
                doc={software.doc(SCOPE, setting.option)}
                value={shown(setting.option)}
                input={setting.input}
                users={accounts.filter((user) => !removing(user)).map((user) => user.name)}
                onchange={(value) => set(setting.option, value)}
              />{/if}
            </div>
          {/each}
        </div>
      </section>
    {/snippet}

    {#each sections.slice(0, 1) as item (item.id)}{@render section(item)}{/each}

    {#if inventory.users}
      <section class="panel" aria-labelledby="computer-users">
        <header class="flex items-start gap-3 p-4 pb-2">
          <UsersIcon class="mt-0.5 size-5 shrink-0 text-muted-foreground" />
          <div class="flex-1">
            <h2 id="computer-users" class="font-semibold">Users</h2>
            <p class="text-xs text-muted-foreground">People who can log in. Administrators can install software and change these settings.</p>
          </div>
          {#if !adding}<Button variant="outline" size="sm" onclick={() => (adding = true)}><UserPlusIcon /> Add user</Button>{/if}
        </header>
        <ul class="divide-y px-4">
          {#each accounts as user (user.name)}
            {@const problem = removeProblem(user)}
            {@const gone = removing(user)}
            {@const details = [(user.description || shown(userOption(user.name, "description"), null) || user.name) !== user.name ? user.name : "", userFile(user) ? `set in ${userFile(user)}` : ""].filter(Boolean)}
            <li class="flex flex-wrap items-center gap-x-4 gap-y-2 py-3">
              <div class="min-w-48 flex-1 {gone ? 'opacity-60' : ''}">
                <p class="font-medium {gone ? 'line-through' : ''}">
                  {user.description ?? shown(userOption(user.name, "description"), null) ?? user.name}
                  {#if user.name === app.config?.user}<span class="ml-2 text-xs font-normal text-muted-foreground">you</span>{/if}
                  {#if user.added}<span class="ml-2 text-xs font-normal text-primary">new</span>{/if}
                  {#if gone}<span class="ml-2 text-xs font-normal text-destructive">will be removed</span>{/if}
                </p>
                {#if details.length}<p class="text-xs text-muted-foreground">{details.join(", ")}</p>{/if}
              </div>
              <label class="flex items-center gap-2 text-xs">
                Administrator
                <Switch
                  checked={isAdmin(user)}
                  disabled={gone || (isAdmin(user) && admins.length === 1)}
                  onCheckedChange={(checked) => setAdmin(user, checked)}
                  aria-label="{user.name} is an administrator"
                />
              </label>
              {#if !user.added || edits[userOption(user.name, "isNormalUser")]}<Button variant="ghost" size="sm" disabled={!user.added && !gone && Boolean(problem)} title={user.added || gone ? undefined : problem} onclick={() => toggleRemove(user)}>
                {gone ? "Keep" : "Remove"}
              </Button>{/if}
            </li>
          {/each}
        </ul>
        {#if adding}
          <form class="m-4 mt-0 space-y-3 rounded-lg border border-dashed p-3" onsubmit={(event) => { event.preventDefault(); addUser(); }}>
            <div class="grid gap-3 sm:grid-cols-2">
              <label class="block space-y-1">
                <span class="text-xs font-medium">Full name</span>
                <input class="focus-ring h-8 w-full rounded-md border bg-card px-2 text-xs" bind:value={newDescription} placeholder="Alex Doe" />
              </label>
              <label class="block space-y-1">
                <span class="text-xs font-medium">User name</span>
                <input class="focus-ring h-8 w-full rounded-md border bg-card px-2 font-mono text-xs" bind:value={newName} spellcheck="false" autocapitalize="off" placeholder="alex"
                  aria-invalid={Boolean(newName && nameProblem)} />
                {#if newName && nameProblem}<span class="block text-xs text-destructive">{nameProblem}</span>{/if}
              </label>
            </div>
            <label class="flex items-center gap-2 text-xs"><Switch bind:checked={newAdmin} aria-label="Administrator" /> Administrator</label>
            <p class="text-xs text-muted-foreground">
              {#if mutable}You set the password after applying, so it is never written into your configuration.
              {:else}This configuration manages passwords itself, so add a password for the new user in its file before applying.{/if}
            </p>
            <div class="flex justify-end gap-2">
              <Button variant="ghost" size="sm" type="button" onclick={() => (adding = false)}>Cancel</Button>
              <Button size="sm" type="submit" disabled={Boolean(nameProblem)}>Add</Button>
            </div>
          </form>
        {/if}
        {#if [...Object.keys(edits)].some((option) => option.startsWith("users.users."))}
          <p class="px-4 pb-3 text-xs text-muted-foreground">Removing a user keeps their files in /home.</p>
        {/if}
      </section>
    {/if}

    {#each sections.slice(1) as item (item.id)}{@render section(item)}{/each}

    {#if !sections.length && !inventory.users}
      <p class="text-muted-foreground">{app.host}'s configuration has none of these settings.</p>
    {/if}
  {/if}

  {#if changed.length}
    <div class="panel sticky bottom-4 z-10 space-y-3 p-4 shadow-lg">
      <div class="flex flex-wrap items-center gap-3">
        <p class="flex-1 font-medium">{plural(changed.length, "change")} not added yet</p>
        <Button variant="ghost" size="sm" onclick={() => { edits = {}; target = ""; }}>Reset</Button>
        <Button size="sm" disabled={!ready || software.saving} onclick={save}>Add to pending edits</Button>
      </div>
      {#if homeless.length}
        <label class="block space-y-1">
          <span class="text-xs font-medium">Write {[...new Set(homeless.map(name))].join(", ")} to</span>
          <select class="focus-ring h-8 w-full rounded-md border bg-card px-2 text-xs" bind:value={target}>
            <optgroup label="Used by {app.host}">
              {#each modules.filter((module) => module.active !== false) as module (module.file)}<option value={module.file}>{module.file}</option>{/each}
            </optgroup>
            <option value={NEW_FILE}>New file…</option>
          </select>
          {#if creating}<NewModuleFields scope={SCOPE} bind:file={newFile} bind:importer />{/if}
        </label>
      {/if}
    </div>
  {/if}
</div>
