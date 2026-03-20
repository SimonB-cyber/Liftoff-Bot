/**
 * Centralized broadcast module.
 * Holds references to the public and admin broadcast functions created by
 * liveSocket.js and exposes them to any module that needs to push messages
 * to connected browser clients.
 *
 * Call init() once during startup (from index.js) with the real broadcast
 * functions. After that, any module can require('./broadcast') and call
 * broadcastPublic / broadcastAdmin / broadcastAll without needing a
 * reference to app.locals or the live-socket layer.
 */

let _broadcastPublic = null;
let _broadcastAdmin = null;

function init(pub, admin) {
  _broadcastPublic = pub;
  _broadcastAdmin = admin;
}

function broadcastPublic(msg) {
  _broadcastPublic?.(msg);
}

function broadcastAdmin(msg) {
  _broadcastAdmin?.(msg);
}

function broadcastAll(msg) {
  broadcastPublic(msg);
  broadcastAdmin(msg);
}

module.exports = { init, broadcastPublic, broadcastAdmin, broadcastAll };
