import { expect, test } from "bun:test";
import { bundles } from "../src/bundles";
import { experiences } from "../src/curated";

test("bundles name curated features that exist, and each item once", () => {
  for (const bundle of bundles) {
    for (const id of bundle.features) expect(experiences.some((item) => item.id === id)).toBe(true);
    expect(new Set(bundle.apps).size).toBe(bundle.apps.length);
    expect(bundle.apps.every((attr) => /^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*$/.test(attr))).toBe(true);
  }
  expect(new Set(bundles.map((bundle) => bundle.id)).size).toBe(bundles.length);
});
