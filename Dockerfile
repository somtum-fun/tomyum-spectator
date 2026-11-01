# ppy's osu-server-spectator (multiplayer, spectating, online presence, lazer replays) for TomYum.
# Built from ppy's source with a few changes (see README.md): the database password comes from DB_PASS, and lazer
# replays are written to a local folder (mounted
# as the website's public/uploads/replay, where osu-web's "local-replay" disk reads them) instead
# of Amazon S3, which ppy's build always uses. See TomYum docs/lazer.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
ARG SPECTATOR_REPO=https://github.com/ppy/osu-server-spectator
ARG SPECTATOR_REF=eb52d25e44de269790828fabb245d45a1f7e86fa
RUN git clone "$SPECTATOR_REPO" /src && git -C /src checkout "$SPECTATOR_REF"
WORKDIR /src/osu.Server.Spectator
# ppy's packages are on 10.0.13 (October 2026's fixes), but ppy.osu.Game still brings MessagePack 3.1.8:
# 3.1.9 fixes a denial of service (CVE-2026-92707), 3.1.10 a ReDoS.
RUN dotnet add package MessagePack --version 3.1.12
# MySqlConnector 2.6.1 fixes a password leak in zero-configuration TLS (GHSA-473q-m89c-ghf8); AutoMapper as in TomYum.
RUN dotnet add package MySqlConnector --version 2.6.2 && dotnet add package AutoMapper --version 16.2.0
RUN grep -q 'IScoreStorage, S3ScoreStorage>' Extensions/ServiceCollectionExtensions.cs \
    && sed -i 's/IScoreStorage, S3ScoreStorage>/IScoreStorage, FileScoreStorage>/' Extensions/ServiceCollectionExtensions.cs
# ppy's build connects to MySQL without a password (their deployment authenticates otherwise):
# take it from DB_PASS, like ppy's score processor does.
RUN grep -q 'User ID={AppSettings.DatabaseUser};' Database/DatabaseAccess.cs \
    && sed -i 's/User ID={AppSettings.DatabaseUser};/User ID={AppSettings.DatabaseUser};Password={System.Environment.GetEnvironmentVariable("DB_PASS")};/' Database/DatabaseAccess.cs
# Publish lazer presence to Redis for TomYum (online lists, the website's online graph).
COPY TomYumPresencePublisher.cs Hubs/Metadata/TomYumPresencePublisher.cs
RUN grep -q '.AddHostedService<MultiplayerRoomLifetimeBackgroundService>();' Extensions/ServiceCollectionExtensions.cs     && sed -i 's/.AddHostedService<MultiplayerRoomLifetimeBackgroundService>();/.AddHostedService<MultiplayerRoomLifetimeBackgroundService>().AddHostedService<TomYumPresencePublisher>();/' Extensions/ServiceCollectionExtensions.cs
# Tell TomYum whether a lazer play was paused (osu!dan's "no pausing"): see TomYumPauseTracker.cs.
COPY TomYumPauseTracker.cs Hubs/Spectator/TomYumPauseTracker.cs
RUN grep -q 'usage.Item.ScoreTokens.Add(scoreToken.Value);' Hubs/Spectator/SpectatorHub.cs \
    && grep -q 'await scoreBuffer.UpdateAsync(scoreToken.Value, data);' Hubs/Spectator/SpectatorHub.cs \
    && grep -q 'var score = await scoreBuffer.DequeueAsync(scoreToken.Value);' Hubs/Spectator/SpectatorHub.cs \
    && sed -i -e 's/usage.Item.ScoreTokens.Add(scoreToken.Value);/usage.Item.ScoreTokens.Add(scoreToken.Value); TomYumPauseTracker.Begin(scoreToken.Value, state);/' \
        -e 's/await scoreBuffer.UpdateAsync(scoreToken.Value, data);/await scoreBuffer.UpdateAsync(scoreToken.Value, data); TomYumPauseTracker.Frames(scoreToken.Value, data);/' \
        -e 's/var score = await scoreBuffer.DequeueAsync(scoreToken.Value);/TomYumPauseTracker.End(scoreToken.Value); var score = await scoreBuffer.DequeueAsync(scoreToken.Value);/' \
        Hubs/Spectator/SpectatorHub.cs \
    && sed -i 's/.AddHostedService<TomYumPresencePublisher>();/.AddHostedService<TomYumPresencePublisher>().AddHostedService<osu.Server.Spectator.Hubs.Spectator.TomYumPauseTracker>();/' Extensions/ServiceCollectionExtensions.cs \
    && grep -q 'TomYumPauseTracker>()' Extensions/ServiceCollectionExtensions.cs
RUN dotnet publish -c Release -o /out && rm -rf /out/runtimes /out/osu.Game.Resources.dll

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ca-certificates: the base image's are older than Ubuntu's fix for the eMudhra CA key compromise
# (CVE-2026-111943, 20260816ubuntu0.24.04.1): upgrade them until Microsoft's image has it.
RUN apt-get update && apt-get install -y --only-upgrade ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build-env /out .
ENTRYPOINT ["dotnet", "osu.Server.Spectator.dll"]
