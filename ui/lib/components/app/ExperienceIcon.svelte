<script lang="ts">
  import BluetoothIcon from "@lucide/svelte/icons/bluetooth";
  import BotIcon from "@lucide/svelte/icons/bot";
  import CastIcon from "@lucide/svelte/icons/cast";
  import CodeIcon from "@lucide/svelte/icons/code";
  import ContainerIcon from "@lucide/svelte/icons/container";
  import FileCodeIcon from "@lucide/svelte/icons/file-code";
  import FolderCogIcon from "@lucide/svelte/icons/folder-cog";
  import FolderSearchIcon from "@lucide/svelte/icons/folder-search";
  import FolderSyncIcon from "@lucide/svelte/icons/folder-sync";
  import Gamepad2Icon from "@lucide/svelte/icons/gamepad-2";
  import GitBranchIcon from "@lucide/svelte/icons/git-branch";
  import GitPullRequestIcon from "@lucide/svelte/icons/git-pull-request";
  import GlobeIcon from "@lucide/svelte/icons/globe";
  import HardDriveDownloadIcon from "@lucide/svelte/icons/hard-drive-download";
  import MemoryStickIcon from "@lucide/svelte/icons/memory-stick";
  import MonitorCogIcon from "@lucide/svelte/icons/monitor-cog";
  import MonitorPlayIcon from "@lucide/svelte/icons/monitor-play";
  import NetworkIcon from "@lucide/svelte/icons/network";
  import PackageIcon from "@lucide/svelte/icons/package";
  import PanelsTopLeftIcon from "@lucide/svelte/icons/panels-top-left";
  import PrinterIcon from "@lucide/svelte/icons/printer";
  import RocketIcon from "@lucide/svelte/icons/rocket";
  import SearchIcon from "@lucide/svelte/icons/search";
  import SparklesIcon from "@lucide/svelte/icons/sparkles";
  import TerminalIcon from "@lucide/svelte/icons/terminal";

  let { name, class: className }: { name: string; class?: string } = $props();
  // Curated experiences name their icon; unknown names fall back to a package.
  const icons = {
    "bluetooth": BluetoothIcon,
    "bot": BotIcon,
    "cast": CastIcon,
    "code": CodeIcon,
    "container": ContainerIcon,
    "file-code": FileCodeIcon,
    "folder-cog": FolderCogIcon,
    "folder-search": FolderSearchIcon,
    "folder-sync": FolderSyncIcon,
    "gamepad-2": Gamepad2Icon,
    "git-branch": GitBranchIcon,
    "git-pull-request": GitPullRequestIcon,
    "globe": GlobeIcon,
    "hard-drive-download": HardDriveDownloadIcon,
    "memory-stick": MemoryStickIcon,
    "monitor-cog": MonitorCogIcon,
    "monitor-play": MonitorPlayIcon,
    "network": NetworkIcon,
    "package": PackageIcon,
    "panels-top-left": PanelsTopLeftIcon,
    "printer": PrinterIcon,
    "rocket": RocketIcon,
    "search": SearchIcon,
    "sparkles": SparklesIcon,
    "terminal": TerminalIcon,
  } as const;
  const Icon = $derived(icons[name as keyof typeof icons] ?? PackageIcon);
</script>

<Icon class={className} />
