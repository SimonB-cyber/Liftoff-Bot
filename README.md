# Liftoff Competition

Competition management platform for Liftoff FPV Simulator. Remotely control your lobby — change tracks, kick players, run playlists with scheduled rotations, and let pilots vote to skip. Includes a BepInEx game plugin, Node.js backend, live spectator view, and admin dashboard. Turn casual lobbies into league nights.

---

## Overview

Liftoff Competition transforms a standard Liftoff multiplayer session into a structured, managed event. It connects a BepInEx game plugin running inside Liftoff to a server backend, which powers two web interfaces: a **public live view** for spectators and an **admin dashboard** for organisers.

### How It Works

```
┌──────────────┐   WebSocket    ┌──────────────┐   WebSocket    ┌──────────────┐
│  Liftoff Game │◄─────────────►│    Server     │◄─────────────►│  Admin Panel  │
│  (BepInEx     │   /ws/plugin  │  (Node.js +   │   /ws/admin   │  (Browser)    │
│   Plugin)     │               │   Express)    │               │               │
└──────────────┘               │               │               └──────────────┘
                                │   SQLite DB   │
                                │               │   WebSocket    ┌──────────────┐
                                │               │◄─────────────►│  Live View    │
                                └──────────────┘   /ws/live     │  (Browser)    │
                                                                └──────────────┘
```

1. The **BepInEx plugin** captures Photon multiplayer events (races, laps, players, chat) inside Liftoff and sends them to the server over WebSocket.
2. The **server** ingests events into SQLite, manages state, and broadcasts updates to connected web clients.
3. The **admin dashboard** lets organisers control the lobby: change tracks, run playlists, send chat, and manage players.
4. The **live view** gives spectators a real-time window into the current race, track, and player activity.
5. Commands flow back from the server to the plugin to execute lobby changes (track switches, chat messages, kicks) inside the game.

---

## Features

### Lobby Control
- Change tracks remotely from the admin dashboard
- Kick players when moderation is needed
- Browse and search the available track catalog
- Full command/response protocol between server and game

### Playlists
- Create named playlists with ordered track lists
- Start, stop, pause, and skip through playlists
- Scheduled track rotation with configurable timing
- Ideal for league nights, qualifying sessions, tournaments, and curated race events

### Vote to Skip
- Players type `/skip` in game chat to start a skip vote (3-minute timer)
- Additional players type `/skip` to add their vote
- Configurable vote threshold — when enough players vote, the track advances automatically
- `/info` command shows available player commands

### Live Spectator View
- Real-time race visualization in the browser
- Current track and environment display
- Active player roster
- Live lap activity feed
- Connection status indicator
- Designed for embedding in streams, club pages, or community sites

### Admin Dashboard
- Browser-based control panel for event organisers
- Player management with kick controls
- Track catalog browsing and selection
- Playlist creation, management, and execution
- Live chat monitoring
- Manual and automated chat messaging
- Race monitoring and session overview

### Chat System
- View in-game chat live in the admin panel
- Send messages directly into the game from the browser
- Automated message templates triggered by events:
  - `track_change` — announce the next track
  - `race_start` — notify players a race has begun
  - `race_end` — congratulate the winner
- Template variables: `{env}`, `{track}`, `{race}`, `{mins}`, `{winner}`, `{time}`
- Schedule warning messages before track rotation

### Race Data
- Automatic race and lap recording to SQLite
- Per-pilot tracking via Steam ID and pilot GUID
- Session history and leaderboard support
- Structured JSONL event logs from the plugin

---

## Tech Stack

| Component | Technology |
|-----------|------------|
| Game Plugin | C# / .NET 4.7.2 / BepInEx / Photon (PUN3) |
| Server | Node.js / Express / WebSocket (ws) |
| Database | SQLite (WAL mode) |
| Validation | AJV (JSON Schema) |
| Frontend | Vanilla JS, HTML, CSS |
| Infrastructure | Docker, Docker Compose, Nginx, Let's Encrypt |
| Tests | Vitest |

---

## Project Structure

```
Liftoff/
├── contracts/                          # Shared event schemas (JSON Schema)
│   ├── common.json
│   ├── lap_recorded.json
│   ├── player_entered.json
│   ├── player_left.json
│   ├── player_list.json
│   ├── race_end.json
│   ├── race_reset.json
│   └── set_track.json
│
├── Pluggins/
│   └── LiftoffPhotonEventLogger/       # BepInEx game plugin
│       ├── LiftoffPhotonEventLogger.cs
│       ├── Features/
│       │   ├── Chat/                   # In-game chat capture
│       │   ├── Competition/            # WebSocket client & config
│       │   ├── Identity/               # Player identity tracking
│       │   ├── Logging/                # File logging & serialization
│       │   └── MultiplayerTrackControl/# Track/race/environment control
│       └── docs/
│
├── Server/
│   ├── src/
│   │   ├── index.js                    # Entry point
│   │   ├── pluginSocket.js             # Plugin WebSocket server
│   │   ├── liveSocket.js               # Live & admin WebSocket servers
│   │   ├── broadcast.js                # Event broadcast dispatcher
│   │   ├── playlistRunner.js           # Playlist scheduling & execution
│   │   ├── state.js                    # In-memory state
│   │   ├── skipVote.js                 # Vote-to-skip logic
│   │   ├── contracts.js                # Event validation
│   │   ├── routes/
│   │   │   ├── admin.js                # Admin API endpoints
│   │   │   └── public.js               # Public API endpoints
│   │   └── db/                         # SQLite layer
│   │
│   ├── public/
│   │   ├── index.html                  # Public live view
│   │   ├── admin.html                  # Admin dashboard
│   │   └── js/                         # Frontend modules
│   │
│   ├── nginx/                          # Reverse proxy config
│   ├── docker-compose.yml
│   ├── Dockerfile
│   └── .env.example
│
└── Logs/                               # Plugin log output
```

---

## Getting Started

### Prerequisites

- **Server:** Node.js 23+ (or Docker)
- **Plugin:** .NET Framework 4.7.2 SDK, Liftoff with [BepInEx](https://github.com/BepInEx/BepInEx) installed

### Server Setup

1. **Clone the repo and configure environment:**
   ```bash
   cd Server
   cp .env.example .env
   ```

2. **Edit `.env`** with your own secrets:
   ```env
   PORT=3000
   PLUGIN_API_KEY=your-plugin-key
   ADMIN_TOKEN=your-admin-token
   DB_PATH=./competition.db
   ```

3. **Run with Docker (recommended):**
   ```bash
   docker compose up -d
   ```

   Or **run locally:**
   ```bash
   npm install
   npm start
   ```

4. **Access the interfaces:**
   - Live view: `http://localhost:3000/`
   - Admin panel: `http://localhost:3000/admin.html`

### Plugin Setup

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx) into your Liftoff game directory.

2. Build the plugin:
   ```bash
   cd Pluggins/LiftoffPhotonEventLogger
   dotnet build
   ```
   > By default, the project looks for Liftoff at the standard Steam install path. Set the `LIFTOFF_DIR` environment variable to override.

3. Copy the built DLL into `BepInEx/plugins/` in your Liftoff install folder.

4. Configure the plugin's connection settings (server URL and API key) to match your server's `.env` values.

5. Launch Liftoff and join a multiplayer session — the plugin connects to the server automatically.

### Production Deployment

For production with HTTPS:

1. Set up your domain's DNS to point to your server.
2. Run `init-certs.sh` to provision Let's Encrypt certificates.
3. Run `init-htpasswd.sh` to set up Basic Auth for the admin panel.
4. Start with Docker Compose — Nginx handles SSL termination and proxying.

---

## Running Tests

```bash
cd Server
npm test
```

---

## Who It's For

- **Race organisers** — reduce the friction of running structured events
- **League admins** — automate track rotation and manage sessions remotely
- **Community hosts** — give your club nights a professional feel
- **Streamers** — embed the live view for your audience
- **Anyone** who wants Liftoff multiplayer to feel like an organised event, not an ad-hoc lobby

---

## License

This project is currently private. All rights reserved.
