import { readFile } from "node:fs/promises";
import { JSDOM } from "jsdom";
import { describe, expect, it } from "vitest";

const root = new URL("../..", import.meta.url);

async function loadPresentation() {
  const source = await readFile(
    new URL("TaskbarLyrics.App/Web/Lyrics/presentation.js", root),
    "utf8");
  const dom = new JSDOM("", { runScripts: "outside-only" });
  dom.window.eval(source);
  return dom.window.taskbarLyricsPresentation;
}

describe("computeFittedFontSize", () => {
  it("keeps the base size when the text already fits", async () => {
    const api = await loadPresentation();

    expect(api.computeFittedFontSize(14, 200, 150)).toBeCloseTo(14);
  });

  it("keeps the base size when the text exactly fills the viewport", async () => {
    const api = await loadPresentation();

    expect(api.computeFittedFontSize(14, 200, 200)).toBeCloseTo(14);
  });

  it("shrinks proportionally when the text overflows", async () => {
    const api = await loadPresentation();

    // 200px viewport / 250px content -> ratio 0.8 -> 14 * 0.8 = 11.2
    expect(api.computeFittedFontSize(14, 200, 250)).toBeCloseTo(11.2);
  });

  it("clamps to the minimum ratio for extreme overflow", async () => {
    const api = await loadPresentation();

    // Ratio would be 0.1, but the default minimum is 0.6 -> 14 * 0.6 = 8.4
    expect(api.computeFittedFontSize(14, 200, 2000)).toBeCloseTo(8.4);
  });

  it("returns the base size for invalid measurements", async () => {
    const api = await loadPresentation();

    expect(api.computeFittedFontSize(14, 0, 400)).toBeCloseTo(14);
    expect(api.computeFittedFontSize(14, 200, 0)).toBeCloseTo(14);
    expect(api.computeFittedFontSize(14, -1, 400)).toBeCloseTo(14);
    expect(api.computeFittedFontSize(14, 200, -1)).toBeCloseTo(14);
  });

  it("honors a custom minimum ratio", async () => {
    const api = await loadPresentation();

    // Custom minimum 0.5 -> 14 * 0.5 = 7 even though the raw ratio is 0.1.
    expect(api.computeFittedFontSize(14, 200, 2000, 0.5)).toBeCloseTo(7);
  });
});
