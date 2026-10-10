import type { Hosts, Plan, Request } from "./domain";
import type { UpdateReport } from "./updates";

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
  finishingPrivilegedStep?: boolean;
}

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

export interface BuildReview {
  id: string;
  host: string;
  scope: Request["scope"];
  createdAt: string;
  /** Checkout state the outputs were evaluated from. */
  fingerprint: string;
  source: "checkout" | "inputs" | "generation";
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
}

export interface Snapshot {
  config: {
    flake: string;
    hosts: Hosts;
    hostKey: string;
    homeProfile: string;
  };
  active?: Operation;
  operations: Operation[];
  updates?: UpdateReport;
  system?: SystemState;
  reviews: BuildReview[];
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
