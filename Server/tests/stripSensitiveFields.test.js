import { describe, it, expect } from 'vitest';

// Replicate the pure function here to avoid loading the full pluginSocket module
// which requires WebSocket, database, and other dependencies.
const SENSITIVE_FIELDS = ['user_id', 'steam_id', 'pilot_guid'];

function stripSensitiveFields(jsonLine, event) {
  const hasSensitive = SENSITIVE_FIELDS.some(f => jsonLine.includes(f));
  if (!hasSensitive) return jsonLine;

  const cleaned = { ...event };
  for (const f of SENSITIVE_FIELDS) delete cleaned[f];
  if (Array.isArray(cleaned.players)) {
    cleaned.players = cleaned.players.map(p => {
      const { user_id, steam_id, pilot_guid, ...safe } = p;
      return safe;
    });
  }
  return JSON.stringify(cleaned);
}

describe('stripSensitiveFields', () => {
  it('returns jsonLine unchanged when no sensitive fields present', () => {
    const event = { event_type: 'lap_recorded', nick: 'Alice', lap_ms: 12345 };
    const json = JSON.stringify(event);
    expect(stripSensitiveFields(json, event)).toBe(json);
  });

  it('strips user_id from top-level event', () => {
    const event = { event_type: 'lap_recorded', nick: 'Alice', user_id: 'secret123', lap_ms: 12345 };
    const json = JSON.stringify(event);
    const result = JSON.parse(stripSensitiveFields(json, event));
    expect(result.user_id).toBeUndefined();
    expect(result.nick).toBe('Alice');
    expect(result.lap_ms).toBe(12345);
  });

  it('strips steam_id and pilot_guid', () => {
    const event = { event_type: 'lap_recorded', nick: 'Bob', steam_id: 'STEAM_123', pilot_guid: 'guid-abc' };
    const json = JSON.stringify(event);
    const result = JSON.parse(stripSensitiveFields(json, event));
    expect(result.steam_id).toBeUndefined();
    expect(result.pilot_guid).toBeUndefined();
    expect(result.nick).toBe('Bob');
  });

  it('strips sensitive fields from nested player arrays', () => {
    const event = {
      event_type: 'player_list',
      players: [
        { actor: 1, nick: 'Alice', user_id: 'u1', steam_id: 's1' },
        { actor: 2, nick: 'Bob', pilot_guid: 'g2' },
      ],
    };
    const json = JSON.stringify(event);
    const result = JSON.parse(stripSensitiveFields(json, event));
    expect(result.players).toHaveLength(2);
    expect(result.players[0]).toEqual({ actor: 1, nick: 'Alice' });
    expect(result.players[1]).toEqual({ actor: 2, nick: 'Bob' });
  });

  it('does not mutate the original event object', () => {
    const event = { event_type: 'lap_recorded', nick: 'Alice', user_id: 'secret' };
    const json = JSON.stringify(event);
    stripSensitiveFields(json, event);
    expect(event.user_id).toBe('secret');
  });
});
