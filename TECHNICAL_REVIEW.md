# Technical Review

## Executive Summary
The codebase has a workable first-generation shape, but it is carrying a lot of responsibility in a few large modules, and the most important problems are now boundary problems rather than syntax or style problems. The biggest risks are: public/admin data leaking through the shared live WebSocket, an admin-page XSS path tied to token storage, a very large Unity `Plugin` class that mixes transport/domain/logging/game integration, and backend state that is spread across global mutable modules. If this keeps growing without a structural split, change velocity will fall quickly and regressions around multiplayer/race handling will get harder to diagnose.

## Strengths
- The Unity side already shows some useful feature-level separation under [Features](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features), especially [CompetitionClient.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features/Competition/CompetitionClient.cs#L23) and [MultiplayerTrackControlService.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features/MultiplayerTrackControl/MultiplayerTrackControlService.cs#L13).
- The reverse-engineering-heavy track-control work is at least documented in [multiplayer-track-control.md](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/docs/multiplayer-track-control.md), which is a real maintainability win.
- The server is already split at a coarse level into routes, sockets, playlist runner, and persistence, starting from [index.js](/c:/Projects/Liftoff/Server/src/index.js#L7).
- The ops side has a sensible baseline with SQLite WAL and foreign keys in [database.js](/c:/Projects/Liftoff/Server/src/database.js#L13), WebSocket keepalives in [liveSocket.js](/c:/Projects/Liftoff/Server/src/liveSocket.js#L24), and nginx/TLS/docker wiring in [nginx.conf](/c:/Projects/Liftoff/Server/nginx/nginx.conf#L49) and [docker-compose.yml](/c:/Projects/Liftoff/Server/docker-compose.yml).

## Key Issues Found
- Critical: the public `/ws/live` channel is also carrying admin-relevant events and operational state. [liveSocket.js](/c:/Projects/Liftoff/Server/src/liveSocket.js#L9) broadcasts one shared stream, [pluginSocket.js](/c:/Projects/Liftoff/Server/src/pluginSocket.js#L252) forwards raw plugin events into it, and both [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L601) and [index.html](/c:/Projects/Liftoff/Server/public/index.html#L390) connect to the same endpoint. That means any client that opens `/ws/live` can observe `chat_message`, `kick_result`, `online_players`, and full snapshots, even if the public page does not render them.
- Critical: the admin UI has an injection path in the kick button. [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L668) writes `onclick="kickPlayer(..., '${esc(p.nick)}')"` while [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L654) does not escape apostrophes. Combined with token storage in [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L456), a crafted nickname can break the inline JavaScript and potentially steal the admin bearer token.
- High: the Unity plugin entrypoint is doing too much. [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L26) owns plugin bootstrap, Photon callbacks, race reconstruction, file logging, JSON serialization, server event emission, chat capture, and kick execution. The race logic alone spans [UpdateRaceStateFromProperties](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L896), [MergeGmsLapSeries](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L961), [RecordLapTime](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L1016), and [StartNewRace](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L1109). This is now beyond "large but okay"; it is difficult to test or reason about safely.
- High: the plugin/server contract is implicit and stringly typed. The plugin hand-builds JSON in [AppendRaceEvent](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L1261) and [SerializeJsonObject](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L1280), while commands are parsed by the narrow [SimpleJsonParser.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features/Competition/SimpleJsonParser.cs). This is already leaking into behavior: `session_started` is emitted more than once from [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L179) and [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L199), and admin APIs treat "socket open" as "command succeeded" via [admin.js](/c:/Projects/Liftoff/Server/src/routes/admin.js#L23).
- High: `CompetitionClient` has reliability hazards. It keeps an unbounded outbox in [CompetitionClient.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features/Competition/CompetitionClient.cs#L30), polls it every 50 ms in [CompetitionClient.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features/Competition/CompetitionClient.cs#L151), cancels without awaiting shutdown in [CompetitionClient.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features/Competition/CompetitionClient.cs#L83), and may run Unity-affecting work off the main thread in [CompetitionClient.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features/Competition/CompetitionClient.cs#L191). If the sync context is missing, the fallback is unsafe.
- High: server state ownership is too implicit. [pluginSocket.js](/c:/Projects/Liftoff/Server/src/pluginSocket.js#L40) owns current track, player presence, skip votes, recent chat, and broadcast side effects; [playlistRunner.js](/c:/Projects/Liftoff/Server/src/playlistRunner.js) mutates overlapping state; [index.js](/c:/Projects/Liftoff/Server/src/index.js#L32) wires everything together through globals and `app.locals`. This is brittle and hard to unit test.
- Medium: identity handling is inconsistent and can merge users incorrectly. The public UI keys live pilots by nickname in [index.html](/c:/Projects/Liftoff/Server/public/index.html#L350), server leaderboards group by `COALESCE(steam_id, pilot_guid, nick)` in [database.js](/c:/Projects/Liftoff/Server/src/database.js#L230), and `onlinePlayers` only keeps `{ actor, nick }` in [pluginSocket.js](/c:/Projects/Liftoff/Server/src/pluginSocket.js#L277) even though the plugin emits `user_id`. Two pilots with the same visible nick can collapse together.
- Medium: the backend persistence layer is doing schema creation, ad-hoc migrations, ingest handlers, query endpoints, chat templates, and playlists in one file, all synchronously, inside [database.js](/c:/Projects/Liftoff/Server/src/database.js#L13). This matters less today for raw performance than for change risk and testability.
- Medium: the frontends are carrying too much logic inside static HTML. [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L456) is effectively a single-page admin app with globals, duplicated catalog selection code at [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L535) and [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L990), and many full `innerHTML` re-renders. [index.html](/c:/Projects/Liftoff/Server/public/index.html#L272) has the same pattern.
- Medium: observability and test coverage are weak. There are many swallow-and-continue catches in the plugin and UI, such as [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L515), [pluginSocket.js](/c:/Projects/Liftoff/Server/src/pluginSocket.js#L228), and multiple `catch {}` blocks in [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L510). The server has no `test` or `lint` scripts in [package.json](/c:/Projects/Liftoff/Server/package.json#L6), and there is no parallel test project on the plugin side.

## Architectural Concerns
- The core race domain is not isolated from transport and diagnostics. On the Unity side, Photon event capture, lap heuristics, file logging, and WebSocket emission all sit in the same object graph instead of flowing through a clear `capture -> project -> publish` path.
- The multiplayer track-change feature is necessarily reflection-heavy, but the boundary is still too wide. [MultiplayerTrackChangeExecutor.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/Features/MultiplayerTrackControl/MultiplayerTrackChangeExecutor.cs#L32) is effectively a second god object and should be treated as an adapter behind a narrow interface, not as core application logic.
- Public and admin concerns are sharing runtime channels and assets. The admin page is not just "another view"; it has different trust and data needs and should not ride on the same live feed as the public site.

## Suggestions For Splitting The Codebase Into Better Functional Areas
- Split the Unity plugin into `Bootstrap`, `PhotonCapture`, `RaceDomain`, `Transport`, and `GameControlAdapter`. `Plugin` should wire interfaces together, not hold the behavior itself.
- Extract shared contracts for plugin events and server commands into a repo-level contracts package. That package should define message names, payload shapes, stable pilot/session/race identifiers, and command acknowledgements.
- Split the server into four internal modules even if it remains one deployable service: ingestion, domain services, persistence, and delivery. `pluginSocket` should stop being the de facto state store.
- Separate public and admin web clients. Even if both stay "plain JS", they should have their own scripts, state containers, and live API clients rather than two giant inline `<script>` blocks.
- Keep the reflection/reverse-engineering code in its own adapter boundary. The rest of the plugin should depend on an interface like `ITrackControlAdapter`, not on reflection mechanics directly.

## Performance And Optimisation Observations
- The plugin uses synchronous `File.AppendAllText` on the main thread in [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L784), [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L799), and [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L1254). That is fine for debugging bursts, but risky for a live multiplayer plugin over long sessions.
- Race extraction does repeated regex parsing of serialized GMS text in [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L1132) and object reflection in `Describe()` during event logging. These are expensive enough that they should stay behind debug/diagnostic gates where possible.
- The server snapshot path loads the latest race and all of its laps on each live connection in [database.js](/c:/Projects/Liftoff/Server/src/database.js#L246) and [liveSocket.js](/c:/Projects/Liftoff/Server/src/liveSocket.js#L38). In long InfiniteRace sessions, that becomes a hidden O(total laps) reconnect cost.
- The public and admin pages rebuild large sections with `innerHTML` on every update in [index.html](/c:/Projects/Liftoff/Server/public/index.html#L282) and [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L730). That is acceptable at low scale, but it will age badly as live event frequency or admin complexity increases.

## Security And Operational Concerns
- `/admin.html` is BasicAuth-protected in [nginx.conf](/c:/Projects/Liftoff/Server/nginx/nginx.conf#L68), but `/api/admin/*` and `/ws/live` are not behind the same edge guard because of the shared `Authorization` header design noted in [nginx.conf](/c:/Projects/Liftoff/Server/nginx/nginx.conf#L66). That is a fragile auth model.
- The admin bearer token is stored in `localStorage` in [admin.html](/c:/Projects/Liftoff/Server/public/admin.html#L456). Even without the current nickname injection bug, this increases blast radius for any future XSS.
- The plugin project file hardcodes local machine paths, including a personal OneDrive fallback, in [LiftoffPhotonEventLogger.csproj](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.csproj#L12). That is an operational portability problem and should be replaced with local props or environment-driven configuration.
- Admin commands accept largely free-form input and there is no visible schema validation, throttling, or per-command authorization boundary in [admin.js](/c:/Projects/Liftoff/Server/src/routes/admin.js#L12). That is manageable for a private tool, but it should be tightened before wider use.

## Prioritised Refactoring Plan
1. Close the security holes first: split public and admin live channels, stop broadcasting admin-only events to public clients, remove inline event handlers, and replace `localStorage` token handling with edge auth or an httpOnly session.
2. Define a shared contracts layer for events and commands. Add command IDs plus ack/nack responses so admin actions can report actual outcome instead of "socket open".
3. Break [LiftoffPhotonEventLogger.cs](/c:/Projects/Liftoff/Pluggins/LiftoffPhotonEventLogger/LiftoffPhotonEventLogger.cs#L26) into focused services: `RaceStateProjector`, `PhotonEventLogger`, `CompetitionEventPublisher`, and `PlayerIdentityStore`.
4. Isolate risky reverse-engineering behavior behind a narrow game adapter. Cache reflective lookups and capability detection so kick/track-control code is less repetitive and easier to guard.
5. Refactor the server into explicit services: `PluginIngestService`, `RaceQueryService`, `PlaylistService`, `LiveBroadcastService`, and `SqliteRepositories`. Remove cross-module global state.
6. Move the frontend logic into separate JS modules or separate frontend projects. Keep the current UI if you want, but stop embedding application logic inside the HTML files.
7. Add tests around the highest-risk logic first: race projection from plugin events, command parsing/serialization, playlist scheduling, SQL query behavior, and frontend state reducers for live race/player lists.

## Suggested Target Project Structure
```text
Liftoff/
  contracts/
    schemas/
      plugin-events.json
      plugin-commands.json
    ts/
    csharp/

  plugin/
    LiftoffCompetition.Plugin/
      PluginBootstrap.cs
      Photon/
        PhotonCallbackAdapter.cs
        PhotonEventCapture.cs
      RaceDomain/
        RaceStateProjector.cs
        PilotState.cs
        RaceEventFactory.cs
      Transport/
        CompetitionClient.cs
        OutboxBuffer.cs
      GameAdapters/
        ITrackControlAdapter.cs
        LiftoffTrackControlAdapter.cs
        LiftoffKickAdapter.cs
      Diagnostics/
        EventLogWriter.cs
        StateLogWriter.cs

  server/
    src/
      app/
        index.js
      api/
        public/
        admin/
      realtime/
        publicLiveSocket.js
        adminLiveSocket.js
      ingest/
        pluginIngestService.js
      domain/
        raceService.js
        playlistService.js
        identityService.js
      persistence/
        sqlite/
          migrations/
          raceRepository.js
          playlistRepository.js
          templateRepository.js
      contracts/

  web-public/
    src/
      api/
      live/
      state/
      views/

  web-admin/
    src/
      api/
      live/
      state/
      views/
```

Static review only; I did not run the Unity plugin or the live server.
