import { describe, expect, it } from "vitest";

import { GATEWAY_PACKAGE } from "../src/index.js";

describe("gateway package surface", () => {
  it("exports the package name", () => {
    expect(GATEWAY_PACKAGE).toBe("@ofac/gateway");
  });
});
