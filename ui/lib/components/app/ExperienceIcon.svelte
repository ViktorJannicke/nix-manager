<script lang="ts">
  import ActivityIcon from "@lucide/svelte/icons/activity";
  import AudioLinesIcon from "@lucide/svelte/icons/audio-lines";
  import BluetoothIcon from "@lucide/svelte/icons/bluetooth";
  import BookSearchIcon from "@lucide/svelte/icons/book-search";
  import BotIcon from "@lucide/svelte/icons/bot";
  import BoxesIcon from "@lucide/svelte/icons/boxes";
  import CastIcon from "@lucide/svelte/icons/cast";
  import ClapperboardIcon from "@lucide/svelte/icons/clapperboard";
  import CodeIcon from "@lucide/svelte/icons/code";
  import ContainerIcon from "@lucide/svelte/icons/container";
  import DownloadIcon from "@lucide/svelte/icons/download";
  import FingerprintIcon from "@lucide/svelte/icons/fingerprint";
  import FileCodeIcon from "@lucide/svelte/icons/file-code";
  import FileTextIcon from "@lucide/svelte/icons/file-text";
  import FolderCogIcon from "@lucide/svelte/icons/folder-cog";
  import FolderOpenIcon from "@lucide/svelte/icons/folder-open";
  import FolderSearchIcon from "@lucide/svelte/icons/folder-search";
  import FolderSyncIcon from "@lucide/svelte/icons/folder-sync";
  import Gamepad2Icon from "@lucide/svelte/icons/gamepad-2";
  import GaugeIcon from "@lucide/svelte/icons/gauge";
  import GitBranchIcon from "@lucide/svelte/icons/git-branch";
  import GitMergeIcon from "@lucide/svelte/icons/git-merge";
  import GitPullRequestIcon from "@lucide/svelte/icons/git-pull-request";
  import GlobeIcon from "@lucide/svelte/icons/globe";
  import HardDriveDownloadIcon from "@lucide/svelte/icons/hard-drive-download";
  import HistoryIcon from "@lucide/svelte/icons/history";
  import ListTreeIcon from "@lucide/svelte/icons/list-tree";
  import LockOpenIcon from "@lucide/svelte/icons/lock-open";
  import MemoryStickIcon from "@lucide/svelte/icons/memory-stick";
  import MonitorCogIcon from "@lucide/svelte/icons/monitor-cog";
  import MonitorPlayIcon from "@lucide/svelte/icons/monitor-play";
  import NetworkIcon from "@lucide/svelte/icons/network";
  import PackageIcon from "@lucide/svelte/icons/package";
  import PanelsTopLeftIcon from "@lucide/svelte/icons/panels-top-left";
  import PenLineIcon from "@lucide/svelte/icons/pen-line";
  import PrinterIcon from "@lucide/svelte/icons/printer";
  import RadarIcon from "@lucide/svelte/icons/radar";
  import RecycleIcon from "@lucide/svelte/icons/recycle";
  import RocketIcon from "@lucide/svelte/icons/rocket";
  import SearchIcon from "@lucide/svelte/icons/search";
  import ServerIcon from "@lucide/svelte/icons/server";
  import Share2Icon from "@lucide/svelte/icons/share-2";
  import ShieldAlertIcon from "@lucide/svelte/icons/shield-alert";
  import SparklesIcon from "@lucide/svelte/icons/sparkles";
  import SquareTerminalIcon from "@lucide/svelte/icons/square-terminal";
  import StoreIcon from "@lucide/svelte/icons/store";
  import TerminalIcon from "@lucide/svelte/icons/terminal";
  import VideoIcon from "@lucide/svelte/icons/video";
  import ZapIcon from "@lucide/svelte/icons/zap";

  let { name, class: className }: { name: string; class?: string } = $props();
  // Curated experiences name their icon; unknown names fall back to a package.
  const icons = {
    "activity": ActivityIcon,
    "audio-lines": AudioLinesIcon,
    "bluetooth": BluetoothIcon,
    "book-search": BookSearchIcon,
    "bot": BotIcon,
    "boxes": BoxesIcon,
    "cast": CastIcon,
    "clapperboard": ClapperboardIcon,
    "code": CodeIcon,
    "container": ContainerIcon,
    "download": DownloadIcon,
    "file-code": FileCodeIcon,
    "file-text": FileTextIcon,
    "fingerprint": FingerprintIcon,
    "folder-cog": FolderCogIcon,
    "folder-open": FolderOpenIcon,
    "folder-search": FolderSearchIcon,
    "folder-sync": FolderSyncIcon,
    "gamepad-2": Gamepad2Icon,
    "gauge": GaugeIcon,
    "git-branch": GitBranchIcon,
    "git-merge": GitMergeIcon,
    "git-pull-request": GitPullRequestIcon,
    "globe": GlobeIcon,
    "hard-drive-download": HardDriveDownloadIcon,
    "history": HistoryIcon,
    "list-tree": ListTreeIcon,
    "lock-open": LockOpenIcon,
    "memory-stick": MemoryStickIcon,
    "monitor-cog": MonitorCogIcon,
    "monitor-play": MonitorPlayIcon,
    "network": NetworkIcon,
    "package": PackageIcon,
    "panels-top-left": PanelsTopLeftIcon,
    "pen-line": PenLineIcon,
    "printer": PrinterIcon,
    "radar": RadarIcon,
    "recycle": RecycleIcon,
    "rocket": RocketIcon,
    "search": SearchIcon,
    "server": ServerIcon,
    "share-2": Share2Icon,
    "shield-alert": ShieldAlertIcon,
    "sparkles": SparklesIcon,
    "square-terminal": SquareTerminalIcon,
    "store": StoreIcon,
    "terminal": TerminalIcon,
    "video": VideoIcon,
    "zap": ZapIcon,
  } as const;
  const Icon = $derived(icons[name as keyof typeof icons] ?? PackageIcon);
</script>

<Icon class={className} />
