import { describe, expect, it } from "vitest";
import { isAllowedIdentity } from "./policy.js";

const cfg = { allowedTenantIds: ["tenant-1"], allowedEmailDomains: ["vistula.edu.pl", "stu.vistula.edu.pl"] };

describe("isAllowedIdentity", () => {
  it("allows a listed tenant regardless of e-mail", () => {
    expect(isAllowedIdentity(cfg, { tenantId: "TENANT-1", email: "x@gmail.com" })).toBe(true);
  });
  it("allows a listed e-mail domain", () => {
    expect(isAllowedIdentity(cfg, { tenantId: "other", email: "Jan@stu.vistula.edu.pl" })).toBe(true);
  });
  it("rejects look-alike domains", () => {
    expect(isAllowedIdentity(cfg, { tenantId: "other", email: "a@evilvistula.edu.pl" })).toBe(false);
    expect(isAllowedIdentity(cfg, { tenantId: null, email: "a@vistula.edu.pl.evil.com" })).toBe(false);
  });
  it("rejects everything with empty lists", () => {
    expect(isAllowedIdentity({ allowedTenantIds: [], allowedEmailDomains: [] }, { tenantId: "t", email: "a@b.pl" })).toBe(false);
  });
});
