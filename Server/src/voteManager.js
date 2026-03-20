/**
 * Vote Manager module.
 *
 * Manages the in-game /skip, /extend, and /agree chat commands that let players
 * collectively vote to skip the current playlist track or extend its duration.
 */

const state = require('./state');
const playlistRunner = require('./playlistRunner');

const VOTE_TIMEOUT_MS = 180_000; // votes expire after 3 minutes
const EXTEND_MS = 5 * 60 * 1000; // 5 minutes extension

const activeVote = {
  type: null, // 'skip' | 'extend' | null
  voters: new Set(), // voter keys (user_id or nick) who have voted
  timer: null,
};

let _sendCommand = null;

function init(sendCommandFn) {
  _sendCommand = sendCommandFn;
}

function cancelVote() {
  activeVote.type = null;
  activeVote.voters.clear();
  if (activeVote.timer) {
    clearTimeout(activeVote.timer);
    activeVote.timer = null;
  }
}

function getVoteInfo() {
  const total = state.getOnlinePlayerCount();
  const realPlayers = Math.max(total - 1, 0); // exclude the bot
  const needed = realPlayers <= 1 ? 1 : Math.max(Math.floor(realPlayers / 2), 2);
  return { realPlayers, needed };
}

function handleSkipVoteCommand(voterId) {
  if (!playlistRunner.getState().running) {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>SKIP</color> <color=#FFFF00>No playlist is running — nothing to skip.</color>' });
    return;
  }

  if (activeVote.type === 'extend') {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>SKIP</color> <color=#FFFF00>An extend vote is currently active. Type /agree to extend.</color>' });
    return;
  }

  if (activeVote.type === 'skip') {
    const { needed } = getVoteInfo();
    const have = activeVote.voters.size;
    _sendCommand({ cmd: 'send_chat', message: `<color=#00BFFF>SKIP VOTE</color> <color=#FFFF00>In progress:</color> <color=#00FF00>${have}/${needed}</color> <color=#FFFF00>— Type /agree</color>` });
    return;
  }

  // Start a new skip vote
  activeVote.type = 'skip';
  activeVote.voters.clear();
  activeVote.voters.add(voterId);

  const { realPlayers, needed } = getVoteInfo();
  _sendCommand({ cmd: 'send_chat', message: `<color=#00FF00>SKIP VOTE</color> <color=#FFFF00>Need</color> <color=#00BFFF>${needed}/${realPlayers}</color> <color=#FFFF00>— Type /agree (3m)</color>` });

  checkVoteThreshold();

  activeVote.timer = setTimeout(() => {
    if (activeVote.type === 'skip') {
      cancelVote();
      _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>SKIP VOTE</color> <color=#FFFF00>Skip vote expired.</color>' });
    }
  }, VOTE_TIMEOUT_MS);
}

function handleExtendVoteCommand(voterId) {
  if (!playlistRunner.getState().running) {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>EXTEND</color> <color=#FFFF00>No playlist is running — nothing to extend.</color>' });
    return;
  }

  if (activeVote.type === 'skip') {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>EXTEND</color> <color=#FFFF00>A skip vote is currently active. Type /agree to skip.</color>' });
    return;
  }

  if (activeVote.type === 'extend') {
    const { needed } = getVoteInfo();
    const have = activeVote.voters.size;
    _sendCommand({ cmd: 'send_chat', message: `<color=#00BFFF>EXTEND VOTE</color> <color=#FFFF00>In progress:</color> <color=#00FF00>${have}/${needed}</color> <color=#FFFF00>— Type /agree</color>` });
    return;
  }

  // Start a new extend vote
  activeVote.type = 'extend';
  activeVote.voters.clear();
  activeVote.voters.add(voterId);

  const { realPlayers, needed } = getVoteInfo();
  _sendCommand({ cmd: 'send_chat', message: `<color=#00FF00>EXTEND VOTE</color> <color=#FFFF00>Need</color> <color=#00BFFF>${needed}/${realPlayers}</color> <color=#FFFF00>— Type /agree (3m)</color>` });

  checkVoteThreshold();

  activeVote.timer = setTimeout(() => {
    if (activeVote.type === 'extend') {
      cancelVote();
      _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>EXTEND VOTE</color> <color=#FFFF00>Extend vote expired.</color>' });
    }
  }, VOTE_TIMEOUT_MS);
}

function handleAgreeCommand(voterId) {
  if (!activeVote.type) {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>VOTE</color> <color=#FFFF00>No vote active. Type /skip or /extend to start.</color>' });
    return;
  }

  if (activeVote.voters.has(voterId)) {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FFFF00>You have already voted.</color>' });
    return;
  }

  activeVote.voters.add(voterId);
  const { needed } = getVoteInfo();
  const have = activeVote.voters.size;
  const label = activeVote.type === 'skip' ? 'SKIP VOTE' : 'EXTEND VOTE';
  _sendCommand({ cmd: 'send_chat', message: `<color=#00BFFF>${label}</color> <color=#00FF00>${have}/${needed}</color>` });

  checkVoteThreshold();
}

function checkVoteThreshold() {
  const { realPlayers, needed } = getVoteInfo();
  if (realPlayers === 0) return;
  if (activeVote.voters.size >= needed) {
    const vType = activeVote.type;
    cancelVote();

    if (vType === 'skip') {
      _sendCommand({ cmd: 'send_chat', message: '<color=#00FF00>VOTE PASSED</color> <color=#FFFF00>Skipping to next track...</color>' });
      playlistRunner.skipToNext();
    } else if (vType === 'extend') {
      _sendCommand({ cmd: 'send_chat', message: '<color=#00FF00>VOTE PASSED</color> <color=#FFFF00>Time extended by 5 minutes.</color>' });
      playlistRunner.extendCurrentTrack(EXTEND_MS);
    }
  }
}

function isActive() {
  return activeVote.type !== null;
}

module.exports = {
  init,
  isActive,
  cancelVote,
  handleSkipVoteCommand,
  handleExtendVoteCommand,
  handleAgreeCommand,
};
