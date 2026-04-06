import { io } from "socket.io-client";
import net from "node:net";

const WS_URL = "wss://puck2.nasejevs.com";
const WS_TIMEOUT_MS = 15000;
const TCP_CONNECT_TIMEOUT_MS = 2000;
const TCP_RESPONSE_TIMEOUT_MS = 2000;

/**
 * Fetch server list from Puck b312 master server.
 * Step 1: Get endpoints (IP:port) via WebSocket
 * Step 2: TCP preview request to each endpoint for server details
 */
export async function fetchB312Servers() {
  const endpoints = await fetchEndpoints();
  if (endpoints.length === 0) return [];

  console.log(`[b312] Got ${endpoints.length} endpoints, fetching previews...`);

  const results = await Promise.allSettled(
    endpoints.map((ep) => fetchServerPreview(ep))
  );

  const servers = [];
  for (let i = 0; i < results.length; i++) {
    if (results[i].status === "fulfilled" && results[i].value) {
      servers.push({
        ...results[i].value,
        ipAddress: endpoints[i].ipAddress,
        port: endpoints[i].port,
      });
    } else {
      servers.push({
        ipAddress: endpoints[i].ipAddress,
        port: endpoints[i].port,
        name: null,
        players: null,
        maxPlayers: null,
        isPasswordProtected: null,
        clientRequiredModIds: [],
        unreachable: true,
      });
    }
  }

  console.log(
    `[b312] Got details for ${servers.filter((s) => !s.unreachable).length}/${endpoints.length} servers`
  );
  return servers;
}

async function fetchEndpoints() {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      socket.disconnect();
      reject(new Error("b312: timed out waiting for endpoint list"));
    }, WS_TIMEOUT_MS);

    const socket = io(WS_URL, {
      reconnection: false,
      timeout: WS_TIMEOUT_MS,
      autoUpgrade: false,
      transports: ["websocket", "polling"],
      rejectUnauthorized: false,
    });

    socket.onAny((event, ...args) => {
      console.log(`[b312] Received event: "${event}"`, JSON.stringify(args).slice(0, 300));
    });

    socket.on("connect", () => {
      console.log("[b312] Connected (id: " + socket.id + ")");

      // Try ack callback
      socket.emit(
        "playerGetServerBrowserEndPointsRequest",
        null,
        (response) => {
          console.log("[b312] Got ack response");
          clearTimeout(timeout);
          const endpoints = parseEndpointResponse(response);
          socket.disconnect();
          resolve(endpoints);
        }
      );
    });

    // Also listen for the response as a separate event
    socket.on("playerGetServerBrowserEndPointsResponse", (response) => {
      console.log("[b312] Got event response");
      clearTimeout(timeout);
      const endpoints = parseEndpointResponse(response);
      socket.disconnect();
      resolve(endpoints);
    });

    socket.on("connect_error", (err) => {
      console.error(`[b312] Connect error: ${err.message}`);
    });

    socket.on("disconnect", (reason) => {
      console.log(`[b312] Disconnected: ${reason}`);
    });
  });
}

function parseEndpointResponse(response) {
  if (Array.isArray(response)) return response;
  if (response?.data?.endPoints) return response.data.endPoints;
  if (response?.endPoints) return response.endPoints;
  if (typeof response === "string") return parseEndpointResponse(JSON.parse(response));
  console.warn(
    "[b312] Endpoint response format:",
    JSON.stringify(response).slice(0, 500)
  );
  return [];
}

/**
 * Connect to a game server via TCP and request preview data.
 * Protocol: JSON over raw TCP
 *   Send: {"type":0}  (PreviewRequest = 0)
 *   Recv: {"type":1,"name":"...","players":N,...}  (PreviewResponse = 1)
 */
function fetchServerPreview(endpoint) {
  return new Promise((resolve, reject) => {
    const { ipAddress, port } = endpoint;
    let data = "";
    let resolved = false;

    const timeout = setTimeout(() => {
      if (!resolved) {
        resolved = true;
        client.destroy();
        reject(new Error(`TCP preview timeout for ${ipAddress}:${port}`));
      }
    }, TCP_CONNECT_TIMEOUT_MS + TCP_RESPONSE_TIMEOUT_MS);

    const client = new net.Socket();
    client.setTimeout(TCP_CONNECT_TIMEOUT_MS);

    client.connect(port, ipAddress, () => {
      const request = JSON.stringify({ type: 0 });
      client.write(request);
    });

    client.on("data", (chunk) => {
      data += chunk.toString("utf8");
      try {
        const msg = JSON.parse(data);
        if (msg.type === 1) {
          resolved = true;
          clearTimeout(timeout);
          client.destroy();
          resolve({
            name: msg.name,
            players: msg.players,
            maxPlayers: msg.maxPlayers,
            isPasswordProtected: msg.isPasswordProtected,
            clientRequiredModIds: msg.clientRequiredModIds || [],
          });
        }
      } catch {
        // Incomplete JSON, wait for more data
      }
    });

    client.on("error", (err) => {
      if (!resolved) {
        resolved = true;
        clearTimeout(timeout);
        reject(err);
      }
    });

    client.on("timeout", () => {
      if (!resolved) {
        resolved = true;
        clearTimeout(timeout);
        client.destroy();
        reject(new Error(`TCP connect timeout for ${ipAddress}:${port}`));
      }
    });
  });
}
