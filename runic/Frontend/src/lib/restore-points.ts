// Restore points and the configuration's history as the Bun version observes them (src/contracts.ts,
// src/history.ts). The C# backend does not observe them yet (W300-033); the components that show them are
// ready for that state.

export interface Generation {
  readonly number: number;
  readonly createdAt: string;
  readonly current: boolean;
  readonly booted?: boolean;
  /** NixOS version label for system generations. */
  readonly version?: string;
  readonly kernel?: string;
}

export interface HistoryEntry {
  readonly revision: string;
  readonly subject: string;
  /** The rest of the message, such as every part of a change. */
  readonly body: string;
  readonly date: string;
  readonly author: string;
  /** Made by Nix Manager after applying a change. */
  readonly applied: boolean;
}
