import type { AppConfig } from "../config.js";

/**
 * Who may use the app: tenant id on the allow-list, OR e-mail domain on the allow-list.
 * Note: in a multi-tenant app registration the e-mail claim is controlled by the user's home tenant,
 * so ALLOWED_TENANT_IDS is the strong check; the domain list is a convenience when the tenant id is unknown.
 */
export function isAllowedIdentity(
  config: Pick<AppConfig, "allowedTenantIds" | "allowedEmailDomains">,
  identity: { tenantId: string | null; email: string }
): boolean {
  const tid = identity.tenantId?.toLowerCase() ?? null;
  if (tid && config.allowedTenantIds.includes(tid)) return true;
  const domain = identity.email.toLowerCase().split("@")[1];
  return !!domain && config.allowedEmailDomains.includes(domain);
}
