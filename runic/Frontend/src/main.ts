import { mount } from "svelte";
import "@fontsource-variable/atkinson-hyperlegible-next/wght.css";
import "@fontsource-variable/atkinson-hyperlegible-mono/wght.css";
import "./app.css";
import App from "./App.svelte";

mount(App, { target: document.getElementById("app")! });
