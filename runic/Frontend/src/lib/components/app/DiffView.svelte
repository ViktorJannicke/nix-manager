<script lang="ts">
  import type { DraftFile } from "../../../generated/types.js";
  import { m } from "$lib/i18n";

  let { file }: { file: DraftFile } = $props();
  const tone: Record<string, string> = { "+": "bg-success/10 text-foreground", "-": "bg-destructive/10 text-muted-foreground", " ": "text-muted-foreground" };
</script>

<figure class="overflow-hidden rounded-lg border bg-card">
  <figcaption class="border-b bg-muted/50 px-3 py-1.5 font-mono text-[0.75rem] break-all">{file.path}{#if file.created}<span class="ml-2 font-sans text-success">{m.diff_new_file()}</span>{/if}</figcaption>
  <!-- Scrolls sideways on its own, so it takes focus; its name says whose changes it shows. -->
  <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
  <div tabindex="0" role="region" aria-label={m.diff_file_changes({ path: file.path })} class="focus-ring scroll-thin overflow-x-auto">
    {#each file.hunks as hunk, index (index)}
      {#if index > 0}<div class="border-y border-dashed bg-muted/30 px-3 text-[0.7rem] leading-5 text-muted-foreground" aria-hidden="true">⋯</div>{/if}
      <table class="w-full font-mono text-[0.75rem] leading-5">
        <tbody>
          {#each hunk.lines as line, offset (offset)}
            <tr class={tone[line.kind]}>
              <!-- The sign is read as a word, so "+" and "-" lines can be told apart without colour. -->
              <td class="w-6 pl-2 text-center select-none">{#if line.kind === "+"}<span aria-hidden="true">+</span><span class="sr-only">{m.diff_added()}</span>{:else if line.kind === "-"}<span aria-hidden="true">-</span><span class="sr-only">{m.diff_removed()}</span>{/if}</td>
              <td class="pr-3 whitespace-pre">{line.text}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/each}
  </div>
</figure>
