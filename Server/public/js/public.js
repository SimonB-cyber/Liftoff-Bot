// ── State ──────────────────────────────────────────────────────────────────
// pilotKey → { nick, laps: [], bestMs, lastDelta }
const livePilots = new Map();
let currentRaceId = null;
let currentTrack = null;
// actor → { actor, nick, lastSeen } for players currently in the lobby
const lobbyPlayers = new Map();
const LOBBY_TIMEOUT_MS = 10 * 60 * 1000; // 10 minutes

function lobbyTouch(actor, nick) {
  const existing = lobbyPlayers.get(actor);
  lobbyPlayers.set(actor, { actor, nick: nick || (existing && existing.nick) || String(actor), lastSeen: Date.now() });
}

function pruneLobby() {
  const cutoff = Date.now() - LOBBY_TIMEOUT_MS;
  for (const [actor, p] of lobbyPlayers) {
    if (p.lastSeen < cutoff) {
      lobbyPlayers.delete(actor);
    }
  }
  renderPlayers();
}

setInterval(pruneLobby, 60_000);

// ── Formatting ─────────────────────────────────────────────────────────────
// fmtMs, fmtDelta, esc are provided by shared.js

function fmtTrack(t) {
  if (!t || !t.track) return '';
  const parts = [t.env, t.track].filter(Boolean);
  return parts.join(' · ');
}

// ── Track display ──────────────────────────────────────────────────────────
function setTrack(track) {
  currentTrack = track;
  const label = fmtTrack(track);
  document.querySelector('#live-section h2').textContent =
    label ? `Race in Progress · ${label}` : 'Race in Progress';
}

// ── Render live race ───────────────────────────────────────────────────────
function renderLive(newNick) {
  const tbody = document.getElementById('live-body');
  if (livePilots.size === 0) {
    tbody.innerHTML = '<tr><td colspan="6" class="empty">Waiting for first lap…</td></tr>';
    renderPlayers();
    return;
  }

  const sorted = [...livePilots.values()].sort((a, b) => (a.bestMs || Infinity) - (b.bestMs || Infinity));

  tbody.innerHTML = sorted.map((p, i) => {
    const lastLap = p.laps[p.laps.length - 1];
    const rankClass = i === 0 ? 'gold' : i === 1 ? 'silver' : i === 2 ? 'bronze' : '';
    const isNew = p.nick === newNick;
    const delta = p.lastDelta;
    return `<tr class="${isNew ? 'lap-new' : ''}">
      <td class="rank ${rankClass}">${i + 1}</td>
      <td class="pilot-name">${esc(p.nick)}</td>
      <td>${p.laps.length}</td>
      <td class="lap-time best-time">${fmtMs(p.bestMs)}</td>
      <td class="lap-time">${fmtMs(lastLap)}</td>
      <td class="${delta != null ? (delta < 0 ? 'delta-neg' : 'delta-pos') : ''}">${fmtDelta(delta)}</td>
    </tr>`;
  }).join('');

  renderPlayers();
}

// ── Render players online ──────────────────────────────────────────────────
// Merges livePilots (pilots who have lapped this race) with lobbyPlayers
// (players the server has seen enter/leave the lobby). Showing only one source
// caused players to disappear when the server's lobby map was incomplete
// (e.g. players joined before a server restart).
function renderPlayers() {
  const tbody = document.getElementById('players-body');

  // Start with all pilots who have lapped — they are definitely active this race.
  const rows = new Map(); // nick → { nick, laps, bestMs }
  for (const p of livePilots.values()) {
    rows.set(p.nick, { nick: p.nick, laps: p.laps.length, bestMs: p.bestMs });
  }
  // Add lobby players not already shown (online but haven't lapped yet).
  for (const { nick } of lobbyPlayers.values()) {
    if (!rows.has(nick)) rows.set(nick, { nick, laps: 0, bestMs: null });
  }

  if (rows.size === 0) {
    tbody.innerHTML = '<tr><td colspan="3" class="empty">No pilots in lobby.</td></tr>';
    return;
  }

  const sorted = [...rows.values()].sort((a, b) => (a.bestMs || Infinity) - (b.bestMs || Infinity));
  tbody.innerHTML = sorted.map(r => `<tr>
    <td class="pilot-name"><span class="online-dot">●</span> ${esc(r.nick)}</td>
    <td>${r.laps || '—'}</td>
    <td class="lap-time best-time">${fmtMs(r.bestMs)}</td>
  </tr>`).join('');
}

// ── Apply race snapshot ────────────────────────────────────────────────────
function applyRaceSnapshot(race, trackSince) {
  if (!race) return;
  // Always clear and rebuild — snapshots are authoritative.
  livePilots.clear();
  currentRaceId = race.id;
  document.getElementById('race-meta').textContent =
    `Race started ${new Date(race.started_at).toLocaleTimeString()} · ID: ${race.id.slice(0, 8)}`;
  for (const lap of (race.laps || [])) {
    // In InfiniteRace mode all laps accumulate in one race across track changes.
    // Only replay laps recorded after the last track change so we show only the
    // current track's times, not every lap from the entire session.
    if (trackSince && lap.recorded_at < trackSince) continue;
    applyLap(lap.nick, lap.actor, lap.lap_ms, lap.steam_id, lap.pilot_guid);
  }
  renderLive(null);
}

// ── Apply a single lap ─────────────────────────────────────────────────────
// Use actor as the primary key for public clients. The public WebSocket strips
// steam_id and pilot_guid for privacy, so those are only available in snapshot
// data (from the database). Actor is session-unique and avoids merging pilots
// who share a nickname.
function applyLap(nick, actor, lapMs, steamId, pilotGuid) {
  const key = `actor-${actor}`;
  if (!livePilots.has(key)) livePilots.set(key, { nick: nick || `Actor ${actor}`, laps: [], bestMs: null, lastDelta: null });
  const p = livePilots.get(key);
  // Update nick in case it changed
  if (nick) p.nick = nick;
  const prevBest = p.bestMs;
  p.laps.push(lapMs);
  if (p.bestMs === null || lapMs < p.bestMs) p.bestMs = lapMs;
  p.lastDelta = prevBest != null ? lapMs - prevBest : null;
}

// ── Pilot activity stats ───────────────────────────────────────────────────
function loadPilotActivity() {
  fetch('/api/pilot-activity')
    .then(r => r.json())
    .then(data => {
      document.getElementById('activity-24h').textContent = data.last_24h ?? '0';
      document.getElementById('activity-7d').textContent  = data.last_7d  ?? '0';
      document.getElementById('activity-30d').textContent = data.last_30d ?? '0';
    })
    .catch(() => {});
}

// ── Initial HTTP fetch ─────────────────────────────────────────────────────
function loadInitialState() {
  fetch('/api/status')
    .then(r => r.json())
    .then(data => {
      if (data.current_track && data.current_track.track) setTrack(data.current_track);
      if (data.latest_race) applyRaceSnapshot(data.latest_race, data.track_since);
    })
    .catch(() => {});
}

// ── WebSocket ──────────────────────────────────────────────────────────────
// How long without a message before we consider the connection dead and force
// a reconnect. NAT/firewall devices can silently kill idle WebSocket connections
// without sending a TCP RST, so the browser never fires onclose.
// The server sends keepalives every 20s, so 60s gives 3 missed beats before we reconnect.
const WS_HEARTBEAT_MS = 60_000;

function connect() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const ws = new WebSocket(`${proto}://${location.host}/ws/live`);

  let lastMsgAt = Date.now();
  let heartbeat = null;

  function startHeartbeat() {
    heartbeat = setInterval(() => {
      if (Date.now() - lastMsgAt > WS_HEARTBEAT_MS) {
        // No message for too long — connection is probably frozen.
        // Closing triggers onclose which schedules a fresh reconnect.
        ws.close();
      }
    }, 10_000);
  }

  function stopHeartbeat() {
    if (heartbeat !== null) { clearInterval(heartbeat); heartbeat = null; }
  }

  ws.onopen = () => {
    lastMsgAt = Date.now();
    startHeartbeat();
    document.getElementById('status-dot').className = 'dot connected';
    document.getElementById('status-label').textContent = 'Live';
  };

  ws.onclose = () => {
    stopHeartbeat();
    document.getElementById('status-dot').className = 'dot disconnected';
    document.getElementById('status-label').textContent = 'Reconnecting…';
    setTimeout(connect, 3000);
  };

  ws.onmessage = ({ data }) => {
    lastMsgAt = Date.now();

    let event;
    try { event = JSON.parse(data); } catch { return; }

    if (event.event_type === 'keepalive') return; // just resets lastMsgAt above

    if (event.event_type === 'state_snapshot') {
      if (event.current_track && event.current_track.track) setTrack(event.current_track);
      if (event.online_players) {
        lobbyPlayers.clear();
        for (const p of event.online_players) lobbyTouch(p.actor, p.nick);
      }
      applyRaceSnapshot(event.race, event.track_since);
      return;
    }

    if (event.event_type === 'track_changed') {
      // Clear current race data — it belongs to the previous track
      livePilots.clear();
      currentRaceId = null;
      document.getElementById('race-meta').textContent = '';
      setTrack({ env: event.env, track: event.track, race: event.race });
      renderLive(null);
      return;
    }

    if (event.event_type === 'lap_recorded') {
      if (event.race_id !== currentRaceId) {
        livePilots.clear();
        currentRaceId = event.race_id;
        document.getElementById('race-meta').textContent =
          `Race started ${new Date().toLocaleTimeString()} · ID: ${event.race_id.slice(0, 8)}`;
      }
      applyLap(event.nick, event.actor, event.lap_ms, event.steam_id, event.pilot_guid);
      // Count a lap as activity — keeps pilots who race but don't trigger player_entered
      if (lobbyPlayers.has(event.actor)) lobbyTouch(event.actor, event.nick);
      renderLive(event.nick);
      return;
    }

    if (event.event_type === 'player_entered') {
      lobbyTouch(event.actor, event.nick);
      renderPlayers();
      return;
    }

    if (event.event_type === 'player_left') {
      lobbyPlayers.delete(event.actor);
      renderPlayers();
      return;
    }

    if (event.event_type === 'player_list') {
      lobbyPlayers.clear();
      for (const p of (event.players || [])) lobbyTouch(p.actor, p.nick);
      renderPlayers();
      return;
    }

    if (event.event_type === 'race_reset') {
      livePilots.clear();
      currentRaceId = event.race_id;
      document.getElementById('race-meta').textContent =
        `Race reset at ${new Date().toLocaleTimeString()} · ID: ${event.race_id.slice(0, 8)}`;
      renderLive(null);
      return;
    }
  };
}

loadInitialState();
loadPilotActivity();
connect();
