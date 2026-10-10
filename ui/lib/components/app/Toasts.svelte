<script lang="ts">
  import { fly } from "svelte/transition";
  import XIcon from "@lucide/svelte/icons/x";
  import CircleCheckIcon from "@lucide/svelte/icons/circle-check";
  import CircleAlertIcon from "@lucide/svelte/icons/circle-alert";
  import InfoIcon from "@lucide/svelte/icons/info";
  import { app } from "$lib/app.svelte";
</script>

<div class="pointer-events-none fixed right-4 bottom-4 z-50 flex w-88 flex-col gap-2" aria-live="polite">
  {#each app.toasts as toast (toast.id)}
    <div
      transition:fly={{ y: 12, duration: 150 }}
      class="pointer-events-auto flex gap-3 rounded-xl border bg-popover p-3 text-popover-foreground shadow-lg"
      role={toast.tone === "error" ? "alert" : "status"}
    >
      {#if toast.tone === "success"}<CircleCheckIcon class="mt-0.5 size-4 shrink-0 text-success" />
      {:else if toast.tone === "error"}<CircleAlertIcon class="mt-0.5 size-4 shrink-0 text-destructive" />
      {:else}<InfoIcon class="mt-0.5 size-4 shrink-0 text-primary" />{/if}
      <div class="min-w-0 flex-1">
        <p class="font-medium">{toast.title}</p>
        {#if toast.body}<p class="mt-0.5 line-clamp-4 text-xs break-words text-muted-foreground">{toast.body}</p>{/if}
        {#if toast.action}
          <button
            class="focus-ring mt-1.5 rounded text-xs font-medium text-primary hover:underline"
            onclick={() => { toast.action?.run(); app.dismiss(toast.id); }}
          >{toast.action.label}</button>
        {/if}
      </div>
      <button class="focus-ring size-5 rounded text-muted-foreground hover:text-foreground" aria-label="Dismiss" onclick={() => app.dismiss(toast.id)}>
        <XIcon class="size-4" />
      </button>
    </div>
  {/each}
</div>
