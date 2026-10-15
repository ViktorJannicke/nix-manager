// The setup assistant's Window (SetupWindow): its own page, before there is a checkout and a dashboard.
import { mount } from "svelte";
import "@fontsource-variable/atkinson-hyperlegible-next/wght.css";
import "@fontsource-variable/atkinson-hyperlegible-mono/wght.css";
import "./app.css";
import Setup from "./Setup.svelte";

mount(Setup, { target: document.getElementById("app")! });
