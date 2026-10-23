import { describe, expect, it } from "vitest";

import { LINK_PACKAGE } from "../src/index.js";

describe("workspace-link package surface", () => {
  it("exports the package name", () => {
    expect(LINK_PACKAGE).toBe("@ofac/workspace-link");
  });
});
