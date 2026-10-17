<script lang="ts">
  import CheckIcon from "@lucide/svelte/icons/check";
  import XIcon from "@lucide/svelte/icons/x";
  import LoaderIcon from "@lucide/svelte/icons/loader-circle";
  import BanIcon from "@lucide/svelte/icons/ban";
  import CircleDashedIcon from "@lucide/svelte/icons/circle-dashed";
  import { cn } from "$lib/utils";
  import { m } from "$lib/i18n";

  let { status, class: className }: { status: string; class?: string } = $props();
  const labels: Record<string, () => string> = {
    running: m.status_running,
    succeeded: m.status_done,
    failed: m.status_failed,
    cancelled: m.status_cancelled,
    interrupted: m.status_interrupted,
    pending: m.status_waiting,
    skipped: m.status_skipped,
  };
  const tones: Record<string, string> = {
    running: "bg-primary/10 text-primary",
    succeeded: "bg-success/12 text-success",
    failed: "bg-destructive/12 text-destructive",
    interrupted: "bg-warning/15 text-warning",
  };
</script>

<span
  class={cn(
    "inline-flex h-5 shrink-0 items-center gap-1 rounded-full px-2 text-xs font-medium",
    tones[status] ?? "bg-muted text-muted-foreground",
    className,
  )}
>
  {#if status === "running"}<LoaderIcon class="size-3 animate-spin" />
  {:else if status === "succeeded"}<CheckIcon class="size-3" />
  {:else if status === "failed"}<XIcon class="size-3" />
  {:else if status === "cancelled" || status === "interrupted"}<BanIcon class="size-3" />
  {:else}<CircleDashedIcon class="size-3" />{/if}
  {labels[status]?.() ?? status}
</span>
