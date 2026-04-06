import { io } from "socket.io-client";

const WS_URL = "wss://puck1.nasejevs.com";
const TIMEOUT_MS = 15000;

/**
 * Fetch server list from Puck b202 master server.
 * Returns array of { ipAddress, port, pingPort, name, maxPlayers, isPasswordProtected, players }
 */
export async function fetchB202Servers() {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      socket.disconnect();
      reject(new Error("b202: timed out waiting for server list"));
    }, TIMEOUT_MS);

    const socket = io(WS_URL, {
      reconnection: false,
      timeout: TIMEOUT_MS,
      autoUpgrade: false,
      transports: ["websocket", "polling"],
      rejectUnauthorized: false,
    });

    socket.onAny((event, ...args) => {
      console.log(`[b202] Received event: "${event}"`, JSON.stringify(args).slice(0, 300));
    });

    socket.on("connect", () => {
      console.log("[b202] Connected (id: " + socket.id + ")");

      // Try both patterns:
      // 1. Ack callback (game's primary pattern)
      // 2. Listen for response event (fallback)
      socket.emit("playerGetServerBrowserServersRequest", null, (response) => {
        console.log("[b202] Got ack response");
        clearTimeout(timeout);
        const servers = parseResponse(response);
        socket.disconnect();
        resolve(servers);
      });
    });

    // Also listen for the response as a separate event
    socket.on("playerGetServerBrowserServersResponse", (response) => {
      console.log("[b202] Got event response");
      clearTimeout(timeout);
      const servers = parseResponse(response);
      socket.disconnect();
      resolve(servers);
    });

    socket.on("connect_error", (err) => {
      console.error(`[b202] Connect error: ${err.message}`);
    });

    socket.on("disconnect", (reason) => {
      console.log(`[b202] Disconnected: ${reason}`);
    });
  });
}

function parseResponse(response) {
  if (Array.isArray(response)) return response;
  if (response?.data?.serverBrowserServers) return response.data.serverBrowserServers;
  if (response?.serverBrowserServers) return response.serverBrowserServers;
  if (typeof response === "string") return parseResponse(JSON.parse(response));
  console.warn("[b202] Response format:", JSON.stringify(response).slice(0, 500));
  return [];
}
