import { mount } from "svelte";
import "@fontsource-variable/atkinson-hyperlegible-next/wght.css";
import "@fontsource-variable/atkinson-hyperlegible-mono/wght.css";
import App from "./App.svelte";
import "./app.css";

mount(App, { target: document.getElementById("app")! });
