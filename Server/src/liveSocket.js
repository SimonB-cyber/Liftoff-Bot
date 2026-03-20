const { WebSocketServer } = require('ws');
const db = require('./database');
const { getCurrentTrack, getOnlinePlayers, getCurrentTrackSince } = require('./state');

const ADMIN_TOKEN = process.env.ADMIN_TOKEN || '';
const COOKIE_NAME = 'liftoff_admin';

function parseCookies(header) {
  const cookies = {};
  if (!header) return cookies;
  for (const part of header.split(';')) {
    const [k, ...v] = part.trim().split('=');
    if (k) cookies[k] = v.join('=');
  }
  return cookies;
}

/**
 * Creates two WebSocket servers for browser clients:
 *   /ws/live  — public, unauthenticated, receives only whitelisted events
 *   /ws/admin — authenticated via cookie or query param, receives all events
 */
function createLiveSocketServer(httpServer) {
  const publicWss = new WebSocketServer({ noServer: true });
  const adminWss  = new WebSocketServer({ noServer: true });

  httpServer.on('upgrade', (req, socket, head) => {
    const url = new URL(req.url, `http://${req.headers.host}`);

    if (url.pathname === '/ws/live') {
      publicWss.handleUpgrade(req, socket, head, (ws) => {
        publicWss.emit('connection', ws, req);
      });
    } else if (url.pathname === '/ws/admin') {
      // Authenticate via cookie (preferred) or query param (legacy fallback)
      const cookies = parseCookies(req.headers.cookie);
      const token = cookies[COOKIE_NAME] || url.searchParams.get('token') || '';
      if (!ADMIN_TOKEN || token !== ADMIN_TOKEN) {
        console.warn('[admin-ws] Rejected connection: invalid token');
        socket.write('HTTP/1.1 401 Unauthorized\r\n\r\n');
        socket.destroy();
        return;
      }
      adminWss.handleUpgrade(req, socket, head, (ws) => {
        adminWss.emit('connection', ws, req);
      });
    }
  });

  // Send a keepalive ping to all connected clients every 20 seconds.
  // This prevents NAT/firewall devices from silently dropping idle connections,
  // and lets the client detect a frozen connection via its own heartbeat timer.
  const keepalivePayload = JSON.stringify({ event_type: 'keepalive' });
  setInterval(() => {
    for (const wss of [publicWss, adminWss]) {
      for (const client of wss.clients) {
        if (client.readyState === 1 /* OPEN */) {
          try { client.send(keepalivePayload); } catch (_) {}
        }
      }
    }
  }, 20_000);

  // ── Public /ws/live — limited snapshot, no sensitive fields ────────────────

  publicWss.on('connection', (ws) => {
    console.log('[live] Public client connected');

    try {
      const trackSince = getCurrentTrackSince();
      const race = db.getLatestRaceWithLaps(trackSince);
      const players = getOnlinePlayers().map(({ actor, nick }) => ({ actor, nick }));
      ws.send(JSON.stringify({
        event_type: 'state_snapshot',
        race: stripSensitiveFromRace(race),
        current_track: getCurrentTrack(),
        track_since: trackSince,
        online_players: players,
      }));
    } catch (err) {
      console.error('[live] Failed to send state snapshot:', err.message);
    }

    ws.on('close', () => console.log('[live] Public client disconnected'));
    ws.on('error', (err) => console.error('[live] Public client error:', err.message));
  });

  // ── Admin /ws/admin — full snapshot, all events ───────────────────────────

  adminWss.on('connection', (ws) => {
    console.log('[admin-ws] Admin client connected');

    try {
      const race = db.getLatestRaceWithLaps();
      ws.send(JSON.stringify({
        event_type: 'state_snapshot',
        race,
        current_track: getCurrentTrack(),
        track_since: getCurrentTrackSince(),
        online_players: getOnlinePlayers(),
      }));
    } catch (err) {
      console.error('[admin-ws] Failed to send state snapshot:', err.message);
    }

    ws.on('close', () => console.log('[admin-ws] Admin client disconnected'));
    ws.on('error', (err) => console.error('[admin-ws] Admin client error:', err.message));
  });

  // ── Broadcast helpers ─────────────────────────────────────────────────────

  function broadcastPublic(message) {
    const payload = typeof message === 'string' ? message : JSON.stringify(message);
    for (const client of publicWss.clients) {
      if (client.readyState === 1 /* OPEN */) {
        client.send(payload);
      }
    }
  }

  function broadcastAdmin(message) {
    const payload = typeof message === 'string' ? message : JSON.stringify(message);
    for (const client of adminWss.clients) {
      if (client.readyState === 1 /* OPEN */) {
        client.send(payload);
      }
    }
  }

  return { publicWss, adminWss, broadcastPublic, broadcastAdmin };
}

/**
 * Remove sensitive fields (user_id, steam_id) from a race snapshot
 * so that public clients only see safe data.
 */
function stripSensitiveFromRace(race) {
  if (!race) return race;
  const cleaned = { ...race };
  if (Array.isArray(cleaned.laps)) {
    cleaned.laps = cleaned.laps.map(({ user_id, steam_id, ...lap }) => lap);
  }
  return cleaned;
}

module.exports = { createLiveSocketServer };
