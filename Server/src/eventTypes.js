/**
 * Canonical list of event types exchanged between plugin and server.
 * This is the contract. The plugin serializes these as event_type values,
 * the server switches on them.
 *
 * Any new event type MUST be added here.
 */
module.exports = {
  // Plugin → Server (ingested from JSONL stream)
  SESSION_STARTED: 'session_started',
  RACE_RESET: 'race_reset',
  LAP_RECORDED: 'lap_recorded',
  RACE_END: 'race_end',
  TRACK_CATALOG: 'track_catalog',
  PLAYER_ENTERED: 'player_entered',
  PLAYER_LEFT: 'player_left',
  PLAYER_LIST: 'player_list',
  CHAT_MESSAGE: 'chat_message',
  KICK_RESULT: 'kick_result',
  CHECKPOINT: 'checkpoint',
  PILOT_COMPLETE: 'pilot_complete',
  PILOT_RESET: 'pilot_reset',
  COMMAND_ACK: 'command_ack',

  // Server → Browser
  TRACK_CHANGED: 'track_changed',
  PLAYLIST_STATE: 'playlist_state',
  STATE_SNAPSHOT: 'state_snapshot',
  KEEPALIVE: 'keepalive',
};
