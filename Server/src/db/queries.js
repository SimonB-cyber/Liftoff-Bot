const { getDb } = require('./connection');

function getRaces({ limit = 50, offset = 0 } = {}) {
  return getDb().prepare(`
    SELECT * FROM races
    ORDER BY started_at DESC
    LIMIT ? OFFSET ?
  `).all(limit, offset);
}

function getRaceById(id) {
  const db = getDb();
  const race = db.prepare('SELECT * FROM races WHERE id = ?').get(id);
  if (!race) return null;
  race.laps = db.prepare('SELECT * FROM laps WHERE race_id = ? ORDER BY lap_number').all(id);
  return race;
}

function getBestLaps({ limit = 100 } = {}) {
  return getDb().prepare(`
    SELECT
      COALESCE(steam_id, pilot_guid, nick) AS pilot_key,
      nick,
      pilot_guid,
      steam_id,
      MIN(lap_ms)  AS best_lap_ms,
      COUNT(*)     AS total_laps
    FROM laps
    GROUP BY pilot_key
    ORDER BY best_lap_ms ASC
    LIMIT ?
  `).all(limit);
}

function getLatestRaceWithLaps(since) {
  const db = getDb();
  const race = db.prepare(`
    SELECT r.* FROM races r
    LEFT JOIN laps l ON l.race_id = r.id
    GROUP BY r.id
    ORDER BY COALESCE(MAX(l.recorded_at), r.started_at) DESC, r.started_at DESC
    LIMIT 1
  `).get();
  if (!race) return null;
  if (since) {
    race.laps = db.prepare(`
      SELECT * FROM laps WHERE race_id = ? AND recorded_at >= ? ORDER BY recorded_at ASC
    `).all(race.id, since);
  } else {
    race.laps = db.prepare(`
      SELECT * FROM laps WHERE race_id = ? ORDER BY recorded_at ASC
    `).all(race.id);
  }
  return race;
}

function getBestLapsByTrack(env, track, { limit = 100 } = {}) {
  return getDb().prepare(`
    SELECT
      COALESCE(l.steam_id, l.pilot_guid, l.nick) AS pilot_key,
      l.nick, l.pilot_guid, l.steam_id,
      MIN(l.lap_ms)  AS best_lap_ms,
      COUNT(*)       AS total_laps
    FROM laps l
    JOIN races r ON l.race_id = r.id
    WHERE r.env = ? AND r.track = ?
    GROUP BY pilot_key
    ORDER BY best_lap_ms ASC
    LIMIT ?
  `).all(env, track, limit);
}

function getPlayerStats({ limit = 200 } = {}) {
  return getDb().prepare(`
    SELECT
      COALESCE(steam_id, pilot_guid, nick) AS pilot_key,
      nick, pilot_guid, steam_id,
      MIN(lap_ms)            AS best_lap_ms,
      COUNT(*)               AS total_laps,
      COUNT(DISTINCT race_id) AS races_entered
    FROM laps
    GROUP BY pilot_key
    ORDER BY total_laps DESC
    LIMIT ?
  `).all(limit);
}

function getLatestCatalog() {
  const row = getDb().prepare(`
    SELECT catalog_json FROM track_catalog ORDER BY id DESC LIMIT 1
  `).get();
  return row ? JSON.parse(row.catalog_json) : null;
}

function getPilotActivity() {
  return getDb().prepare(`
    SELECT
      COUNT(DISTINCT CASE WHEN datetime(recorded_at) >= datetime('now', '-1 day')
        THEN COALESCE(steam_id, pilot_guid, nick) END) AS last_24h,
      COUNT(DISTINCT CASE WHEN datetime(recorded_at) >= datetime('now', '-7 days')
        THEN COALESCE(steam_id, pilot_guid, nick) END) AS last_7d,
      COUNT(DISTINCT CASE WHEN datetime(recorded_at) >= datetime('now', '-1 month')
        THEN COALESCE(steam_id, pilot_guid, nick) END) AS last_30d
    FROM laps
  `).get();
}

module.exports = {
  getRaces,
  getRaceById,
  getBestLaps,
  getLatestRaceWithLaps,
  getBestLapsByTrack,
  getPlayerStats,
  getLatestCatalog,
  getPilotActivity,
};
