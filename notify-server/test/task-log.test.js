import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { createMessage, normalizeTaskLog } from "../src/message.js";

const cases = JSON.parse(readFileSync(new URL("../../tests/fixtures/task-log-cases.json", import.meta.url), "utf8"));

describe("task log wire contract", () => {
  it.each(cases)("preserves $name through JSON", ({ taskLog }) => {
    const wire = JSON.parse(JSON.stringify(createMessage("result", { source: "codex", taskLog })));
    expect(wire.taskLog).toBe(taskLog);
  });
  it("bounds logs without splitting Unicode pairs and falls back on blank input", () => {
    expect(normalizeTaskLog("x".repeat(3999) + "😀suffix")).toBe("x".repeat(3999));
    expect(normalizeTaskLog("x".repeat(4100))).toHaveLength(4000);
    expect(normalizeTaskLog(" \r\n ")).toBe("");
  });
});
