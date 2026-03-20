const { Router } = require('express');
const db = require('../database');
const { getPluginSocket } = require('../pluginSocket');
const { getCurrentTrack, getOnlinePlayers, getCurrentTrackSince } = require('../state');

const router = Router();

/**
 * GET /api/races
 * Recent races. Query: ?limit=50&offset=0
 */
router.get('/races', (req, res) => {
  const limit = Math.min(parseInt(req.query.limit) || 50, 200);
  const offset = parseInt(req.query.offset) || 0;
  res.json(db.getRaces({ limit, offset }));
});

/**
 * GET /api/races/:id
 * Single race with all laps.
 */
router.get('/races/:id', (req, res) => {
  const race = db.getRaceById(req.params.id);
  if (!race) return res.status(404).json({ error: 'Race not found' });
  res.json(race);
});

/**
 * GET /api/leaderboard
 * Best lap per pilot across all races.
 */
router.get('/leaderboard', (req, res) => {
  res.json(db.getBestLaps());
});

/**
 * GET /api/leaderboard/track?env=X&track=Y
 * Best lap per pilot for a specific env+track combination.
 */
router.get('/leaderboard/track', (req, res) => {
  const { env, track } = req.query;
  if (!env || !track) return res.status(400).json({ error: 'env and track query params required' });
  res.json(db.getBestLapsByTrack(env, track));
});

/**
 * GET /api/players
 * All-time stats per pilot: best lap, total laps, races entered.
 */
router.get('/players', (req, res) => {
  res.json(db.getPlayerStats());
});

/**
 * GET /api/status
 * Plugin connection status + latest race summary.
 */
router.get('/status', (req, res) => {
  const socket = getPluginSocket();
  const race = db.getLatestRaceWithLaps();
  res.json({
    plugin_connected: socket !== null && socket.readyState === 1,
    current_track: getCurrentTrack(),
    track_since: getCurrentTrackSince(),
    latest_race: race,
    online_players: getOnlinePlayers(),
  });
});

/**
 * GET /api/pilot-activity
 * Unique pilot counts for the last 24h, 7 days, and 30 days.
 */
router.get('/pilot-activity', (req, res) => {
  res.json(db.getPilotActivity());
});

/**
 * GET /api/catalog
 * Most recently received track catalog from the plugin.
 */
router.get('/catalog', (req, res) => {
  const catalog = db.getLatestCatalog();
  if (!catalog) return res.status(404).json({ error: 'No catalog received yet' });
  res.json(catalog);
});

module.exports = router;
