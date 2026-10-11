import type { Hosts, Plan, Request } from "./domain";
import type { Change, DraftFile, RenderedDraft } from "./draft";
import type { UpdateReport } from "./updates";
import type { Explanation } from "./explain";
import type { Hardware } from "./hardware";
import type { Preferences } from "./preferences";

export type OperationStatus =
  | "running"
  | "succeeded"
  | "failed"
  | "cancelled"
  | "interrupted";

export type StepStatus =
  | "pending"
  | "running"
  | "succeeded"
  | "failed"
  | "cancelled"
  | "skipped";

/** Aggregated progress of a Nix build, derived from `--log-format internal-json`. */
export interface BuildProgress {
  /** No build or download has been scheduled yet; Nix is still evaluating. */
  evaluating: boolean;
  builds: { done: number; expected: number; running: number; failed: number };
  /** Store paths substituted from binary caches. */
  downloads: { done: number; expected: number; running: number };
  bytes: { done: number; expected: number };
  /** Names of derivations currently building or downloading. */
  current: string[];
}

export interface StepOutcome {
  id: string;
  label: string;
  status: StepStatus;
  startedAt?: string;
  finishedAt?: string;
  progress?: BuildProgress;
}

export interface Operation {
  id: string;
  pid: number;
  flake: string;
  request: Request;
  status: OperationStatus;
  startedAt: string;
  finishedAt?: string;
  step: string;
  steps?: StepOutcome[];
  error?: string;
  /** The error in plain words, when it is a known one. */
  explanation?: Explanation;
  finishingPrivilegedStep?: boolean;
  /** What an applied change replaced, so that it can be undone. */
  undo?: UndoPoint;
  /** Edits prepared to apply in one step that wait for review instead, and why. */
  held?: string;
  /** After a failed apply: whether going back to the earlier generations and files worked. */
  rollback?: { done: boolean; error?: string };
}

/** The state an applied change replaced. */
export interface UndoPoint {
  /** What the change did, in words. */
  changes: string[];
  /** Profile generations that were active before. */
  generations: { home?: number; system?: number };
  /**
   * Files the change wrote, with a hash of what it wrote (null when it deleted
   * the file). Their earlier content is kept beside the operation, outside
   * snapshots.
   */
  files: { path: string; created: boolean; written: string | null }[];
}

/** Earlier content of the files an undo point lists, by path; created files have none. */
export type UndoContents = Record<string, string>;

export interface Generation {
  number: number;
  createdAt: string;
  output: string;
  current: boolean;
  booted?: boolean;
  /** NixOS version label for system generations. */
  version?: string;
  kernel?: string;
}

export interface Repository {
  fingerprint: string;
  clean: boolean;
  changes: string[];
  branch?: string;
  revision?: string;
  subject?: string;
  committedAt?: string;
  /** The remote branch the checkout's branch pushes to, such as origin/main. */
  upstream?: string;
  /** flake.lock differs from the committed version. */
  lockModified: boolean;
  lockHash: string;
}

export interface SystemState {
  observedAt: string;
  repository: Repository;
  generations: { home: Generation[]; system: Generation[] };
  current: { home?: string; system?: string; booted?: string };
  nixosVersion?: string;
  kernel: { current?: string; booted?: string };
  /** The running kernel, initrd or modules differ from the active system. */
  rebootRequired: boolean;
  disk?: { path: string; free: number; total: number };
}

export interface PackageChange {
  name: string;
  before: string[];
  after: string[];
  /** Closure size change in bytes. */
  sizeDelta: number;
  kind: "added" | "removed" | "changed" | "rebuilt";
}

export interface LockRef {
  rev?: string;
  narHash?: string;
  lastModified?: string;
  source?: string;
  url?: string;
}

export interface LockChange {
  name: string;
  before?: LockRef;
  after?: LockRef;
}

export interface ReviewOutput {
  path: string;
  closureSize?: number;
  currentClosureSize?: number;
  changes: PackageChange[];
  /** The active configuration could not be compared (for example, none exists). */
  compared: boolean;
  version?: { before?: string; after?: string };
  kernel?: { before?: string; after?: string };
}

export interface EditedPackage {
  out: string;
  name: string;
  version?: string | null;
  description?: string | null;
  file: string;
}

/** What the edits themselves did to one configuration, by evaluation. */
export interface EditEffect {
  scope: "home" | "system";
  added: EditedPackage[];
  removed: EditedPackage[];
  options: { option: string; before?: unknown; after?: unknown }[];
}

/** Source edits a review was built with, written to the checkout on apply. */
export interface ReviewEdits {
  changes: Change[];
  /** Hash of the change list, compared with the current draft. */
  hash: string;
  files: DraftFile[];
  effects: EditEffect[];
}

export interface BuildReview {
  id: string;
  host: string;
  scope: Request["scope"];
  createdAt: string;
  /** Checkout state the outputs were evaluated from. */
  fingerprint: string;
  source: "checkout" | "inputs" | "generation";
  edits?: ReviewEdits;
  generation?: number;
  inputs?: {
    names: string[];
    changes: LockChange[];
    /** Hash of the checkout's flake.lock the proposal was resolved from. */
    baseLockHash: string;
  };
  home?: ReviewOutput;
  system?: ReviewOutput;
  operationId?: string;
  /** Bytes Nix downloaded while preparing the review. */
  downloaded?: number;
  /** For an undo: the applied change it reverses, from that change's operation. */
  undo?: { operationId: string; appliedAt: string; point: UndoPoint };
  /** The system part was applied on next boot; the home part follows once the computer runs that system. */
  afterBoot?: { system: string };
}

export interface Snapshot {
  config: {
    flake: string;
    hosts: Hosts;
    hostKey: string;
    homeProfile: string;
    /** The user running Nix Manager. */
    user: string;
    /** Hardware of the computer Nix Manager runs on. */
    hardware: Hardware;
  };
  active?: Operation;
  operations: Operation[];
  updates?: UpdateReport;
  system?: SystemState;
  reviews: BuildReview[];
  draft: RenderedDraft;
  catalog: CatalogStatus;
  preferences: Preferences;
}

export interface CatalogStatus {
  state: "missing" | "building" | "ready" | "failed";
  count?: number;
  builtAt?: string;
  error?: string;
}

export interface LogResult {
  text: string;
  /** Byte offset of the end of the returned text in the stored log. */
  end: number;
  truncated: boolean;
}

export type ManagerEvent =
  | {
      type: "operation";
      sequence: number;
      timestamp: string;
      operation: Operation;
    }
  | {
      type: "output";
      sequence: number;
      timestamp: string;
      operationId: string;
      stream: "stdout" | "stderr";
      text: string;
      /** Byte offset of this text in the stored log. */
      offset: number;
    }
  | {
      type: "progress";
      sequence: number;
      timestamp: string;
      operationId: string;
      stepId: string;
      progress: BuildProgress;
    };

export type SessionMessage =
  | ManagerEvent
  | { type: "snapshot"; snapshot: Snapshot };

export type { Plan };
