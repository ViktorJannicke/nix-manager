<!--
  A translated message with markup in it. Call the message with `slot(name)` for each value that is markup; the
  snippet renders it, given the slot's name:

  <Markup text={m.settings_detected_as({ host: slot("host") })}>{#snippet children()}<b>{host}</b>{/snippet}</Markup>
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import { pieces } from "$lib/i18n";

  let { text, children }: { text: string; children: Snippet<[string]> } = $props();
</script>

{#each pieces(text) as piece, index (index)}{#if "text" in piece}{piece.text}{:else}{@render children(piece.slot)}{/if}{/each}
