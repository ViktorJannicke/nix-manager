<script lang="ts">
  import ConstructionIcon from "@lucide/svelte/icons/construction";
  import EmptyState from "$lib/components/app/EmptyState.svelte";
  import { m } from "$lib/i18n";

  // Software and Computer follow once their backend is ported (W300-032, second part).
  let { title }: { title: string } = $props();
</script>

<div class="space-y-6">
  <header>
    <h1 class="title">{title}</h1>
  </header>
  <section class="panel">
    <EmptyState icon={ConstructionIcon} title={m.placeholder_page_title()}>{m.placeholder_page()}</EmptyState>
  </section>
</div>
