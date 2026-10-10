import { Context, Effect, Layer } from "effect";
import { ManagerError, type Command } from "./domain";
import type { BuildProgress } from "./contracts";
import { NixProgress } from "./nix";

export type Output =
  | { stream: "stdout" | "stderr"; text: string }
  | { stream: "progress"; progress: BuildProgress };
export interface Result {
  code: number;
  stdout: string;
  stderr: string;
}
export class Runner extends Context.Tag("nix-manager/Runner")<
  Runner,
  {
    run: (command: Command) => Effect.Effect<Result, ManagerError>;
  }
>() {}

// Each command has its own process group. Interruption waits for that group to
// terminate before releasing the operation lock or deleting temporary inputs.
export const runProcess = (
  command: Command,
  output: (event: Output) => void = () => {},
): Effect.Effect<Result, ManagerError> =>
  Effect.async<Result, ManagerError>((resume) => {
    let child: ReturnType<typeof Bun.spawn>;
    try {
      child = Bun.spawn([...command.argv], {
        cwd: command.cwd,
        env: { ...process.env, ...command.env },
        stdin: "ignore",
        stdout: "pipe",
        stderr: "pipe",
        detached: true,
      });
    } catch (error) {
      resume(
        Effect.fail(
          new ManagerError({
            message: `Cannot start ${command.argv[0]}: ${error}`,
          }),
        ),
      );
      return;
    }
    // Nix's structured log is converted to readable lines and throttled
    // progress snapshots; other output passes through unchanged.
    const nix = command.progress ? new NixProgress() : undefined;
    let lastProgress = 0;
    const read = async (
      stream: ReadableStream<Uint8Array>,
      channel: "stdout" | "stderr",
    ) => {
      let captured = "";
      let pending = "";
      const decoder = new TextDecoder();
      const accept = (text: string, final = false) => {
        if (!nix || channel !== "stderr") {
          captured = (captured + text).slice(-8 * 1024 * 1024);
          if (text && !command.quiet) output({ stream: channel, text });
          return;
        }
        pending += text;
        const lines = pending.split("\n");
        pending = lines.pop()!;
        if (final && pending) lines.push(pending);
        if (final) pending = "";
        let readable = "";
        for (const line of lines) {
          const converted = nix.line(line);
          if (converted !== undefined) readable += converted + "\n";
        }
        captured = (captured + readable).slice(-8 * 1024 * 1024);
        if (command.quiet) return;
        if (readable) output({ stream: channel, text: readable });
        const now = Date.now();
        if (final || now - lastProgress > 250) {
          lastProgress = now;
          output({ stream: "progress", progress: nix.snapshot() });
        }
      };
      for await (const chunk of stream)
        accept(decoder.decode(chunk, { stream: true }));
      accept(decoder.decode(), true);
      return captured;
    };
    const readers = [
      read(child.stdout as ReadableStream<Uint8Array>, "stdout"),
      read(child.stderr as ReadableStream<Uint8Array>, "stderr"),
    ];
    const completed = Promise.all([
      child.exited,
      readers[0],
      readers[1],
    ] as const);
    let stopping: Promise<void> | undefined;
    const stop = () =>
      (stopping ??= (async () => {
        const signal = (name: NodeJS.Signals) => {
          try {
            process.kill(-child.pid, name);
          } catch {
            /* Already exited. */
          }
        };
        signal("SIGTERM");
        const timer = setTimeout(() => signal("SIGKILL"), 3000);
        try {
          await child.exited;
          // The group leader can exit before one of its descendants. Signal
          // the process group again, then reap any remaining member before the
          // operation lock is released.
          signal("SIGTERM");
          await Bun.sleep(50);
          signal("SIGKILL");
          await Promise.allSettled(readers);
        } finally {
          clearTimeout(timer);
        }
      })());
    void completed.then(
      ([code, stdout, stderr]) => {
        if (code !== 0 && !command.allowFailure)
          resume(
            Effect.fail(
              new ManagerError({
                message: `${command.label} failed (exit ${code}). ${stderr.slice(-1500).trim()}`,
              }),
            ),
          );
        else resume(Effect.succeed({ code, stdout, stderr }));
      },
      async (error) => {
        // A failed output sink (for example a full disk) must not leave a build
        // running after its operation lock has been released.
        await stop();
        resume(
          Effect.fail(
            new ManagerError({ message: `${command.label}: ${error}` }),
          ),
        );
      },
    );
    return Effect.promise(stop);
  });

export const runnerLayer = (output?: (event: Output) => void) =>
  Layer.succeed(Runner, { run: (command) => runProcess(command, output) });
export const command = (
  label: string,
  argv: string[],
  options: Omit<Command, "label" | "argv"> = {},
) => Effect.flatMap(Runner, (r) => r.run({ label, argv, ...options }));
export const attempt = <A>(label: string, f: () => Promise<A>) =>
  Effect.tryPromise({
    try: f,
    catch: (error) => new ManagerError({ message: `${label}: ${error}` }),
  });
