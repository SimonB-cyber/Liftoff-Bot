const { getDb } = require('./connection');

function getPlaylists() {
  const db = getDb();
  const rows = db.prepare('SELECT * FROM playlists ORDER BY id').all();
  for (const p of rows) {
    p.track_count = db.prepare('SELECT COUNT(*) AS n FROM playlist_tracks WHERE playlist_id = ?').get(p.id).n;
  }
  return rows;
}

function getPlaylistById(id) {
  return getDb().prepare('SELECT * FROM playlists WHERE id = ?').get(id) || null;
}

function createPlaylist(name) {
  const db = getDb();
  const result = db.prepare(
    "INSERT INTO playlists (name, created_at) VALUES (?, datetime('now'))"
  ).run(name);
  return db.prepare('SELECT * FROM playlists WHERE id = ?').get(result.lastInsertRowid);
}

function renamePlaylist(id, name) {
  const db = getDb();
  db.prepare('UPDATE playlists SET name = ? WHERE id = ?').run(name, id);
  return db.prepare('SELECT * FROM playlists WHERE id = ?').get(id);
}

function deletePlaylist(id) {
  const db = getDb();
  db.prepare('DELETE FROM playlist_tracks WHERE playlist_id = ?').run(id);
  db.prepare('DELETE FROM playlists WHERE id = ?').run(id);
}

function getPlaylistTracks(playlistId) {
  return getDb().prepare(
    'SELECT * FROM playlist_tracks WHERE playlist_id = ? ORDER BY position'
  ).all(playlistId);
}

function addPlaylistTrack(playlistId, { env = '', track = '', race = '', workshop_id = '' }) {
  const db = getDb();
  const maxPos = db.prepare(
    'SELECT COALESCE(MAX(position), -1) AS m FROM playlist_tracks WHERE playlist_id = ?'
  ).get(playlistId).m;
  const result = db.prepare(
    'INSERT INTO playlist_tracks (playlist_id, position, env, track, race, workshop_id) VALUES (?, ?, ?, ?, ?, ?)'
  ).run(playlistId, maxPos + 1, env, track, race, workshop_id);
  return db.prepare('SELECT * FROM playlist_tracks WHERE id = ?').get(result.lastInsertRowid);
}

function removePlaylistTrack(trackId) {
  const db = getDb();
  const row = db.prepare('SELECT * FROM playlist_tracks WHERE id = ?').get(trackId);
  if (!row) return;
  db.prepare('DELETE FROM playlist_tracks WHERE id = ?').run(trackId);
  const tracks = db.prepare(
    'SELECT id FROM playlist_tracks WHERE playlist_id = ? ORDER BY position'
  ).all(row.playlist_id);
  const update = db.prepare('UPDATE playlist_tracks SET position = ? WHERE id = ?');
  tracks.forEach((t, i) => update.run(i, t.id));
}

function movePlaylistTrack(trackId, direction) {
  const db = getDb();
  const row = db.prepare('SELECT * FROM playlist_tracks WHERE id = ?').get(trackId);
  if (!row) return;
  const tracks = db.prepare(
    'SELECT * FROM playlist_tracks WHERE playlist_id = ? ORDER BY position'
  ).all(row.playlist_id);
  const idx = tracks.findIndex(t => t.id === trackId);
  const swapIdx = direction === 'up' ? idx - 1 : idx + 1;
  if (swapIdx < 0 || swapIdx >= tracks.length) return;
  const a = tracks[idx], b = tracks[swapIdx];
  db.prepare('UPDATE playlist_tracks SET position = ? WHERE id = ?').run(b.position, a.id);
  db.prepare('UPDATE playlist_tracks SET position = ? WHERE id = ?').run(a.position, b.id);
}

module.exports = {
  getPlaylists,
  getPlaylistById,
  createPlaylist,
  renamePlaylist,
  deletePlaylist,
  getPlaylistTracks,
  addPlaylistTrack,
  removePlaylistTrack,
  movePlaylistTrack,
};
