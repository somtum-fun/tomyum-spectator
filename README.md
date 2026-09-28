# tomyum-spectator

ppy's [osu-server-spectator](https://github.com/ppy/osu-server-spectator) (osu!lazer's multiplayer,
spectating, online presence and replay saving) built for TomYum. The Dockerfile clones ppy's source
at a pinned commit and changes it in three small ways:

1. **Replays on disk.** Lazer replays are written to `REPLAYS_PATH` (the website's
   `public/uploads/replay`, where osu-web reads them and TomYum's circleguard checks them) instead
   of Amazon S3, which ppy's build always uses. Files are named by the website score id.
2. **Database password.** The MySQL connection takes its password from `DB_PASS`, like ppy's score
   processor (ppy's own deployment authenticates otherwise).
3. **Presence for TomYum.** `TomYumPresencePublisher.cs` writes who's online on lazer and what
   they're doing to Redis (`tomyum:lazer-presence`, every 5 seconds). TomYum merges it with stable
   players and website visitors: the staff panel's online list, Discord's `/online` and status
   message, `!alert`, and the website's online graph.

## Building

TomYum's production compose builds it from a checkout next to TomYum:

```
git clone https://github.com/sundeiii/tomyum-spectator ~/tomyum-spectator
# deploy/production/.env: SPECTATOR_PATH=../../../tomyum-spectator (the default)
docker compose build spectator
```

`SPECTATOR_REF` (a build argument) is the ppy commit it builds; each `sed` step checks that the
line it changes is still there, so an update of ppy's code that moved it fails the build instead of
silently skipping a change.

Settings are ppy's (`DB_HOST`, `DB_USER`, `DB_PASS`, `REDIS_HOST`, `SHARED_INTEROP_*`,
`SAVE_REPLAYS=true`, `REPLAYS_PATH`, …); see TomYum's `deploy/production/docker-compose.yml` and
`docs/lazer.md`.
