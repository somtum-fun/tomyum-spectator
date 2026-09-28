# ppy's osu-server-spectator (multiplayer, spectating, online presence, lazer replays) for TomYum.
# Built from ppy's source with a few changes (see README.md): the database password comes from DB_PASS, and lazer
# replays are written to a local folder (mounted
# as the website's public/uploads/replay, where osu-web's "local-replay" disk reads them) instead
# of Amazon S3, which ppy's build always uses. See TomYum docs/lazer.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
ARG SPECTATOR_REPO=https://github.com/ppy/osu-server-spectator
ARG SPECTATOR_REF=b6ac7a9a2b8cbf148d64a01e928f7eea7ced7f64
RUN git clone "$SPECTATOR_REPO" /src && git -C /src checkout "$SPECTATOR_REF"
WORKDIR /src/osu.Server.Spectator
RUN grep -q 'IScoreStorage, S3ScoreStorage>' Extensions/ServiceCollectionExtensions.cs \
    && sed -i 's/IScoreStorage, S3ScoreStorage>/IScoreStorage, FileScoreStorage>/' Extensions/ServiceCollectionExtensions.cs
# ppy's build connects to MySQL without a password (their deployment authenticates otherwise):
# take it from DB_PASS, like ppy's score processor does.
RUN grep -q 'User ID={AppSettings.DatabaseUser};' Database/DatabaseAccess.cs \
    && sed -i 's/User ID={AppSettings.DatabaseUser};/User ID={AppSettings.DatabaseUser};Password={System.Environment.GetEnvironmentVariable("DB_PASS")};/' Database/DatabaseAccess.cs
# Publish lazer presence to Redis for TomYum (online lists, the website's online graph).
COPY TomYumPresencePublisher.cs Hubs/Metadata/TomYumPresencePublisher.cs
RUN grep -q '.AddHostedService<MultiplayerRoomLifetimeBackgroundService>();' Extensions/ServiceCollectionExtensions.cs     && sed -i 's/.AddHostedService<MultiplayerRoomLifetimeBackgroundService>();/.AddHostedService<MultiplayerRoomLifetimeBackgroundService>().AddHostedService<TomYumPresencePublisher>();/' Extensions/ServiceCollectionExtensions.cs
RUN dotnet publish -c Release -o /out && rm -rf /out/runtimes /out/osu.Game.Resources.dll

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build-env /out .
ENTRYPOINT ["dotnet", "osu.Server.Spectator.dll"]
