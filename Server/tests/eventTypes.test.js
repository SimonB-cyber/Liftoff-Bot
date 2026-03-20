import { describe, it, expect } from 'vitest';
import { createRequire } from 'module';
const require = createRequire(import.meta.url);
const E = require('../src/eventTypes');

describe('eventTypes', () => {
  it('exports only non-empty string values', () => {
    for (const [key, value] of Object.entries(E)) {
      expect(typeof value).toBe('string');
      expect(value.length).toBeGreaterThan(0);
    }
  });

  it('has no duplicate values', () => {
    const values = Object.values(E);
    const unique = new Set(values);
    expect(unique.size).toBe(values.length);
  });

  it('includes all expected plugin→server events', () => {
    expect(E.SESSION_STARTED).toBe('session_started');
    expect(E.RACE_RESET).toBe('race_reset');
    expect(E.LAP_RECORDED).toBe('lap_recorded');
    expect(E.RACE_END).toBe('race_end');
    expect(E.PLAYER_ENTERED).toBe('player_entered');
    expect(E.PLAYER_LEFT).toBe('player_left');
    expect(E.PLAYER_LIST).toBe('player_list');
    expect(E.CHAT_MESSAGE).toBe('chat_message');
  });

  it('includes all expected server→browser events', () => {
    expect(E.TRACK_CHANGED).toBe('track_changed');
    expect(E.PLAYLIST_STATE).toBe('playlist_state');
    expect(E.STATE_SNAPSHOT).toBe('state_snapshot');
    expect(E.KEEPALIVE).toBe('keepalive');
  });
});
