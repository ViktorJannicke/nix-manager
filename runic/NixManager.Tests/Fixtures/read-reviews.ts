/**
 * Reads review records, operation records with their .undo contents and the draft another version wrote, with
 * the Bun version's own functions, and prints what it read as JSON. ReviewCompatibilityTests runs it on records
 * the Runic port wrote:
 *
 *     bun runic/NixManager.Tests/Fixtures/read-reviews.ts FLAKE STATE_DIR HOME_PROFILE
 *
 * For every review: getReview and the review-apply plan. For every applied change with an undo point: the plan
 * of its undo review, which reads the .undo contents. Nothing runs.
 */
import { existsSync } from "node:fs";
import { join } from "node:path";
import { plan, request, type Config } from "../../../src/domain";
import { readDraft } from "../../../src/draft";
import { Manager } from "../../../src/manager";
import { getReview, listReviews } from "../../../src/reviews";

const [flake, stateDir, homeProfile] = process.argv.slice(2);
const config: Config = {
  flake,
  stateDir,
  homeProfile,
  homeProfileExists: true,
  hostKey: "test",
  hosts: {
    test: {
      system: "x86_64-linux",
      userName: "test",
      nixosHostName: "test-machine",
      homeConfiguration: "user-test",
      nixosConfiguration: "test-machine",
    },
  },
  privileged: ["pkexec", "/immutable/bin/nix-manager", "--privileged"],
};
const manager = new Manager(config);
const reviews = await Promise.all(
  (await listReviews(config)).map(async (listed) => {
    const review = getReview(config, listed.id);
    const steps = plan(config, request({ action: "review-apply", reviewId: review.id, scope: review.scope }), review).steps;
    return { id: review.id, source: review.source, apply: steps.map((step) => step.label) };
  }),
);
const undos = manager
  .history()
  .filter((operation) => operation.undo)
  .map((operation) => ({
    id: operation.id,
    changes: operation.undo!.changes,
    plan: manager.plan(request({ action: "review-build", undo: operation.id })).steps.map((step) => step.label),
  }));
const draft = existsSync(join(stateDir, "draft.json")) ? readDraft(config) : undefined;
console.log(JSON.stringify({ reviews, undos, draft }));
