import { mount } from "svelte";
import { installErrorReporting } from "@runic-artifex/views";
import "@fontsource-variable/atkinson-hyperlegible-next/wght.css";
import "@fontsource-variable/atkinson-hyperlegible-mono/wght.css";
import "./app.css";
import App from "./App.svelte";

// Errors in the page reach the backend's log (Logging.cs) instead of staying in the web view's console.
installErrorReporting();
mount(App, { target: document.getElementById("app")! });
