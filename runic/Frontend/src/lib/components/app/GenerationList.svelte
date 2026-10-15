<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { dateTime, relative } from "$lib/format";
  import { m } from "$lib/i18n";
  import type { Generation } from "$lib/restore-points";

  let { generations, profile, label, now, disabled, onGoBack }: {
    generations: readonly Generation[];
    /** Part of each row's ID, so the lists of both profiles can be on one page. */
    profile: "home" | "system";
    /** The list's accessible name, such as "Restore points of your user". */
    label: string;
    now: number;
    disabled: boolean;
    onGoBack: (generation: Generation) => void;
  } = $props();

  const sorted = $derived([...generations].sort((a, b) => b.number - a.number));
  const id = (generation: Generation) => `generation-${profile}-${generation.number}`;
</script>

<!-- Tailwind removes the list style, which makes WebKitGTK drop the list role (G-19): name it as a list. -->
<ul role="list" aria-label={label} class="divide-y">
  {#each sorted as generation (generation.number)}
    <li class="flex items-center gap-4 px-4 py-3 {generation.current ? 'bg-primary/5' : ''}">
      <span id="{id(generation)}-number" class="w-12 font-mono text-muted-foreground tabular-nums">#{generation.number}</span>
      <div class="min-w-0 flex-1">
        <p id="{id(generation)}-date" class="font-medium">
          {dateTime(generation.createdAt)}
          <span class="ml-1 font-normal text-muted-foreground">{relative(generation.createdAt, now)}</span>
        </p>
        {#if generation.version || generation.kernel}
          <p class="text-xs text-muted-foreground">
            {generation.version ? `NixOS ${generation.version}` : ""}{generation.version && generation.kernel ? ", " : ""}{generation.kernel ? `Linux ${generation.kernel}` : ""}
          </p>
        {/if}
      </div>
      {#if generation.current}<span class="rounded-full bg-primary/10 px-2 py-0.5 text-xs font-medium text-primary">{m.restore_in_use()}</span>{/if}
      {#if generation.booted && !generation.current}<span class="rounded-full bg-muted px-2 py-0.5 text-xs font-medium" title={m.restore_running_until_restart_body()}>{m.restore_running_until_restart()}</span>{/if}
      {#if !generation.current}
        <!-- Every row has this button: the number and date of its row tell them apart. -->
        <Button
          variant="outline"
          size="sm"
          aria-describedby="{id(generation)}-number {id(generation)}-date"
          {disabled}
          onclick={() => onGoBack(generation)}
        >{m.restore_go_back()}</Button>
      {/if}
    </li>
  {/each}
</ul>
