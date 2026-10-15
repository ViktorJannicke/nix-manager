<script lang="ts">
  import MinusIcon from "@lucide/svelte/icons/minus";
  import PlusIcon from "@lucide/svelte/icons/plus";
  import { Button } from "$lib/components/ui/button";
  import { m } from "$lib/i18n";

  let { value = $bindable(), min = 1, max = 50 }: { value: number; min?: number; max?: number } = $props();
</script>

<!-- The buttons describe themselves by the value, and the value is a polite live region, so pressing "Keep more"
     announces the new number. -->
<div class="flex items-center gap-2" role="group" aria-labelledby="retention-label retention-newest">
  <span id="retention-label" class="text-xs text-muted-foreground">{m.restore_keep()}</span>
  <div class="flex items-center rounded-lg border">
    <Button variant="ghost" size="icon-sm" aria-label={m.restore_keep_fewer()} aria-describedby="retention-value" disabled={value <= min} onclick={() => value--}><MinusIcon /></Button>
    <output id="retention-value" aria-live="polite" aria-atomic="true" class="w-8 text-center font-medium tabular-nums">{value}</output>
    <Button variant="ghost" size="icon-sm" aria-label={m.restore_keep_more()} aria-describedby="retention-value" disabled={value >= max} onclick={() => value++}><PlusIcon /></Button>
  </div>
  <span id="retention-newest" class="text-xs text-muted-foreground">{m.restore_newest()}</span>
</div>
