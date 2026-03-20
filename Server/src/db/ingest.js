const { getDb } = require('./connection');

function handleSessionStarted(event) {
  const db = getDb();
  const stmt = db.prepare(`
    INSERT OR IGNORE INTO sessions (id, started_at, plugin_ver)
    VALUES (@id, @started_at, @plugin_ver)
  `);
  stmt.run({
    id: event.session_id,
    started_at: event.timestamp_utc,
    plugin_ver: event.version || null,
  });
}

function handleRaceReset(event, currentTrack = {}) {
  const db = getDb();
  const stmt = db.prepare(`
    INSERT OR IGNORE INTO races (id, session_id, ordinal, started_at, env, track)
    VALUES (@id, @session_id, @ordinal, @started_at, @env, @track)
  `);
  stmt.run({
    id: event.race_id,
    session_id: event.session_id,
    ordinal: event.race_ordinal,
    started_at: event.timestamp_utc,
    env: currentTrack.env || null,
    track: currentTrack.track || null,
  });
}

function handleLapRecorded(event, currentTrack = {}) {
  const db = getDb();
  ensureRaceExists(event);
  if (currentTrack.env && currentTrack.track) {
    db.prepare(`
      UPDATE races SET env = ?, track = ? WHERE id = ? AND env IS NULL
    `).run(currentTrack.env, currentTrack.track, event.race_id);
  }
  const stmt = db.prepare(`
    INSERT INTO laps (race_id, session_id, actor, nick, pilot_guid, steam_id, lap_number, lap_ms, recorded_at)
    VALUES (@race_id, @session_id, @actor, @nick, @pilot_guid, @steam_id, @lap_number, @lap_ms, @recorded_at)
  `);
  stmt.run({
    race_id: event.race_id,
    session_id: event.session_id,
    actor: event.actor,
    nick: event.nick || null,
    pilot_guid: event.pilot_guid || null,
    steam_id: event.steam_id || null,
    lap_number: event.lap_number,
    lap_ms: event.lap_ms,
    recorded_at: event.timestamp_utc,
  });
}

function handleRaceEnd(event) {
  const db = getDb();
  ensureRaceExists(event);
  const stmt = db.prepare(`
    UPDATE races
    SET ended_at        = @ended_at,
        winner_actor    = @winner_actor,
        winner_nick     = @winner_nick,
        winner_total_ms = @winner_total_ms,
        participants    = @participants,
        completed       = @completed
    WHERE id = @id
  `);
  stmt.run({
    id: event.race_id,
    ended_at: event.timestamp_utc,
    winner_actor: event.winner_actor || null,
    winner_nick: event.winner_nick || null,
    winner_total_ms: event.winner_total_ms || null,
    participants: event.participants || 0,
    completed: event.completed || 0,
  });
}

function handleTrackCatalog(event) {
  const db = getDb();
  const stmt = db.prepare(`
    INSERT INTO track_catalog (session_id, recorded_at, catalog_json)
    VALUES (@session_id, @recorded_at, @catalog_json)
  `);
  stmt.run({
    session_id: event.session_id,
    recorded_at: event.timestamp_utc,
    catalog_json: JSON.stringify(event),
  });
}

function ensureRaceExists(event) {
  const db = getDb();
  db.prepare(`
    INSERT OR IGNORE INTO sessions (id, started_at, plugin_ver)
    VALUES (@id, @started_at, @plugin_ver)
  `).run({
    id: event.session_id,
    started_at: event.timestamp_utc,
    plugin_ver: event.version || null,
  });

  db.prepare(`
    INSERT OR IGNORE INTO races (id, session_id, ordinal, started_at)
    VALUES (@id, @session_id, @ordinal, @started_at)
  `).run({
    id: event.race_id,
    session_id: event.session_id,
    ordinal: event.race_ordinal || 0,
    started_at: event.timestamp_utc,
  });
}

module.exports = {
  handleSessionStarted,
  handleRaceReset,
  handleLapRecorded,
  handleRaceEnd,
  handleTrackCatalog,
};
