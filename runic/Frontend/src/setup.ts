// The setup assistant's Window (SetupWindow): its own page, before there is a checkout and a dashboard.
import { mount } from "svelte";
import { installErrorReporting } from "@runic-artifex/views";
import "@fontsource-variable/atkinson-hyperlegible-next/wght.css";
import "@fontsource-variable/atkinson-hyperlegible-mono/wght.css";
import "./app.css";
import { followWindowLocale } from "$lib/locale.svelte";
import Setup from "./Setup.svelte";

// Errors in the page reach the backend's log (Logging.cs) instead of staying in the web view's console.
installErrorReporting();
followWindowLocale();
mount(Setup, { target: document.getElementById("app")! });
