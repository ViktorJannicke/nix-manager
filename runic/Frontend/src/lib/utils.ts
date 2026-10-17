import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";
import type { Snippet } from "svelte";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

export type WithElementRef<T, U extends HTMLElement = HTMLElement> = T & {
  ref?: U | null;
};

export type WithoutChild<T> = T extends { child?: unknown }
  ? Omit<T, "child">
  : T;

export type WithoutChildrenOrChild<T> = T extends {
  children?: unknown;
  child?: unknown;
}
  ? Omit<T, "children" | "child">
  : T;

export type WithChildren<T = unknown> = T & { children?: Snippet };
