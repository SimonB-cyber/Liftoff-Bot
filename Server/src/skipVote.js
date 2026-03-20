/**
 * Skip-vote module.
 *
 * Manages the in-game /skip and /agree chat commands that let players
 * collectively vote to skip the current playlist track.
 *
 * Dependencies are injected via init() so this module stays decoupled
 * from the WebSocket transport layer.
 */

const state = require('./state');

const SKIP_VOTE_TIMEOUT_MS = 180_000; // votes expire after 3 minutes

const skipVote = {
  active: false,
  voters: new Set(), // voter keys (user_id or nick) who have voted
  timer: null,
};

let _sendCommand = null;

/**
 * Initialise the module with a sendCommand function (from pluginSocket).
 */
function init(sendCommandFn) {
  _sendCommand = sendCommandFn;
}

function cancelSkipVote() {
  skipVote.active = false;
  skipVote.voters.clear();
  if (skipVote.timer) {
    clearTimeout(skipVote.timer);
    skipVote.timer = null;
  }
}

function getSkipVoteInfo() {
  const total = state.getOnlinePlayerCount();
  const realPlayers = Math.max(total - 1, 0); // exclude the bot
  const needed = realPlayers <= 1 ? 1 : Math.max(Math.floor(realPlayers / 2), 2);
  return { realPlayers, needed };
}

function handleSkipVoteCommand(voterId) {
  const { skipToNext, getState: getPlaylistState } = require('./playlistRunner');
  if (!getPlaylistState().running) {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>SKIP</color> <color=#FFFF00>No playlist is running — nothing to skip.</color>' });
    return;
  }

  if (skipVote.active) {
    const { needed } = getSkipVoteInfo();
    const have = skipVote.voters.size;
    _sendCommand({ cmd: 'send_chat', message: `<color=#00BFFF>SKIP VOTE</color> <color=#FFFF00>In progress:</color> <color=#00FF00>${have}/${needed}</color> <color=#FFFF00>— Type /agree</color>` });
    return;
  }

  // Start a new vote
  skipVote.active = true;
  skipVote.voters.clear();
  skipVote.voters.add(voterId); // the initiator counts as a vote

  const { realPlayers, needed } = getSkipVoteInfo();
  _sendCommand({ cmd: 'send_chat', message: `<color=#00FF00>SKIP VOTE</color> <color=#FFFF00>Need</color> <color=#00BFFF>${needed}/${realPlayers}</color> <color=#FFFF00>— Type /agree</color> <color=#FF0000>(3m)</color>` });

  // Check immediately in case threshold already met
  checkSkipVoteThreshold();

  skipVote.timer = setTimeout(() => {
    if (skipVote.active) {
      cancelSkipVote();
      _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>SKIP VOTE</color> <color=#FFFF00>Skip vote expired.</color>' });
    }
  }, SKIP_VOTE_TIMEOUT_MS);
}

function handleAgreeCommand(voterId) {
  if (!skipVote.active) {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FF0000>SKIP</color> <color=#FFFF00>No vote active. Type /skip to start.</color>' });
    return;
  }

  if (skipVote.voters.has(voterId)) {
    _sendCommand({ cmd: 'send_chat', message: '<color=#FFFF00>You have already voted.</color>' });
    return;
  }

  skipVote.voters.add(voterId);
  const { needed } = getSkipVoteInfo();
  const have = skipVote.voters.size;
  _sendCommand({ cmd: 'send_chat', message: `<color=#00BFFF>SKIP VOTE</color> <color=#00FF00>${have}/${needed}</color>` });

  checkSkipVoteThreshold();
}

function checkSkipVoteThreshold() {
  const { realPlayers, needed } = getSkipVoteInfo();
  if (realPlayers === 0) return;
  if (skipVote.voters.size >= needed) {
    cancelSkipVote();
    _sendCommand({ cmd: 'send_chat', message: '<color=#00FF00>VOTE PASSED</color> <color=#FFFF00>Skipping to next track...</color>' });
    require('./playlistRunner').skipToNext();
  }
}

/**
 * Whether a skip vote is currently active.
 */
function isActive() {
  return skipVote.active;
}

module.exports = {
  init,
  isActive,
  cancelSkipVote,
  handleSkipVoteCommand,
  handleAgreeCommand,
};
