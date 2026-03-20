require('dotenv').config();

const http = require('http');
const path = require('path');
const express = require('express');
const { initDatabase } = require('./database');
const { createPluginSocketServer } = require('./pluginSocket');
const { createLiveSocketServer } = require('./liveSocket');
const broadcast = require('./broadcast');
const playlistRunner = require('./playlistRunner');
const adminRoutes = require('./routes/admin');
const publicRoutes = require('./routes/public');

// ── Database ────────────────────────────────────────────────────────────────
initDatabase();

// ── HTTP + Express ──────────────────────────────────────────────────────────
const app = express();
app.use(express.json());
app.use(express.static(path.join(__dirname, '..', 'public')));

app.use('/api/admin', adminRoutes);
app.use('/api', publicRoutes);

// JSON error handler — must be after all routes
app.use((err, req, res, next) => {
  res.status(err.status || 500).json({ error: err.message });
});

const server = http.createServer(app);

// ── WebSocket servers ────────────────────────────────────────────────────────
const { broadcastPublic, broadcastAdmin } = createLiveSocketServer(server);

// Initialise the centralised broadcast module so any module can broadcast
// without needing a direct reference to the live-socket layer.
broadcast.init(broadcastPublic, broadcastAdmin);

createPluginSocketServer(server);

// Initialise playlist runner with combined broadcast function
playlistRunner.init(broadcast.broadcastAll);

// ── Start ────────────────────────────────────────────────────────────────────
const PORT = process.env.PORT || 3000;
server.listen(PORT, () => {
  console.log(`[server] Liftoff Competition Server running on http://localhost:${PORT}`);
  console.log(`[server] Plugin WebSocket : ws://localhost:${PORT}/ws/plugin`);
  console.log(`[server] Live WebSocket   : ws://localhost:${PORT}/ws/live`);
  console.log(`[server] Admin WebSocket  : ws://localhost:${PORT}/ws/admin`);
});
