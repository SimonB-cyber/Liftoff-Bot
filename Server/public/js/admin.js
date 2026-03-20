// ── Token management ───────────────────────────────────────────────────────
// Login sets an httpOnly cookie (used by WebSocket) AND returns the token
// so we can send it as Bearer header for API calls (more reliable behind proxies).
let adminToken = '';

async function saveToken() {
  const val = document.getElementById('token-input').value.trim();
  if (!val) return;
  try {
    const res = await fetch('/api/admin/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: val }),
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      toast(data.error || 'Login failed', 'error');
      return;
    }
    adminToken = val;
    document.getElementById('token-input').value = '';
    document.getElementById('token-overlay').style.display = 'none';
    init();
  } catch (err) {
    toast('Login failed: ' + err.message, 'error');
  }
}

async function clearToken() {
  try { await fetch('/api/admin/logout', { method: 'POST' }); } catch {}
  adminToken = '';
  document.getElementById('token-overlay').style.display = 'flex';
}

// ── Toast ──────────────────────────────────────────────────────────────────
let toastTimer;
function toast(msg, type = 'ok') {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className = `toast show ${type}`;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.className = 'toast', 3000);
}

// ── API helpers ────────────────────────────────────────────────────────────
async function apiFetch(method, path, body) {
  const headers = { 'Content-Type': 'application/json' };
  if (adminToken) headers['Authorization'] = `Bearer ${adminToken}`;
  const res = await fetch(path, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.error || res.statusText);
  return data;
}

// ── Plugin status ──────────────────────────────────────────────────────────
function updatePluginStatus(connected) {
  const dot = document.getElementById('plugin-dot');
  dot.className = `dot ${connected ? 'connected' : 'disconnected'}`;
  document.getElementById('plugin-label').textContent = `Plugin: ${connected ? 'Connected' : 'Disconnected'}`;
}

async function pollStatus() {
  try {
    const data = await fetch('/api/status').then(r => r.json());
    updatePluginStatus(data.plugin_connected);
  } catch {}
  setTimeout(pollStatus, 5000);
}

// ── Catalog ────────────────────────────────────────────────────────────────
let catalog = null;

async function loadCatalog() {
  try {
    catalog = await fetch('/api/catalog').then(r => r.json());
    if (catalog.error) { catalog = null; return; }
    populateCatalogSelects();
    const envCount = catalog.environments?.length || 0;
    const trackCount = catalog.environments?.reduce((n, e) => n + (e.tracks?.length || 0), 0) || 0;
    document.getElementById('catalog-status').textContent =
      `${envCount} environments · ${trackCount} tracks · ${catalog.game_modes?.length || 0} game modes`;
    document.getElementById('catalog-age').textContent =
      `Catalog: ${new Date(catalog.timestamp_utc || catalog.recorded_at || '').toLocaleString() || 'loaded'}`;
  } catch {}
}

function populateCatalogSelects() {
  if (!catalog) return;

  const envSel = document.getElementById('env-select');
  envSel.innerHTML = '<option value="">— Select environment —</option>' +
    (catalog.environments || []).map(e =>
      `<option value="${esc(e.internal_name || e.caption)}">${esc(e.display_name || e.caption)}</option>`
    ).join('');

  const modeSel = document.getElementById('mode-select');
  modeSel.innerHTML = '<option value="">— Any mode —</option>' +
    (catalog.game_modes || [])
      .filter(m => m.name !== 'None')
      .map(m => `<option value="${esc(m.name)}">${esc(m.name)}</option>`)
    .join('');

  // Also populate playlist track-add dropdowns
  populatePlaylistCatalogSelects();
}

function onEnvChange() {
  const envKey = document.getElementById('env-select').value;
  const trackSel = document.getElementById('track-select');
  trackSel.innerHTML = '<option value="">— Select track —</option>';
  if (!catalog || !envKey) return;
  const env = catalog.environments?.find(e => (e.internal_name || e.caption) === envKey);
  if (!env) return;
  trackSel.innerHTML += (env.tracks || []).map(t =>
    `<option value="${esc(t.name)}">${esc(t.name)}</option>`
  ).join('');
}

async function refreshCatalog() {
  try {
    await apiFetch('POST', '/api/admin/catalog/refresh');
    toast('Catalog refresh sent — open the track popup in-game');
    setTimeout(loadCatalog, 3000);
  } catch (err) {
    toast(err.message, 'err');
  }
}

// ── Track control ──────────────────────────────────────────────────────────
async function setTrack() {
  const env   = document.getElementById('env-select').value;
  const track = document.getElementById('track-select').value;
  const race  = document.getElementById('mode-select').value;
  if (!env || !track) { toast('Select an environment and track first', 'err'); return; }
  try {
    await apiFetch('POST', '/api/admin/track/set', { env, track, race });
    toast(`Track set: ${track}`);
  } catch (err) {
    toast(err.message, 'err');
  }
}

async function nextTrack() {
  try {
    await apiFetch('POST', '/api/admin/track/next');
    toast('Next track command sent');
  } catch (err) {
    toast(err.message, 'err');
  }
}

// ── Live WebSocket ─────────────────────────────────────────────────────────
function connectWs() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const ws = new WebSocket(`${proto}://${location.host}/ws/admin`);

  ws.onopen = () => {
    document.getElementById('ws-dot').className = 'dot connected';
    document.getElementById('ws-label').textContent = 'Live WS: connected';
  };
  ws.onclose = () => {
    document.getElementById('ws-dot').className = 'dot disconnected';
    document.getElementById('ws-label').textContent = 'Live WS: reconnecting…';
    setTimeout(connectWs, 3000);
  };
  ws.onmessage = ({ data }) => {
    try {
      const event = JSON.parse(data);
      if (event.event_type === 'track_catalog') {
        loadCatalog();
      } else if (event.event_type === 'playlist_state') {
        runnerState = event;
        renderRunnerBar();
        renderPlaylistList();
        updateEditorButtons();
        if (selectedPlaylistId) loadPlaylistTracks(selectedPlaylistId);
      } else if (event.event_type === 'state_snapshot') {
        if (event.online_players) {
          adminLobby.clear();
          for (const p of event.online_players) adminLobby.set(p.actor, p);
          renderAdminPlayers();
        }
      } else if (event.event_type === 'player_entered') {
        adminLobby.set(event.actor, { actor: event.actor, nick: event.nick });
        renderAdminPlayers();
      } else if (event.event_type === 'player_left') {
        adminLobby.delete(event.actor);
        renderAdminPlayers();
      } else if (event.event_type === 'player_list') {
        adminLobby.clear();
        for (const p of (event.players || [])) adminLobby.set(p.actor, { actor: p.actor, nick: p.nick });
        renderAdminPlayers();
      } else if (event.event_type === 'chat_message') {
        appendChatLog(event.nick || event.user_id || `Actor ${event.actor}`, event.message || '');
      } else if (event.event_type === 'kick_result') {
        if (event.success) {
          toast(`Kicked ${event.nick || 'actor ' + event.actor}`);
        } else {
          toast(`Kick failed: ${event.reason || 'unknown'}`, 'err');
        }
      }
    } catch {}
  };
}

// ── Players / Kick ─────────────────────────────────────────────────────────
const adminLobby = new Map(); // actor → { actor, nick }

function renderAdminPlayers() {
  const tbody = document.getElementById('players-tbody');
  if (adminLobby.size === 0) {
    tbody.innerHTML = '<tr><td colspan="3" style="padding:1.5rem;text-align:center;color:#444;font-size:0.85rem">No players in lobby.</td></tr>';
    return;
  }
  const rows = [...adminLobby.values()].sort((a, b) => a.actor - b.actor);
  tbody.innerHTML = '';
  for (const p of rows) {
    const tr = document.createElement('tr');
    const tdNick = document.createElement('td');
    tdNick.style.cssText = 'padding:0.6rem 1rem;font-size:0.9rem;border-bottom:1px solid #18181e';
    tdNick.textContent = p.nick;
    const tdActor = document.createElement('td');
    tdActor.style.cssText = 'padding:0.6rem 1rem;font-size:0.85rem;color:#666;border-bottom:1px solid #18181e';
    tdActor.textContent = p.actor;
    const tdAction = document.createElement('td');
    tdAction.style.cssText = 'padding:0.6rem 1rem;text-align:right;border-bottom:1px solid #18181e';
    const btn = document.createElement('button');
    btn.className = 'btn-danger';
    btn.style.cssText = 'padding:0.25rem 0.75rem;font-size:0.8rem';
    btn.textContent = 'Kick';
    btn.addEventListener('click', () => kickPlayer(p.actor, p.nick));
    tdAction.appendChild(btn);
    tr.append(tdNick, tdActor, tdAction);
    tbody.appendChild(tr);
  }
}

async function kickPlayer(actor, nick) {
  if (!confirm(`Kick ${nick} (actor ${actor}) from the lobby?`)) return;
  try {
    await apiFetch('POST', '/api/admin/players/kick', { actor });
    toast(`Kick command sent for ${nick}`);
  } catch (err) {
    toast(err.message, 'err');
  }
}

// ── Chat log (incoming) ────────────────────────────────────────────────────
const MAX_CHAT_LOG = 100;

function appendChatLog(nick, message) {
  const list = document.getElementById('admin-chat-log');
  const empty = document.getElementById('admin-chat-empty');
  if (empty) empty.remove();

  const ts = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const li = document.createElement('li');
  li.innerHTML = `<span class="chat-log-time">${esc(ts)}</span><span class="chat-log-nick">${esc(nick)}</span><span class="chat-log-msg">${esc(message)}</span>`;
  list.appendChild(li);

  while (list.children.length > MAX_CHAT_LOG) list.removeChild(list.firstChild);
  list.scrollTop = list.scrollHeight;
}

// ── Chat ───────────────────────────────────────────────────────────────────
async function sendChatNow() {
  const message = document.getElementById('chat-msg-input').value.trim();
  if (!message) return;
  try {
    await apiFetch('POST', '/api/admin/chat/send', { message });
    document.getElementById('chat-msg-input').value = '';
    toast(`Sent: ${message}`);
  } catch (err) {
    toast(err.message, 'err');
  }
}

async function loadTemplates() {
  try {
    const rows = await apiFetch('GET', '/api/admin/chat/templates');
    renderTemplates(rows);
  } catch {}
}

function renderTemplates(rows) {
  const tbody = document.getElementById('templates-body');
  if (!rows || rows.length === 0) {
    tbody.innerHTML = '<tr><td colspan="5" style="padding:1rem;text-align:center;color:#444;font-size:0.85rem">No templates yet.</td></tr>';
    return;
  }
  tbody.innerHTML = rows.map(r => `
    <tr data-id="${r.id}">
      <td style="padding:0.45rem 0.5rem"><span style="font-size:0.8rem;color:#f97316;background:#1a1008;padding:0.2rem 0.5rem;border-radius:3px">${esc(r.trigger)}</span></td>
      <td style="padding:0.45rem 0.5rem;color:#ccc">${esc(r.template)}</td>
      <td style="padding:0.45rem 0.5rem;color:${r.delay_ms < 0 ? '#f97316' : '#666'}" title="${r.delay_ms < 0 ? Math.abs(r.delay_ms/60000).toFixed(1)+' min before change' : ''}">${r.delay_ms || 0}</td>
      <td style="padding:0.45rem 0.5rem">
        <input type="checkbox" ${r.enabled ? 'checked' : ''} data-action="toggleTemplate" data-id="${r.id}" style="width:auto">
      </td>
      <td style="padding:0.45rem 0.5rem;text-align:right">
        <button class="btn-danger" style="padding:0.25rem 0.6rem;font-size:0.75rem" data-action="deleteTemplate" data-id="${r.id}">&#10005;</button>
      </td>
    </tr>
  `).join('');
}

async function addTemplate() {
  const trigger  = document.getElementById('tmpl-trigger').value;
  const template = document.getElementById('tmpl-template').value.trim();
  const delay_ms = parseInt(document.getElementById('tmpl-delay').value, 10) || 0;
  const enabled  = document.getElementById('tmpl-enabled').checked;
  if (!template) { toast('Message template is required', 'err'); return; }
  try {
    await apiFetch('POST', '/api/admin/chat/templates', { trigger, template, enabled, delay_ms });
    document.getElementById('tmpl-template').value = '';
    toast('Template added');
    loadTemplates();
  } catch (err) {
    toast(err.message, 'err');
  }
}

async function toggleTemplate(id, enabled) {
  try {
    const rows = await apiFetch('GET', '/api/admin/chat/templates');
    const row = rows.find(r => r.id === id);
    if (!row) return;
    await apiFetch('PUT', `/api/admin/chat/templates/${id}`, { ...row, enabled });
  } catch (err) {
    toast(err.message, 'err');
  }
}

async function deleteTemplate(id) {
  try {
    await apiFetch('DELETE', `/api/admin/chat/templates/${id}`);
    toast('Template deleted');
    loadTemplates();
  } catch (err) {
    toast(err.message, 'err');
  }
}

// ── Playlists ───────────────────────────────────────────────────────────────
let playlists = [];
let selectedPlaylistId = null;
let runnerState = null;
let countdownTimer = null;

async function loadPlaylists() {
  try {
    playlists = await apiFetch('GET', '/api/admin/playlists');
    renderPlaylistList();
  } catch {}
}

async function loadRunnerState() {
  try {
    runnerState = await apiFetch('GET', '/api/admin/playlist/state');
    renderRunnerBar();
  } catch {}
}

function renderPlaylistList() {
  const el = document.getElementById('playlist-list');
  if (!playlists.length) {
    el.innerHTML = '<div style="color:#444;font-size:0.8rem;padding:0.5rem 0">No playlists yet.</div>';
    return;
  }
  el.innerHTML = playlists.map(p => {
    const isRunning = runnerState?.running && runnerState.playlist_id === p.id;
    const isSelected = selectedPlaylistId === p.id;
    return `<div class="playlist-item ${isRunning ? 'running' : isSelected ? 'active' : ''}"
                 data-action="selectPlaylist" data-id="${p.id}">
      <span class="playlist-item-name">${esc(p.name)}</span>
      <span class="playlist-item-count">${p.track_count}</span>
      <button class="btn-danger mini-btn" style="padding:0.15rem 0.45rem"
        data-action="deletePlaylist" data-id="${p.id}">&#10005;</button>
    </div>`;
  }).join('');
}

function renderRunnerBar() {
  const bar = document.getElementById('runner-bar');
  const trackLabel = document.getElementById('runner-track-label');
  const nextLabel = document.getElementById('runner-next-label');

  if (!runnerState?.running) {
    bar.className = 'runner-bar stopped';
    bar.querySelector('.runner-label').textContent = 'Stopped';
    trackLabel.textContent = '';
    nextLabel.textContent = '';
    clearInterval(countdownTimer);
    return;
  }

  bar.className = 'runner-bar';
  bar.querySelector('.runner-label').textContent =
    `▶ ${esc(runnerState.playlist_name || 'Playlist')} (${runnerState.current_index + 1}/${runnerState.track_count})`;
  const t = runnerState.current_track;
  trackLabel.textContent = t ? `${t.env} / ${t.track}` : '';

  // Countdown
  clearInterval(countdownTimer);
  countdownTimer = setInterval(() => {
    if (!runnerState?.next_change_at) { nextLabel.textContent = ''; return; }
    const diff = new Date(runnerState.next_change_at) - Date.now();
    if (diff <= 0) { nextLabel.textContent = 'changing…'; return; }
    const m = Math.floor(diff / 60000);
    const s = Math.floor((diff % 60000) / 1000).toString().padStart(2, '0');
    nextLabel.textContent = `next in ${m}:${s}`;
  }, 1000);
}

async function selectPlaylist(id) {
  selectedPlaylistId = id;
  renderPlaylistList();
  const p = playlists.find(x => x.id === id);
  document.getElementById('editor-title').textContent = p?.name || '';
  document.getElementById('playlist-empty-hint').style.display = 'none';
  document.getElementById('playlist-editor').style.display = 'block';
  await loadPlaylistTracks(id);
  updateEditorButtons();
}

function updateEditorButtons() {
  const isRunning = runnerState?.running && runnerState.playlist_id === selectedPlaylistId;
  document.getElementById('start-btn').style.display = isRunning ? 'none' : '';
  document.getElementById('stop-btn').style.display  = isRunning ? '' : 'none';
  document.getElementById('skip-btn').style.display  = isRunning ? '' : 'none';
}

async function loadPlaylistTracks(id) {
  try {
    const tracks = await apiFetch('GET', `/api/admin/playlists/${id}/tracks`);
    renderTrackList(tracks);
  } catch {}
}

function renderTrackList(tracks) {
  const el = document.getElementById('track-list');
  if (!tracks.length) {
    el.innerHTML = '<div style="color:#444;font-size:0.8rem;padding:0.5rem 0">No tracks. Add some below.</div>';
    return;
  }
  const currentIdx = (runnerState?.running && runnerState.playlist_id === selectedPlaylistId)
    ? runnerState.current_index : -1;
  el.innerHTML = tracks.map((t, i) => {
    const isCurrent = i === currentIdx;
    return `<div class="track-row">
      <span class="track-pos ${isCurrent ? 'current' : ''}">${isCurrent ? '▶' : i + 1}</span>
      <span class="track-row-label">${esc(t.env)} / ${esc(t.track)}${t.race ? ` <span style="color:#666">${esc(t.race)}</span>` : ''}</span>
      <button class="btn-secondary mini-btn" data-action="moveTrack" data-id="${t.id}" data-direction="up">&#8593;</button>
      <button class="btn-secondary mini-btn" data-action="moveTrack" data-id="${t.id}" data-direction="down">&#8595;</button>
      <button class="btn-danger mini-btn" data-action="removeTrack" data-id="${t.id}">&#10005;</button>
    </div>`;
  }).join('');
}

async function createPlaylist() {
  const name = document.getElementById('new-playlist-name').value.trim();
  if (!name) return;
  try {
    const p = await apiFetch('POST', '/api/admin/playlists', { name });
    document.getElementById('new-playlist-name').value = '';
    await loadPlaylists();
    selectPlaylist(p.id);
    toast(`Playlist "${name}" created`);
  } catch (err) { toast(err.message, 'err'); }
}

async function deletePlaylist(id) {
  const p = playlists.find(x => x.id === id);
  if (!confirm(`Delete playlist "${p?.name}"?`)) return;
  try {
    await apiFetch('DELETE', `/api/admin/playlists/${id}`);
    if (selectedPlaylistId === id) {
      selectedPlaylistId = null;
      document.getElementById('playlist-editor').style.display = 'none';
      document.getElementById('playlist-empty-hint').style.display = '';
    }
    await loadPlaylists();
    await loadRunnerState();
    toast('Playlist deleted');
  } catch (err) { toast(err.message, 'err'); }
}

async function addTrackToPlaylist() {
  if (!selectedPlaylistId) return;
  const env   = document.getElementById('pl-env-select').value;
  const track = document.getElementById('pl-track-select').value;
  const race  = document.getElementById('pl-mode-select').value;
  if (!env || !track) { toast('Select an environment and track first', 'err'); return; }
  try {
    await apiFetch('POST', `/api/admin/playlists/${selectedPlaylistId}/tracks`, { env, track, race });
    await loadPlaylistTracks(selectedPlaylistId);
    await loadPlaylists(); // update track count
    renderPlaylistList();
    toast(`${track} added`);
  } catch (err) { toast(err.message, 'err'); }
}

async function removeTrack(tid) {
  try {
    await apiFetch('DELETE', `/api/admin/playlists/tracks/${tid}`);
    await loadPlaylistTracks(selectedPlaylistId);
    await loadPlaylists();
    renderPlaylistList();
  } catch (err) { toast(err.message, 'err'); }
}

async function moveTrack(tid, direction) {
  try {
    await apiFetch('POST', `/api/admin/playlists/tracks/${tid}/move`, { direction });
    await loadPlaylistTracks(selectedPlaylistId);
  } catch (err) { toast(err.message, 'err'); }
}

async function startPlaylist() {
  if (!selectedPlaylistId) return;
  const mins        = parseFloat(document.getElementById('interval-mins').value) || 15;
  const interval_ms = Math.round(mins * 60 * 1000);
  try {
    runnerState = await apiFetch('POST', `/api/admin/playlists/${selectedPlaylistId}/start`, { interval_ms });
    renderRunnerBar();
    renderPlaylistList();
    updateEditorButtons();
    await loadPlaylistTracks(selectedPlaylistId);
    toast('Playlist started');
  } catch (err) { toast(err.message, 'err'); }
}

async function stopPlaylist() {
  try {
    await apiFetch('POST', '/api/admin/playlist/stop');
    runnerState = { running: false };
    renderRunnerBar();
    renderPlaylistList();
    updateEditorButtons();
    toast('Playlist stopped');
  } catch (err) { toast(err.message, 'err'); }
}

async function skipTrack() {
  try {
    runnerState = await apiFetch('POST', '/api/admin/playlist/skip');
    renderRunnerBar();
    await loadPlaylistTracks(selectedPlaylistId);
  } catch (err) { toast(err.message, 'err'); }
}

function populatePlaylistCatalogSelects() {
  if (!catalog) return;
  const envSel = document.getElementById('pl-env-select');
  envSel.innerHTML = '<option value="">— Select environment —</option>' +
    (catalog.environments || []).map(e =>
      `<option value="${esc(e.internal_name || e.caption)}">${esc(e.display_name || e.caption)}</option>`
    ).join('');
  const modeSel = document.getElementById('pl-mode-select');
  modeSel.innerHTML = '<option value="">— Any mode —</option>' +
    (catalog.game_modes || [])
      .filter(m => m.name !== 'None')
      .map(m => `<option value="${esc(m.name)}">${esc(m.name)}</option>`)
    .join('');
}

function onPlEnvChange() {
  const envKey = document.getElementById('pl-env-select').value;
  const trackSel = document.getElementById('pl-track-select');
  trackSel.innerHTML = '<option value="">— Select track —</option>';
  if (!catalog || !envKey) return;
  const env = catalog.environments?.find(e => (e.internal_name || e.caption) === envKey);
  if (!env) return;
  trackSel.innerHTML += (env.tracks || []).map(t =>
    `<option value="${esc(t.name)}">${esc(t.name)}</option>`
  ).join('');
}

// ── Init ───────────────────────────────────────────────────────────────────
async function loadOnlinePlayers() {
  try {
    const data = await fetch('/api/status').then(r => r.json());
    adminLobby.clear();
    for (const p of (data.online_players || [])) adminLobby.set(p.actor, p);
    renderAdminPlayers();
  } catch {}
}

function init() {
  loadCatalog();
  loadTemplates();
  loadPlaylists();
  loadRunnerState();
  loadOnlinePlayers();
  pollStatus();
  connectWs();
}

// ── DOM Ready: bind events & bootstrap ─────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {

  // ── Static element bindings ──────────────────────────────────────────────
  document.getElementById('token-input').addEventListener('keydown', e => {
    if (e.key === 'Enter') saveToken();
  });

  document.getElementById('btn-save-token').addEventListener('click', saveToken);
  document.getElementById('btn-clear-token').addEventListener('click', clearToken);
  document.getElementById('btn-set-track').addEventListener('click', setTrack);
  document.getElementById('btn-next-track').addEventListener('click', nextTrack);
  document.getElementById('btn-refresh-catalog').addEventListener('click', refreshCatalog);
  document.getElementById('btn-send-chat').addEventListener('click', sendChatNow);
  document.getElementById('btn-add-template').addEventListener('click', addTemplate);
  document.getElementById('btn-create-playlist').addEventListener('click', createPlaylist);
  document.getElementById('start-btn').addEventListener('click', startPlaylist);
  document.getElementById('stop-btn').addEventListener('click', stopPlaylist);
  document.getElementById('skip-btn').addEventListener('click', skipTrack);
  document.getElementById('btn-add-track-to-playlist').addEventListener('click', addTrackToPlaylist);

  document.getElementById('env-select').addEventListener('change', onEnvChange);
  document.getElementById('pl-env-select').addEventListener('change', onPlEnvChange);

  document.getElementById('chat-msg-input').addEventListener('keydown', e => {
    if (e.key === 'Enter') sendChatNow();
  });

  // ── Event delegation for dynamic content ─────────────────────────────────

  // Templates table
  document.getElementById('templates-body').addEventListener('click', e => {
    const target = e.target.closest('[data-action]');
    if (!target) return;
    const action = target.dataset.action;
    const id = parseInt(target.dataset.id, 10);
    if (action === 'deleteTemplate') deleteTemplate(id);
  });

  document.getElementById('templates-body').addEventListener('change', e => {
    const target = e.target.closest('[data-action]');
    if (!target) return;
    const action = target.dataset.action;
    const id = parseInt(target.dataset.id, 10);
    if (action === 'toggleTemplate') toggleTemplate(id, target.checked);
  });

  // Playlist list
  document.getElementById('playlist-list').addEventListener('click', e => {
    const target = e.target.closest('[data-action]');
    if (!target) return;
    const action = target.dataset.action;
    const id = parseInt(target.dataset.id, 10);
    if (action === 'deletePlaylist') {
      e.stopPropagation();
      deletePlaylist(id);
    } else if (action === 'selectPlaylist') {
      selectPlaylist(id);
    }
  });

  // Track list
  document.getElementById('track-list').addEventListener('click', e => {
    const target = e.target.closest('[data-action]');
    if (!target) return;
    const action = target.dataset.action;
    const id = parseInt(target.dataset.id, 10);
    if (action === 'moveTrack') {
      moveTrack(id, target.dataset.direction);
    } else if (action === 'removeTrack') {
      removeTrack(id);
    }
  });

  // ── Bootstrap ────────────────────────────────────────────────────────────
  // Check if existing cookie is valid by making a lightweight API call
  fetch('/api/admin/status', { method: 'GET' })
    .then(r => {
      if (r.ok) {
        document.getElementById('token-overlay').style.display = 'none';
        init();
      }
    })
    .catch(() => {});
});
