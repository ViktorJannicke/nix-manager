<script lang="ts">
  import { Switch } from "$lib/components/ui/switch";
  import { m, say, type Phrase } from "$lib/i18n";
  import { field, localeName, parsePorts, timeZones } from "$lib/fields";
  import { showValue } from "$lib/changes";
  import { formatLocale, formatSettings, keyboardLayouts, locales, type ComputerInput } from "$lib/computer";
  import type { OptionDoc } from "../../../generated/types.js";

  /** A form control for one option's value; `input` picks a friendlier control than its type gives. */
  let {
    label,
    doc,
    value,
    input,
    users = [],
    onchange,
  }: {
    label: string;
    doc?: OptionDoc | null;
    value: unknown;
    input?: ComputerInput;
    /** Choices for a user input. */
    users?: readonly string[];
    onchange: (value: unknown) => void;
  } = $props();

  const kind = $derived(field(doc?.t));
  const control = "focus-ring h-8 rounded-md border bg-card px-2 text-xs";
  /** Choices with the current value kept, so an unusual value is not replaced by showing the form. */
  const keep = (choices: readonly string[], current: unknown) =>
    typeof current === "string" && current && !choices.includes(current) ? [current, ...choices] : choices;

  const zones = $derived(input === "timezone" ? keep(["UTC", ...timeZones().filter((zone) => zone !== "UTC")], value) : []);
  const localeChoices = $derived(
    input === "locale" || input === "formats"
      ? keep(locales, input === "formats" ? formatLocale(value) : value)
          .map((locale) => ({ locale, name: localeName(locale) }))
          .sort((a, b) => a.name.localeCompare(b.name))
      : [],
  );
  const formats = $derived(input === "formats" ? formatLocale(value) : undefined);
  const layouts = $derived(
    input === "keyboard" && typeof value === "string" && value && !keyboardLayouts.some((layout) => layout.code === value)
      ? [{ code: value, label: null as Phrase | null }, ...keyboardLayouts]
      : keyboardLayouts,
  );
</script>

{#if !doc}
  <span class="text-xs text-muted-foreground">{m.overview_reading()}</span>
{:else if input === "timezone" && zones.length > 1}
  <select class="{control} w-56" aria-label={label} value={String(value ?? "")} onchange={(event) => onchange(event.currentTarget.value || null)}>
    {#if value == null}<option value="">{m.field_not_set()}</option>{/if}
    {#each zones as zone (zone)}<option value={zone}>{zone.replaceAll("_", " ")}</option>{/each}
  </select>
{:else if input === "locale"}
  <select class="{control} w-56" aria-label={label} value={String(value ?? "")} onchange={(event) => onchange(event.currentTarget.value)}>
    {#each localeChoices as choice (choice.locale)}<option value={choice.locale}>{choice.name}, {choice.locale}</option>{/each}
  </select>
{:else if input === "formats"}
  <select class="{control} w-56" aria-label={label} value={formats === undefined ? "\0custom" : (formats ?? "")}
    onchange={(event) => { const locale = event.currentTarget.value; if (locale !== "\0custom") onchange(locale ? formatSettings(locale) : {}); }}>
    <option value="">{m.field_same_as_language()}</option>
    {#if formats === undefined}<option value={"\0custom"}>{m.field_mixed()}</option>{/if}
    {#each localeChoices as choice (choice.locale)}<option value={choice.locale}>{choice.name}, {choice.locale}</option>{/each}
  </select>
{:else if input === "keyboard"}
  <select class="{control} w-56" aria-label={label} value={String(value ?? "")} onchange={(event) => onchange(event.currentTarget.value)}>
    {#each layouts as layout (layout.code)}<option value={layout.code}>{layout.label ? say(layout.label) : layout.code}</option>{/each}
  </select>
{:else if input === "user"}
  <select class="{control} w-56" aria-label={label} value={String(value ?? "")} onchange={(event) => onchange(event.currentTarget.value || null)}>
    {#if !value}<option value="">{m.field_choose_user()}</option>{/if}
    {#each keep(users, value) as user (user)}<option value={user}>{user}</option>{/each}
  </select>
{:else if input === "ports"}
  <input class="{control} w-56" aria-label={label} placeholder={m.field_ports_placeholder()} value={Array.isArray(value) ? value.join(", ") : ""}
    onchange={(event) => { const ports = parsePorts(event.currentTarget.value); if (ports) onchange(ports); else { event.currentTarget.setCustomValidity(m.field_ports_invalid()); event.currentTarget.reportValidity(); } }}
    oninput={(event) => event.currentTarget.setCustomValidity("")} />
{:else if kind.kind === "boolean"}
  <Switch checked={Boolean(value)} onCheckedChange={(checked) => onchange(checked)} aria-label={label} />
{:else if kind.kind === "choice"}
  <select class={control} aria-label={label} value={String(value ?? "")} onchange={(event) => onchange(event.currentTarget.value)}>
    {#each kind.choices as choice (choice)}<option value={choice}>{choice}</option>{/each}
  </select>
{:else if kind.kind === "number"}
  <input type="number" class="{control} w-28" aria-label={label} min={kind.min} max={kind.max} value={value ?? ""}
    onchange={(event) => { const number = event.currentTarget.valueAsNumber; if (Number.isInteger(number)) onchange(number); }} />
{:else if kind.kind === "text"}
  <input class="{control} w-56" aria-label={label} value={String(value ?? "")} placeholder={kind.nullable ? m.field_not_set() : ""}
    onchange={(event) => { const text = event.currentTarget.value; onchange(kind.nullable && !text ? null : text); }} />
{:else if kind.kind === "lines"}
  <textarea class="focus-ring h-20 w-56 rounded-md border bg-card px-2 py-1 font-mono text-xs" aria-label={label} placeholder={m.field_one_per_line()}
    value={((value as string[] | undefined) ?? []).join("\n")}
    onchange={(event) => onchange(event.currentTarget.value.split("\n").map((line) => line.trim()).filter(Boolean))}></textarea>
{:else}
  <span class="max-w-56 truncate text-xs text-muted-foreground" title={doc.t ?? ""}>{showValue(value)}</span>
{/if}
