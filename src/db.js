import Database from "better-sqlite3";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const DB_PATH = path.join(__dirname, "..", "data", "servers.db");

let db;

export function initDb() {
  const dataDir = path.dirname(DB_PATH);
  if (!fs.existsSync(dataDir)) fs.mkdirSync(dataDir, { recursive: true });

  db = new Database(DB_PATH);
  db.pragma("journal_mode = WAL");

  db.exec(`
    CREATE TABLE IF NOT EXISTS snapshots (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      timestamp TEXT NOT NULL,
      version TEXT NOT NULL  -- 'b202' or 'b312'
    );

    CREATE TABLE IF NOT EXISTS servers (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
      ip_address TEXT NOT NULL,
      port INTEGER NOT NULL,
      name TEXT,
      players INTEGER,
      max_players INTEGER,
      is_password_protected INTEGER,
      unreachable INTEGER DEFAULT 0,
      -- geoip
      country TEXT,
      region TEXT,
      city TEXT,
      lat REAL,
      lon REAL
    );

    CREATE INDEX IF NOT EXISTS idx_servers_snapshot ON servers(snapshot_id);
    CREATE INDEX IF NOT EXISTS idx_snapshots_timestamp ON snapshots(timestamp);
  `);

  return db;
}

export function saveSnapshot(version, servers, timestamp) {
  const insertSnapshot = db.prepare(
    "INSERT INTO snapshots (timestamp, version) VALUES (?, ?)"
  );
  const insertServer = db.prepare(`
    INSERT INTO servers (snapshot_id, ip_address, port, name, players, max_players,
      is_password_protected, unreachable, country, region, city, lat, lon)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `);

  const txn = db.transaction(() => {
    const { lastInsertRowid } = insertSnapshot.run(timestamp, version);
    for (const s of servers) {
      insertServer.run(
        lastInsertRowid,
        s.ipAddress,
        s.port,
        s.name ?? null,
        s.players ?? null,
        s.maxPlayers ?? null,
        s.isPasswordProtected ? 1 : 0,
        s.unreachable ? 1 : 0,
        s.geo?.country ?? null,
        s.geo?.region ?? null,
        s.geo?.city ?? null,
        s.geo?.lat ?? null,
        s.geo?.lon ?? null
      );
    }
    return lastInsertRowid;
  });

  return txn();
}

export function getDb() {
  return db;
}
