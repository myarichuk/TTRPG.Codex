# Self-contained deployment image: builds the app, both system plugins, and both
# content packs from source, then ships them the way Program.cs expects - plugins/
# beside the app binary (see the B10 comment in Program.cs), data on a volume.
#
# Notes:
# - No trimming / AOT: PublishTrimmed breaks reflection plugin loading, YamlDotNet,
#   DynamicExpresso and RavenDB (see release.yml); NativeAOT is a non-starter for
#   Blazor Server + RavenDB. The publish below is framework-dependent.
# - The runtime stage uses aspnet (not runtime-deps): embedded RavenDB shells out to
#   `dotnet Raven.Server.dll`, so a .NET runtime must be present (B10).
# - Packs are generated inside the build (they are not checked in).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*

COPY *.sln Directory.Build.props Directory.Build.targets ./
COPY src/ ./src/
COPY scripts/ ./scripts/
RUN dotnet restore TTRPG.Codex.sln

RUN dotnet publish src/Codex.Web/Codex.Web.csproj -c Release -o /app --no-restore \
    && dotnet build src/Codex.Systems.DnD5e/Codex.Systems.DnD5e.csproj -c Release -o /tmp/pbuild --no-restore \
    && dotnet build src/Codex.Systems.Pf2e/Codex.Systems.Pf2e.csproj -c Release -o /tmp/pbuild --no-restore \
    && mkdir -p /app/plugins \
    && cp /tmp/pbuild/Codex.Systems.DnD5e.dll /tmp/pbuild/Codex.Systems.Pf2e.dll /app/plugins/ \
    && python3 scripts/import_srd51.py --output /app/plugins/srd51-full --no-install \
    && python3 scripts/import_pf2e.py --output /app/plugins/pf2e-remastered --no-install

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /data && chown app /data

ENV ASPNETCORE_URLS=http://+:8080 \
    Codex__DataDirectory=/data
VOLUME /data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Codex.Web.dll"]
