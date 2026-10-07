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

export interface StepOutcome {
  id: string;
  label: string;
  status: StepStatus;
  startedAt?: string;
  finishedAt?: string;
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

export interface ManagerState {
  config: {
    flake: string;
    hosts: Hosts;
    hostKey: string;
    homeProfile: string;
  };
  active?: Operation;
  operations: Operation[];
  updates?: UpdateReport;
  advanced: AdvancedState;
}

export interface Generation {
  number: number;
  createdAt: string;
  output: string;
  active: boolean;
}

export interface BuildReview {
  id: string;
  host: string;
  scope: Request["scope"];
  fingerprint: string;
  createdAt: string;
  homeOutput?: string;
  systemOutput?: string;
  homeClosureSize?: number;
  systemClosureSize?: number;
  homeDiff?: string;
  systemDiff?: string;
  appliedAt?: string;
}

export interface AdvancedState {
  repository: {
    fingerprint: string;
    clean: boolean;
    changes: string[];
    revision?: string;
    observedAt: string;
  };
  generations: { home: Generation[]; system: Generation[] };
  current: { home?: string; system?: string };
  reviews: BuildReview[];
  inputProposals: InputProposal[];
}

export interface InputProposal {
  id: string;
  fingerprint: string;
  createdAt: string;
  inputs: string[];
  changes: Array<{ name: string; before: string; after: string }>;
}

export interface LogResult {
  text: string;
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
    };

export type { Plan };
