<script lang="ts">
  import { dateTime, relative } from "$lib/format";
  import { m } from "$lib/i18n";
  import type { HistoryEntry } from "$lib/restore-points";

  let { entries, labelledby, now }: {
    entries: readonly HistoryEntry[];
    /** The ID of the heading that names the list ("History"). */
    labelledby: string;
    now: number;
  } = $props();
</script>

<!-- Scrolls on its own, so it takes focus; its name is its heading's, "History". -->
<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
<ul role="list" tabindex="0" aria-labelledby={labelledby} class="focus-ring max-h-96 divide-y overflow-y-auto">
  {#each entries as entry (entry.revision)}
    <li class="flex items-start gap-4 px-4 py-3">
      <div class="min-w-0 flex-1">
        <p class="font-medium">{entry.subject}</p>
        {#if entry.applied && entry.body}
          <p class="mt-0.5 text-xs whitespace-pre-line text-muted-foreground">{entry.body}</p>
        {/if}
      </div>
      <div class="shrink-0 text-right text-xs text-muted-foreground">
        <p title={dateTime(entry.date)}>{relative(entry.date, now)}</p>
        <p>{entry.applied ? m.restore_applied_here() : entry.author}</p>
      </div>
    </li>
  {/each}
</ul>
