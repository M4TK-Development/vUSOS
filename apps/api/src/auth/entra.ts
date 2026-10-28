import { ConfidentialClientApplication, CryptoProvider } from "@azure/msal-node";
import type { AppConfig } from "../config.js";

export const ENTRA_SCOPES = ["openid", "profile", "email"];

export interface EntraIdentity {
  /** Stable per-user id: tenant + object id. */
  id: string;
  tenantId: string;
  email: string;
  name: string;
}

export interface EntraClient {
  /** Builds the authorization URL (PKCE S256) and the transient state to remember in the session. */
  startLogin(): Promise<{ url: string; transient: { state: string; nonce: string; verifier: string } }>;
  /** Exchanges the code and validates the ID token claims; throws on any mismatch. */
  finishLogin(
    query: { code: string; state: string },
    transient: { state: string; nonce: string; verifier: string }
  ): Promise<EntraIdentity>;
}

interface IdClaims {
  tid?: string;
  oid?: string;
  sub?: string;
  nonce?: string;
  name?: string;
  email?: string;
  preferred_username?: string;
  upn?: string;
}

export function createEntraClient(config: AppConfig): EntraClient {
  const msal = new ConfidentialClientApplication({
    auth: {
      clientId: config.entra.clientId,
      clientSecret: config.entra.clientSecret,
      authority: config.entra.authority
    }
  });
  const crypto = new CryptoProvider();
  const redirectUri = `${config.publicUrl}/api/auth/callback`;

  return {
    async startLogin() {
      const { verifier, challenge } = await crypto.generatePkceCodes();
      const state = crypto.createNewGuid();
      const nonce = crypto.createNewGuid();
      const url = await msal.getAuthCodeUrl({
        scopes: ENTRA_SCOPES,
        redirectUri,
        state,
        nonce,
        codeChallenge: challenge,
        codeChallengeMethod: "S256",
        prompt: "select_account"
      });
      return { url, transient: { state, nonce, verifier } };
    },

    async finishLogin(query, transient) {
      if (query.state !== transient.state) throw new Error("state mismatch");
      const result = await msal.acquireTokenByCode({
        code: query.code,
        scopes: ENTRA_SCOPES,
        redirectUri,
        codeVerifier: transient.verifier
      });
      const claims = (result.idTokenClaims ?? {}) as IdClaims;
      if (claims.nonce !== transient.nonce) throw new Error("nonce mismatch");
      const tenantId = claims.tid;
      const oid = claims.oid ?? claims.sub;
      const email = (claims.email ?? claims.preferred_username ?? claims.upn ?? "").toLowerCase();
      if (!tenantId || !oid || !email) throw new Error("ID token is missing tid/oid/email claims");
      return { id: `${tenantId.toLowerCase()}.${oid.toLowerCase()}`, tenantId, email, name: claims.name?.trim() || email };
    }
  };
}
