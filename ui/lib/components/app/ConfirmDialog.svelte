<script lang="ts">
  import TerminalIcon from "@lucide/svelte/icons/square-terminal";
  import ChevronRightIcon from "@lucide/svelte/icons/chevron-right";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { formatCommand } from "../../../../src/domain";
  import { app } from "$lib/app.svelte";

  let details = $state(false);
  const confirmation = $derived(app.confirmation);
</script>

<Dialog.Root
  open={Boolean(confirmation)}
  onOpenChange={(open) => {
    if (!open && !confirmation?.running) app.confirmation = undefined;
  }}
>
  <Dialog.Content class="sm:max-w-lg">
    {#if confirmation}
      <Dialog.Header>
        <Dialog.Title>{confirmation.title}</Dialog.Title>
        <Dialog.Description>{confirmation.description}</Dialog.Description>
      </Dialog.Header>
      {#if confirmation.points?.length}
        <ul class="space-y-1.5">
          {#each confirmation.points as point}
            <li class="flex gap-2"><span class="mt-1.5 size-1.5 shrink-0 rounded-full bg-primary"></span>{point}</li>
          {/each}
        </ul>
      {/if}
      {#if confirmation.error}
        <p class="rounded-lg bg-destructive/10 px-3 py-2 text-destructive" role="alert">{confirmation.error}</p>
      {/if}
      {#if confirmation.plan?.notes.length}
        <ul class="space-y-1 rounded-lg bg-warning/10 px-3 py-2 text-xs">
          {#each confirmation.plan.notes as note}<li>{note}</li>{/each}
        </ul>
      {/if}
      <div>
        <button
          class="focus-ring flex items-center gap-1 rounded text-xs text-muted-foreground hover:text-foreground"
          aria-expanded={details}
          onclick={() => (details = !details)}
        >
          <ChevronRightIcon class="size-3.5 transition-transform {details ? 'rotate-90' : ''}" />
          <TerminalIcon class="size-3.5" /> Commands that will run
        </button>
        {#if details}
          <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
          <ol tabindex="0" aria-label="Commands" class="focus-ring scroll-thin mt-2 max-h-56 space-y-2 overflow-auto rounded-lg bg-muted p-3 text-xs">
            {#if confirmation.plan}
              {#each confirmation.plan.steps as command, index}
                <li>
                  <p class="font-medium">{index + 1}. {command.label}</p>
                  <code class="block break-all text-muted-foreground">{formatCommand(command)}</code>
                </li>
              {/each}
            {:else if !confirmation.error}<li class="text-muted-foreground">Loading…</li>{/if}
          </ol>
        {/if}
      </div>
      <Dialog.Footer>
        <Button variant="outline" disabled={confirmation.running} onclick={() => (app.confirmation = undefined)}>Cancel</Button>
        <Button
          variant={confirmation.destructive ? "destructive" : "default"}
          disabled={confirmation.running || Boolean(confirmation.error) || !confirmation.plan || app.busy}
          onclick={() => app.accept()}
        >
          {confirmation.running ? "Starting…" : confirmation.confirmLabel}
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
