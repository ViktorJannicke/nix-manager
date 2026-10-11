<script lang="ts">
  import type { DraftFile } from "../../../../src/draft";
  import { t } from "$lib/i18n";

  let { file }: { file: DraftFile } = $props();
  const tone = { "+": "bg-success/10 text-foreground", "-": "bg-destructive/10 text-muted-foreground", " ": "text-muted-foreground" };
</script>

<figure class="overflow-hidden rounded-lg border bg-card">
  <figcaption class="border-b bg-muted/50 px-3 py-1.5 font-mono text-[0.75rem] break-all">{file.path}{#if file.created}<span class="ml-2 font-sans text-success">{t("new file")}</span>{/if}</figcaption>
  <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
  <div tabindex="0" role="region" aria-label={t("File changes")} class="focus-ring scroll-thin overflow-x-auto">
    {#each file.hunks as hunk, index (index)}
      {#if index > 0}<div class="border-y border-dashed bg-muted/30 px-3 text-[0.7rem] leading-5 text-muted-foreground">⋯</div>{/if}
      <table class="w-full font-mono text-[0.75rem] leading-5">
        <tbody>
          {#each hunk.lines as line, offset (offset)}
            <tr class={tone[line.kind]}>
              <td class="w-6 pl-2 text-center select-none" aria-label={line.kind === "+" ? t("added") : line.kind === "-" ? t("removed") : undefined}>{line.kind === " " ? "" : line.kind}</td>
              <td class="pr-3 whitespace-pre">{line.text}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/each}
  </div>
</figure>
