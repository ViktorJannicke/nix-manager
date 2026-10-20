<script lang="ts">
  import TerminalIcon from "@lucide/svelte/icons/square-terminal";
  import ChevronRightIcon from "@lucide/svelte/icons/chevron-right";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { formatCommand } from "$lib/requests";
  import { app } from "$lib/app.svelte";
  import { m, text } from "$lib/i18n";

  let details = $state(false);
  const confirmation = $derived(app.confirmation);
  // The dry run: what the request would run, planned by the backend without running it.
  const preview = $derived(app.preview);
  const error = $derived(confirmation?.failed ?? (text(preview?.error) || undefined));
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
        <!-- The points belong to the description, so screen readers announce
             what confirming does when the dialog opens. -->
        <Dialog.Description>
          {#snippet child({ props })}
            <div {...props}>
              <p>{confirmation.description}</p>
              {#if confirmation.points?.length}
                <ul role="list" class="mt-4 space-y-1.5 text-foreground">
                  {#each confirmation.points as point, index (index)}
                    <li class="flex gap-2"><span class="mt-1.5 size-1.5 shrink-0 rounded-full bg-primary"></span>{point}</li>
                  {/each}
                </ul>
              {/if}
            </div>
          {/snippet}
        </Dialog.Description>
      </Dialog.Header>
      {#if error}
        <p class="rounded-lg bg-destructive/10 px-3 py-2 text-destructive" role="alert">{error}</p>
      {/if}
      {#if preview?.plan?.notes.length}
        <ul role="list" class="space-y-1 rounded-lg bg-warning/10 px-3 py-2 text-xs">
          {#each preview.plan.notes as note, index (index)}<li>{text(note)}</li>{/each}
        </ul>
      {/if}
      <div>
        <button
          class="focus-ring flex items-center gap-1 rounded text-xs text-muted-foreground hover:text-foreground"
          aria-expanded={details}
          onclick={() => (details = !details)}
        >
          <ChevronRightIcon class="size-3.5 transition-transform {details ? 'rotate-90' : ''}" />
          <TerminalIcon class="size-3.5" /> {m.confirm_commands_that_will_run()}
        </button>
        {#if details}
          <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
          <ol role="list" tabindex="0" aria-label={m.confirm_commands()} class="focus-ring scroll-thin mt-2 max-h-56 space-y-2 overflow-auto rounded-lg bg-muted p-3 text-xs">
            {#if preview?.plan}
              {#each preview.plan.steps as command, index (index)}
                <li>
                  <p class="font-medium">{index + 1}. {text(command.label)}</p>
                  <code class="block break-all text-muted-foreground">{formatCommand(command, m.confirm_performed_by_nix_manager())}</code>
                </li>
              {/each}
            {:else if !error}<li class="text-muted-foreground">{m.common_loading()}</li>{/if}
          </ol>
        {/if}
      </div>
      <Dialog.Footer>
        <Button variant="outline" disabled={confirmation.running} onclick={() => (app.confirmation = undefined)}>{m.common_cancel()}</Button>
        <Button
          variant={confirmation.destructive ? "destructive" : "default"}
          disabled={confirmation.running || Boolean(error) || !preview?.plan || app.busy}
          onclick={() => app.accept()}
        >
          {confirmation.running ? m.confirm_starting() : confirmation.confirmLabel}
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
