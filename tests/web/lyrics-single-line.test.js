import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";
import { describe, expect, it } from "vitest";

const root = new URL("../..", import.meta.url);
const read = relativePath => readFile(new URL(relativePath, root), "utf8");

describe("lyrics single-line mode", () => {
  async function setup() {
    const [html, bridge, state, presentation, script, style] = await Promise.all([
      read("TaskbarLyrics.App/Web/Lyrics/index.html"),
      read("TaskbarLyrics.App/Web/Lyrics/bridge.js"),
      read("TaskbarLyrics.App/Web/Lyrics/state.js"),
      read("TaskbarLyrics.App/Web/Lyrics/presentation.js"),
      read("TaskbarLyrics.App/Web/Lyrics/app.js"),
      read("TaskbarLyrics.App/Web/Lyrics/style.css")
    ]);
    const dom = new JSDOM(html.replace("{{STYLE_CSS}}", "").replace("{{APP_JS}}", ""), {
      runScripts: "outside-only"
    });
    dom.window.CSS = { supports: () => true };
    dom.window.requestAnimationFrame = () => 1;
    dom.window.cancelAnimationFrame = () => {};
    dom.window.eval(bridge);
    dom.window.eval(state);
    dom.window.eval(presentation);
    dom.window.eval(script);
    return { dom, style };
  }

  function enableSingleLine(dom) {
    dom.window.taskbarLyrics.receive({
      version: 1,
      type: "style",
      payload: { singleLineMode: true }
    });
  }

  it("toggles the single-line class from the style payload and declares the hiding rule", async () => {
    const { dom, style } = await setup();
    const layout = dom.window.document.querySelector("#layout");

    expect(style).toContain(".layout.single-line .track > .next-line");
    expect(style).toContain(".layout.single-line .incoming-translation-pair");
    expect(layout.classList.contains("single-line")).toBe(false);

    enableSingleLine(dom);
    expect(layout.classList.contains("single-line")).toBe(true);

    dom.window.taskbarLyrics.receive({
      version: 1,
      type: "style",
      payload: { singleLineMode: false }
    });
    expect(layout.classList.contains("single-line")).toBe(false);
  });

  it("replaces the current line in place and never enters translation mode", async () => {
    const { dom } = await setup();
    enableSingleLine(dom);

    const layout = dom.window.document.querySelector("#layout");
    const track = dom.window.document.querySelector("#track");
    const currentLineText = dom.window.document.querySelector("#currentLineText");
    const receive = payload => dom.window.taskbarLyrics.receive({
      version: 1,
      type: "lyrics",
      payload
    });

    receive({
      current: "Line one",
      next: "Next line",
      progress: 0.25,
      currentLineIndex: 0,
      trackId: "",
      isPureMusic: false,
      isPlaying: true,
      currentTranslation: "译一",
      nextTranslation: "译二",
      translationMode: true
    });
    expect(layout.classList.contains("translation-mode")).toBe(false);
    expect(currentLineText.textContent).toBe("Line one");

    receive({
      current: "Line two",
      next: "Following",
      progress: 0.1,
      currentLineIndex: 1,
      trackId: "",
      isPureMusic: false,
      isPlaying: true
    });
    expect(currentLineText.textContent).toBe("Line two");
    expect(track.classList.contains("animating")).toBe(false);
    expect(track.style.transform).toBe("");
  });
});
