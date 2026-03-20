const { Router } = require('express');
const rateLimit = require('express-rate-limit');
const { sendCommand, sendCommandAwait, getPluginSocket, setCurrentTrack, fireTemplates } = require('../pluginSocket');
const broadcast = require('../broadcast');
const db = require('../database');
const playlist = require('../playlistRunner');

const router = Router();

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

function extractToken(req) {
  // 1. Bearer token in Authorization header
  const authHeader = req.headers['authorization'] || '';
  if (authHeader.startsWith('Bearer ')) return authHeader.slice(7);
  // 2. httpOnly cookie
  const cookies = parseCookies(req.headers.cookie);
  return cookies[COOKIE_NAME] || '';
}

// ── Login endpoint (before auth middleware) ─────────────────────────────────

router.post('/login', (req, res) => {
  const { token } = req.body || {};
  if (!ADMIN_TOKEN || token !== ADMIN_TOKEN) {
    return res.status(401).json({ error: 'Invalid token' });
  }
  res.cookie(COOKIE_NAME, token, {
    httpOnly: true,
    sameSite: 'strict',
    path: '/',
    maxAge: 24 * 60 * 60 * 1000, // 24 hours
  });
  res.json({ ok: true });
});

router.post('/logout', (req, res) => {
  res.clearCookie(COOKIE_NAME, { path: '/' });
  res.json({ ok: true });
});

// ── Auth middleware ─────────────────────────────────────────────────────────

router.use((req, res, next) => {
  const token = extractToken(req);
  if (!ADMIN_TOKEN || token !== ADMIN_TOKEN) {
    return res.status(401).json({ error: 'Unauthorized' });
  }
  next();
});

// ── Rate limiting ───────────────────────────────────────────────────────────

const generalLimiter = rateLimit({
  windowMs: 60_000,
  max: 60,
  standardHeaders: true,
  legacyHeaders: false,
  message: { error: 'Too many requests — try again in a minute' },
});

const strictLimiter = rateLimit({
  windowMs: 60_000,
  max: 10,
  standardHeaders: true,
  legacyHeaders: false,
  message: { error: 'Too many requests — try again in a minute' },
});

router.use(generalLimiter);

// ── Helper ──────────────────────────────────────────────────────────────────

function pluginStatus(res, sent) {
  if (!sent) {
    return res.status(503).json({ error: 'Plugin not connected' });
  }
  res.json({ ok: true });
}

// ── Endpoints ───────────────────────────────────────────────────────────────

/**
 * POST /api/admin/track/next
 * Advance to the next track in the plugin's current sequence.
 */
router.post('/track/next', (req, res) => {
  pluginStatus(res, sendCommand({ cmd: 'next_track' }));
});

/**
 * POST /api/admin/track/set
 * Jump to a specific track immediately.
 * Body: { env, track, race, workshop_id }
 */
router.post('/track/set', strictLimiter, (req, res) => {
  const { env = '', track = '', race = '', workshop_id = '' } = req.body;
  if (!env && !track && !workshop_id) {
    return res.status(400).json({ error: 'Provide at least env+track or workshop_id' });
  }
  setCurrentTrack({ env, track, race });
  const sent = sendCommand({ cmd: 'set_track', env, track, race, workshop_id });
  if (sent) {
    broadcast.broadcastAll({ event_type: 'track_changed', env, track, race });
    fireTemplates('track_change', { env, track, race });
  }
  pluginStatus(res, sent);
});

/**
 * PUT /api/admin/playlist
 * Replace the full track sequence and optionally jump to the first entry.
 * Body: { sequence: "Env|Track|Race;Env2|Track2|Race2", apply_immediately: true }
 */
router.put('/playlist', (req, res) => {
  const { sequence, apply_immediately = false } = req.body;
  if (!sequence || typeof sequence !== 'string') {
    return res.status(400).json({ error: 'sequence is required (pipe/semicolon format)' });
  }
  pluginStatus(res, sendCommand({ cmd: 'update_playlist', sequence, apply_immediately }));
});

/**
 * POST /api/admin/catalog/refresh
 * Asks the plugin to read the current popup dropdowns and emit a track_catalog event.
 * The popup must be open in-game for this to succeed.
 */
router.post('/catalog/refresh', (req, res) => {
  pluginStatus(res, sendCommand({ cmd: 'request_catalog' }));
});

/**
 * POST /api/admin/players/kick
 * Kick a player from the room by actor number. Plugin must be master client.
 * Body: { actor: number }
 */
router.post('/players/kick', strictLimiter, async (req, res) => {
  const actor = parseInt(req.body.actor);
  if (!actor || isNaN(actor)) return res.status(400).json({ error: 'actor (number) is required' });
  try {
    const ack = await sendCommandAwait({ cmd: 'kick_player', actor });
    if (ack.status === 'timeout') return res.json({ ok: true, ack: 'timeout' });
    if (ack.status === 'error') return res.status(502).json({ error: ack.message || 'Plugin error' });
    res.json({ ok: true, ack: ack.status });
  } catch (err) {
    res.status(503).json({ error: err.message });
  }
});

/**
 * GET /api/admin/status
 * Returns whether the plugin is currently connected.
 */
router.get('/status', (req, res) => {
  const socket = getPluginSocket();
  res.json({ plugin_connected: socket !== null && socket.readyState === 1 });
});

// ── Chat ────────────────────────────────────────────────────────────────────

/**
 * POST /api/admin/chat/send
 * Send an immediate chat message into the game.
 * Body: { message }
 */
router.post('/chat/send', (req, res) => {
  const { message = '' } = req.body;
  const trimmed = message.trim();
  if (!trimmed) return res.status(400).json({ error: 'message is required' });
  if (trimmed.length > 500) return res.status(400).json({ error: 'message too long (max 500 chars)' });
  pluginStatus(res, sendCommand({ cmd: 'send_chat', message: trimmed }));
});

/**
 * GET /api/admin/chat/templates
 */
router.get('/chat/templates', (req, res) => {
  res.json(db.getChatTemplates());
});

/**
 * POST /api/admin/chat/templates
 * Body: { trigger, template, enabled, delay_ms }
 */
router.post('/chat/templates', (req, res) => {
  const { trigger, template, enabled = true, delay_ms = 0 } = req.body;
  if (!trigger || !template) return res.status(400).json({ error: 'trigger and template are required' });
  const row = db.createChatTemplate({ trigger, template, enabled, delay_ms });
  res.status(201).json(row);
});

/**
 * PUT /api/admin/chat/templates/:id
 * Body: { trigger, template, enabled, delay_ms }
 */
router.put('/chat/templates/:id', (req, res) => {
  const { trigger, template, enabled = true, delay_ms = 0 } = req.body;
  if (!trigger || !template) return res.status(400).json({ error: 'trigger and template are required' });
  const row = db.updateChatTemplate(Number(req.params.id), { trigger, template, enabled, delay_ms });
  if (!row) return res.status(404).json({ error: 'Template not found' });
  res.json(row);
});

/**
 * DELETE /api/admin/chat/templates/:id
 */
router.delete('/chat/templates/:id', (req, res) => {
  db.deleteChatTemplate(Number(req.params.id));
  res.json({ ok: true });
});

// ── Playlists ────────────────────────────────────────────────────────────────

/** GET /api/admin/playlists */
router.get('/playlists', (req, res) => {
  res.json(db.getPlaylists());
});

/** POST /api/admin/playlists  Body: { name } */
router.post('/playlists', (req, res) => {
  const { name = '' } = req.body;
  if (!name.trim()) return res.status(400).json({ error: 'name is required' });
  try {
    res.status(201).json(db.createPlaylist(name.trim()));
  } catch (err) {
    res.status(409).json({ error: err.message });
  }
});

/** PUT /api/admin/playlists/:id  Body: { name } */
router.put('/playlists/:id', (req, res) => {
  const { name = '' } = req.body;
  if (!name.trim()) return res.status(400).json({ error: 'name is required' });
  const row = db.renamePlaylist(Number(req.params.id), name.trim());
  if (!row) return res.status(404).json({ error: 'Playlist not found' });
  res.json(row);
});

/** DELETE /api/admin/playlists/:id */
router.delete('/playlists/:id', (req, res) => {
  const id = Number(req.params.id);
  if (playlist.getState().playlist_id === id && playlist.getState().running) {
    playlist.stopPlaylist();
  }
  db.deletePlaylist(id);
  res.json({ ok: true });
});

/** GET /api/admin/playlists/:id/tracks */
router.get('/playlists/:id/tracks', (req, res) => {
  res.json(db.getPlaylistTracks(Number(req.params.id)));
});

/** POST /api/admin/playlists/:id/tracks  Body: { env, track, race, workshop_id } */
router.post('/playlists/:id/tracks', (req, res) => {
  const { env = '', track = '', race = '', workshop_id = '' } = req.body;
  if (!env && !track && !workshop_id) return res.status(400).json({ error: 'Provide env+track or workshop_id' });
  const row = db.addPlaylistTrack(Number(req.params.id), { env, track, race, workshop_id });
  res.status(201).json(row);
});

/** DELETE /api/admin/playlists/tracks/:tid */
router.delete('/playlists/tracks/:tid', (req, res) => {
  db.removePlaylistTrack(Number(req.params.tid));
  res.json({ ok: true });
});

/** POST /api/admin/playlists/tracks/:tid/move  Body: { direction: 'up'|'down' } */
router.post('/playlists/tracks/:tid/move', (req, res) => {
  const { direction } = req.body;
  if (!['up', 'down'].includes(direction)) return res.status(400).json({ error: 'direction must be up or down' });
  db.movePlaylistTrack(Number(req.params.tid), direction);
  res.json({ ok: true });
});

/** POST /api/admin/playlists/:id/start  Body: { interval_ms } */
router.post('/playlists/:id/start', strictLimiter, (req, res) => {
  const intervalMs = Math.max(5000, Number(req.body.interval_ms) || 15 * 60 * 1000);
  try {
    playlist.startPlaylist(Number(req.params.id), intervalMs);
    res.json(playlist.getState());
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
});

/** POST /api/admin/playlist/stop */
router.post('/playlist/stop', (req, res) => {
  playlist.stopPlaylist();
  res.json({ ok: true });
});

/** POST /api/admin/playlist/skip */
router.post('/playlist/skip', (req, res) => {
  playlist.skipToNext();
  res.json(playlist.getState());
});

/** GET /api/admin/playlist/state */
router.get('/playlist/state', (req, res) => {
  res.json(playlist.getState());
});

module.exports = router;
