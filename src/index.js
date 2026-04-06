import { fetchB202Servers } from "./b202.js";
import { fetchB312Servers } from "./b312.js";
import { lookupIp } from "./geo.js";
import { initDb, saveSnapshot } from "./db.js";

const INTERVAL_MS = 30 * 60 * 1000; // 30 minutes
const RUN_ONCE = process.argv.includes("--once");

async function scan() {
  const timestamp = new Date().toISOString();
  console.log(`\n${"=".repeat(60)}`);
  console.log(`Scan at ${timestamp}`);
  console.log("=".repeat(60));

  // Fetch from both versions in parallel
  const [b202Result, b312Result] = await Promise.allSettled([
    fetchB202Servers(),
    fetchB312Servers(),
  ]);

  if (b202Result.status === "fulfilled") {
    const servers = enrichWithGeo(b202Result.value);
    printServers("b202", servers);
    saveSnapshot("b202", servers, timestamp);
  } else {
    console.error(`[b202] Failed: ${b202Result.reason.message}`);
  }

  if (b312Result.status === "fulfilled") {
    const servers = enrichWithGeo(b312Result.value);
    printServers("b312", servers);
    saveSnapshot("b312", servers, timestamp);
  } else {
    console.error(`[b312] Failed: ${b312Result.reason.message}`);
  }
}

function enrichWithGeo(servers) {
  return servers.map((s) => ({
    ...s,
    geo: lookupIp(s.ipAddress),
  }));
}

function printServers(version, servers) {
  if (servers.length === 0) {
    console.log(`\n[${version}] No servers found.`);
    return;
  }

  const totalPlayers = servers.reduce((sum, s) => sum + (s.players ?? 0), 0);
  const reachable = servers.filter((s) => !s.unreachable);
  console.log(
    `\n[${version}] ${servers.length} servers | ${totalPlayers} total players`
  );
  console.log("-".repeat(90));
  console.log(
    padRight("SERVER NAME", 30) +
      padRight("PLAYERS", 10) +
      padRight("IP:PORT", 24) +
      padRight("LOCATION", 26)
  );
  console.log("-".repeat(90));

  // Sort by player count descending
  const sorted = [...servers].sort(
    (a, b) => (b.players ?? -1) - (a.players ?? -1)
  );

  for (const s of sorted) {
    const name = s.unreachable
      ? `(unreachable)`
      : truncate(s.name ?? "???", 28);
    const players = s.unreachable
      ? "?"
      : `${s.players}/${s.maxPlayers}`;
    const addr = `${s.ipAddress}:${s.port}`;
    const loc = s.geo
      ? `${s.geo.city || "?"}, ${s.geo.country || "?"}`
      : "Unknown";

    console.log(
      padRight(name, 30) +
        padRight(players, 10) +
        padRight(addr, 24) +
        padRight(loc, 26)
    );
  }
}

function padRight(str, len) {
  return str.length >= len ? str.slice(0, len) : str + " ".repeat(len - str.length);
}

function truncate(str, len) {
  return str.length > len ? str.slice(0, len - 1) + "…" : str;
}

async function main() {
  await initDb();
  console.log("PuckServerTracker started");
  console.log(`Polling interval: ${INTERVAL_MS / 60000} minutes`);
  console.log(`Mode: ${RUN_ONCE ? "single scan" : "continuous"}`);

  await scan();

  if (!RUN_ONCE) {
    setInterval(scan, INTERVAL_MS);
    console.log(`\nNext scan in ${INTERVAL_MS / 60000} minutes...`);
  }
}

main().catch((err) => {
  console.error("Fatal error:", err);
  process.exit(1);
});
