import { mount } from "svelte";
import "@fontsource-variable/atkinson-hyperlegible-next/wght.css";
import "@fontsource-variable/atkinson-hyperlegible-mono/wght.css";
import App from "./App.svelte";
import { localeFrom, setLocale } from "../src/i18n";
import "./app.css";

// The server says which language to speak; until it does, the last one it said,
// or the browser's. A different answer reloads the page in that language.
const locale = localeFrom([localStorage.getItem("nix-manager-locale") ?? undefined, ...navigator.languages]);
setLocale(locale);
document.documentElement.lang = locale;

mount(App, { target: document.getElementById("app")! });
