# Liftoff Competition

## What It Is

Liftoff Competition is a live event and race-management layer for Liftoff multiplayer.

It turns a normal multiplayer session into something that feels much more like a hosted event: live race visibility for spectators, practical controls for organisers, smarter track rotation, and in-game communication tools that help keep players informed.

At a high level, it connects the game host, a web dashboard, and a live public view so a community race night, league session, or special event is easier to run and easier to follow.

## The Big Idea

Instead of treating a Liftoff lobby like a one-off session, Liftoff Competition makes it feel like a managed experience.

That means you can:

- show a live race view in the browser
- see who is currently online
- control tracks from an admin panel
- build and run playlists instead of changing tracks manually every time
- communicate with players through live chat and automated announcements
- capture race activity in a way that supports leaderboards, stats, and event storytelling

## Why It Matters

For organisers, it reduces friction.

For pilots, it creates a more structured and professional race-night experience.

For communities, it makes multiplayer sessions more watchable, repeatable, and scalable.

## Core Experience

### Public Live View

The public-facing view is designed for spectators and participants who just want to see what is happening right now.

It currently highlights:

- race in progress
- current track context
- live lap activity
- players online
- connection/live status

This makes it useful for club nights, streams, casual events, and community competitions where people want a quick, clear picture of the session.

### Admin Control Panel

The admin side is built for the person running the session.

It gives organisers a browser-based control surface for:

- monitoring who is in the lobby
- moving to the next track
- setting a specific track
- refreshing the available track catalog
- managing playlists
- running scheduled track rotation
- sending chat directly into the game
- managing automated chat templates
- kicking players when moderation is needed

## Chat Options

One of the strongest parts of the platform is that chat is not just a message box. It becomes part of the event flow.

Current chat options include:

- live chat visibility inside the admin panel
- sending one-off manual messages into the game
- automated message templates tied to event triggers
- scheduled warning-style messages before a track change
- race-start and race-end announcements
- track-change announcements with dynamic placeholders

Supported template triggers include:

- `track_change`
- `race_start`
- `race_end`

Available template variables include details such as:

- `{env}`
- `{track}`
- `{race}`
- `{mins}`
- `{winner}`
- `{time}`

This means organisers can do things like:

- announce the next track automatically
- send a two-minute warning before rotation
- congratulate the winner after a race
- keep the lobby informed without manually typing every update

There is also a player-facing chat command flow in the session itself, including:

- `/help`
- `/skip`
- `/agree`

That gives players a lightweight way to interact with the event format, especially around vote-to-skip behaviour.

## Playlist and Event Flow

Playlists help turn a loose session into a proper programme.

Organisers can create named playlists, add tracks, reorder them, start the run, stop it, or skip ahead when needed. That is especially useful for:

- weekly club events
- qualifying sessions
- curated race nights
- community tournaments
- stream-friendly scheduled sessions

## Who It Is For

Liftoff Competition is best suited to:

- race organisers
- community hosts
- league admins
- content creators and streamers
- anyone who wants multiplayer sessions to feel more like an organised event than an ad-hoc lobby

## Positioning Summary

Liftoff Competition brings structure, visibility, and communication to Liftoff multiplayer.

It is part live race display, part organiser dashboard, and part event-automation toolset.

If the goal is to make Liftoff sessions feel more professional, more social, and easier to run, this is the layer that makes that possible.
