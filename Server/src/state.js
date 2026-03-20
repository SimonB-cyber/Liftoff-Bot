/**
 * Centralized in-memory state store.
 * Single source of truth for runtime state shared across modules.
 */

// Last known track info, set when admin sends set_track or playlist advances
const currentTrack = { env: null, track: null, race: null };

// ISO timestamp of the last track change — used to filter laps in snapshots
// so that only laps from the current track are shown in "Race in Progress".
let currentTrackSince = null;

// actor → { actor, nick, user_id } for players currently in the lobby
const onlinePlayers = new Map();

// ── Chat command cooldown ────────────────────────────────────────────────────
// Epoch ms after which chat commands are accepted again.
// Set to now + TRACK_CHANGE_CHAT_COOLDOWN_MS on every track change to swallow
// the burst of replayed chat_message events that Liftoff emits when the scene
// reloads.  AppendRaceEvent stamps replayed messages with the *current* time,
// so timestamp-based filtering is not reliable — a cooldown window is the only
// option.
let _chatCommandsAllowedFrom = 0;
const TRACK_CHANGE_CHAT_COOLDOWN_MS = 30_000;

function getCurrentTrack() {
  return currentTrack;
}

function setCurrentTrack(info) {
  Object.assign(currentTrack, info);
  currentTrackSince = new Date().toISOString();
}

function getCurrentTrackSince() {
  return currentTrackSince;
}

function getOnlinePlayers() {
  return [...onlinePlayers.values()];
}

function setOnlinePlayer(actor, data) {
  onlinePlayers.set(actor, data);
}

function removeOnlinePlayer(actor) {
  onlinePlayers.delete(actor);
}

function clearOnlinePlayers() {
  onlinePlayers.clear();
}

function getOnlinePlayerCount() {
  return onlinePlayers.size;
}

// ── Chat cooldown helpers ────────────────────────────────────────────────────

function areChatCommandsAllowed() {
  return Date.now() >= _chatCommandsAllowedFrom;
}

function applyChatCooldown() {
  _chatCommandsAllowedFrom = Date.now() + TRACK_CHANGE_CHAT_COOLDOWN_MS;
}

module.exports = {
  getCurrentTrack,
  setCurrentTrack,
  getCurrentTrackSince,
  getOnlinePlayers,
  setOnlinePlayer,
  removeOnlinePlayer,
  clearOnlinePlayers,
  getOnlinePlayerCount,
  areChatCommandsAllowed,
  applyChatCooldown,
};
