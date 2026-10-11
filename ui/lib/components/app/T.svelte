<!--
  Translated text with markup in it. Each `{name}` in `text` is filled from
  `values`, or else by the snippet, which is given the placeholder's name:

  <T text={"Run {command} in a terminal."}>{#snippet children()}<code>nix-manager ui</code>{/snippet}</T>
  <T text={"{app} needs {file}."}>{#snippet children(name)}<b>{name === "app" ? app : file}</b>{/snippet}</T>
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import { parts } from "../../../../src/i18n";

  let { text, values = {}, children }: { text: string; values?: Record<string, string | number>; children?: Snippet<[string]> } = $props();
</script>

{#each parts(text) as part, index (index)}{#if "text" in part}{part.text}{:else if part.name in values}{values[part.name]}{:else}{@render children?.(part.name)}{/if}{/each}
