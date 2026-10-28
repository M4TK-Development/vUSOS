import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { buildApp } from "./app.js";
import { loadConfig } from "./config.js";
import { openDb } from "./db.js";

// Load apps/api/.env if present (built-in loader; does not override variables already set in the environment).
const envFile = fileURLToPath(new URL("../.env", import.meta.url));
if (existsSync(envFile)) process.loadEnvFile(envFile);

const config = loadConfig();
const db = openDb(config.databasePath);
const app = buildApp(config, db);

async function shutdown(signal: string): Promise<void> {
  app.log.info({ signal }, "shutting down");
  await app.close();
  db.close();
  process.exit(0);
}
process.on("SIGINT", () => void shutdown("SIGINT"));
process.on("SIGTERM", () => void shutdown("SIGTERM"));

try {
  await app.listen({ host: config.host, port: config.port });
  app.log.info(
    `vUSOS API: ${config.publicUrl} (auth=${config.authMode}, adapters=${config.adapterMode}${config.staticDir ? `, static=${config.staticDir}` : ""})`
  );
} catch (err) {
  app.log.error({ err }, "failed to start");
  process.exit(1);
}
