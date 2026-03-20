import { describe, it, expect, beforeEach } from 'vitest';
import { createRequire } from 'module';
const require = createRequire(import.meta.url);
const state = require('../src/state');

describe('state', () => {
  beforeEach(() => {
    state.clearOnlinePlayers();
  });

  describe('currentTrack', () => {
    it('starts with expected shape', () => {
      const track = state.getCurrentTrack();
      expect(track).toHaveProperty('env');
      expect(track).toHaveProperty('track');
    });

    it('setCurrentTrack updates values', () => {
      state.setCurrentTrack({ env: 'Desert', track: 'Oasis', race: 'Sprint' });
      const track = state.getCurrentTrack();
      expect(track.env).toBe('Desert');
      expect(track.track).toBe('Oasis');
      expect(track.race).toBe('Sprint');
    });

    it('setCurrentTrack sets trackSince timestamp', () => {
      state.setCurrentTrack({ env: 'Forest', track: 'Creek' });
      const since = state.getCurrentTrackSince();
      expect(since).toBeTruthy();
      expect(new Date(since).toISOString()).toBe(since);
    });
  });

  describe('onlinePlayers', () => {
    it('starts empty', () => {
      expect(state.getOnlinePlayers()).toEqual([]);
      expect(state.getOnlinePlayerCount()).toBe(0);
    });

    it('setOnlinePlayer adds a player', () => {
      state.setOnlinePlayer(1, { actor: 1, nick: 'Alice', user_id: 'u1' });
      expect(state.getOnlinePlayerCount()).toBe(1);
      expect(state.getOnlinePlayers()[0].nick).toBe('Alice');
    });

    it('removeOnlinePlayer removes a player', () => {
      state.setOnlinePlayer(1, { actor: 1, nick: 'Alice' });
      state.setOnlinePlayer(2, { actor: 2, nick: 'Bob' });
      state.removeOnlinePlayer(1);
      expect(state.getOnlinePlayerCount()).toBe(1);
      expect(state.getOnlinePlayers()[0].nick).toBe('Bob');
    });

    it('clearOnlinePlayers empties the map', () => {
      state.setOnlinePlayer(1, { actor: 1, nick: 'Alice' });
      state.setOnlinePlayer(2, { actor: 2, nick: 'Bob' });
      state.clearOnlinePlayers();
      expect(state.getOnlinePlayerCount()).toBe(0);
    });
  });

  describe('chatCooldown', () => {
    it('commands are allowed by default', () => {
      expect(state.areChatCommandsAllowed()).toBe(true);
    });

    it('applyChatCooldown blocks commands temporarily', () => {
      state.applyChatCooldown();
      expect(state.areChatCommandsAllowed()).toBe(false);
    });
  });
});
