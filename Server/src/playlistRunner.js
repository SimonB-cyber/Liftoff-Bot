/**
 * Playlist Runner
 *
 * Manages a server-side timer that automatically advances through a playlist
 * of tracks, sending set_track commands to the plugin at a configured interval.
 *
 * State is held in memory; playlists themselves are persisted in the DB.
 */

const db = require('./database');
const { sendCommand, setCurrentTrack, fireTemplates } = require('./pluginSocket');
const broadcast = require('./broadcast');

// ── State ────────────────────────────────────────────────────────────────────

const state = {
  running: false,
  playlistId: null,
  playlistName: null,
  currentIndex: 0,
  intervalMs: 15 * 60 * 1000,
  tracks: [],        // loaded snapshot of playlist_tracks at start
  nextChangeAt: null, // Date — when the next auto-advance will fire
};

let _timer = null;
let _preTimers = [];  // timers for negative-delay track_change templates

// ── Public API ───────────────────────────────────────────────────────────────

/**
 * @deprecated kept for backwards compatibility — broadcast.init() is now
 * called directly in index.js.  This is a no-op.
 */
function init(_broadcastFn) {
  // No longer needed — the broadcast module is used directly.
  // Retained so that existing callers (index.js) do not break.
}

function getState() {
  return {
    running: state.running,
    playlist_id: state.playlistId,
    playlist_name: state.playlistName,
    current_index: state.currentIndex,
    interval_ms: state.intervalMs,
    current_track: state.tracks[state.currentIndex] || null,
    track_count: state.tracks.length,
    next_change_at: state.nextChangeAt ? state.nextChangeAt.toISOString() : null,
  };
}

function startPlaylist(playlistId, intervalMs) {
  const playlist = db.getPlaylistById(playlistId);
  if (!playlist) throw new Error(`Playlist ${playlistId} not found`);

  const tracks = db.getPlaylistTracks(playlistId);
  if (tracks.length === 0) throw new Error('Playlist is empty — add tracks first');

  stopPlaylist();

  state.running = true;
  state.playlistId = playlistId;
  state.playlistName = playlist.name;
  state.currentIndex = 0;
  state.intervalMs = intervalMs;
  state.tracks = tracks;

  _applyCurrentTrack();
  _scheduleNext();

  console.log(`[playlist] Started "${playlist.name}" (${tracks.length} tracks, interval=${intervalMs}ms)`);
  _broadcastState();
}

function _clearPreTimers() {
  for (const t of _preTimers) clearTimeout(t);
  _preTimers = [];
}

function stopPlaylist() {
  if (_timer) { clearTimeout(_timer); _timer = null; }
  _clearPreTimers();
  const wasRunning = state.running;
  state.running = false;
  state.nextChangeAt = null;
  if (wasRunning) {
    console.log('[playlist] Stopped');
    _broadcastState();
  }
}

function skipToNext() {
  if (!state.running || state.tracks.length === 0) return;
  if (_timer) { clearTimeout(_timer); _timer = null; }
  _clearPreTimers();
  state.currentIndex = (state.currentIndex + 1) % state.tracks.length;
  _applyCurrentTrack();
  _scheduleNext();
  _broadcastState();
}

function skipToIndex(index) {
  if (!state.running || state.tracks.length === 0) return;
  if (index < 0 || index >= state.tracks.length) return;
  if (_timer) { clearTimeout(_timer); _timer = null; }
  _clearPreTimers();
  state.currentIndex = index;
  _applyCurrentTrack();
  _scheduleNext();
  _broadcastState();
}

// ── Internals ────────────────────────────────────────────────────────────────

function _scheduleNext() {
  state.nextChangeAt = new Date(Date.now() + state.intervalMs);
  _clearPreTimers();

  // Pre-change messages: track_change templates with negative delay_ms.
  // delay_ms = -120000 means "fire 2 minutes before the track changes".
  if (state.tracks.length > 0) {
    let preTemplates = [];
    try { preTemplates = db.getChatTemplatesByTrigger('track_change').filter(t => t.delay_ms < 0); } catch (_) {}

    if (preTemplates.length > 0) {
      const nextIndex = (state.currentIndex + 1) % state.tracks.length;
      const nextTrack = state.tracks[nextIndex];
      for (const tmpl of preTemplates) {
        const fireAt = state.intervalMs + tmpl.delay_ms; // e.g. 900000 + (-120000) = 780000ms
        if (fireAt <= 0) continue; // would fire in the past — skip
        let message = tmpl.template;
        const vars = { env: nextTrack.env, track: nextTrack.track, race: nextTrack.race,
                       mins: Math.round(Math.abs(tmpl.delay_ms) / 60000) };
        for (const [k, v] of Object.entries(vars)) message = message.replaceAll(`{${k}}`, v ?? '');
        message = message.trim();
        if (!message) continue;
        _preTimers.push(setTimeout(() => sendCommand({ cmd: 'send_chat', message }), fireAt));
      }
    }
  }

  _timer = setTimeout(() => {
    state.currentIndex = (state.currentIndex + 1) % state.tracks.length;
    _applyCurrentTrack();
    _scheduleNext();
    _broadcastState();
  }, state.intervalMs);
}

function _applyCurrentTrack() {
  const t = state.tracks[state.currentIndex];
  if (!t) return;

  sendCommand({ cmd: 'set_track', env: t.env, track: t.track, race: t.race, workshop_id: t.workshop_id || '' });
  setCurrentTrack({ env: t.env, track: t.track, race: t.race });

  broadcast.broadcastAll({ event_type: 'track_changed', env: t.env, track: t.track, race: t.race });

  fireTemplates('track_change', { env: t.env, track: t.track, race: t.race });

  console.log(`[playlist] Track ${state.currentIndex + 1}/${state.tracks.length}: ${t.env} / ${t.track}`);
}

function _broadcastState() {
  broadcast.broadcastAll({ event_type: 'playlist_state', ...getState() });
}

module.exports = { init, getState, startPlaylist, stopPlaylist, skipToNext, skipToIndex };
