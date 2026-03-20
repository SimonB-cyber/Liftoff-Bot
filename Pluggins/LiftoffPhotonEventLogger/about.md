# Liftoff Photon Event Logger

`LiftoffPhotonEventLogger` is a BepInEx plugin for Liftoff that watches Photon multiplayer traffic and turns it into useful diagnostics and race telemetry.

At its core, the plugin registers as a Photon callback target and records:

- raw Photon events received by the client
- room and player property changes
- per-event-code log files for easier protocol inspection
- race and lap data derived from Photon event `200` and player-state snapshots

It writes this information into structured log files under the plugin folder, including:

- general Photon event logs
- multiplayer state logs
- race summary logs
- JSONL race event output for downstream analysis

The plugin also includes an experimental `MultiplayerTrackControl` feature. This layer is focused on host-side multiplayer settings changes and can:

- inspect Liftoff's multiplayer UI and related runtime objects by reflection
- detect likely host/session state
- expose debug hotkeys and optional on-screen controls
- attempt track, race, environment, and workshop-content changes through Liftoff's own multiplayer setup flow
- queue and retry deferred host-setting changes when the game UI is not ready yet
- optionally explore or announce multiplayer chat interactions

In short, this plugin is both:

- a Photon/race telemetry logger for reverse-engineering and analysis
- a sandbox for experimenting with multiplayer room and content control inside Liftoff

The logging path is the stable part of the project. The multiplayer track-control path is intentionally experimental and designed to help discover how Liftoff applies host-side room updates in current builds.
