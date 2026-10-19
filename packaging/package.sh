#!/usr/bin/env bash
# Builds a release of TTRPG.Codex for Linux or macOS: the app, the content packs, a bundled .NET
# runtime and a launcher, as a .tar.gz plus a .deb (Linux) or .pkg (macOS).
#
#   packaging/package.sh <rid> <version> [out-dir]
#   packaging/package.sh linux-x64 1.4.0
#   packaging/package.sh osx-arm64 1.4.0-beta.2 dist
#
# Why a bundled runtime instead of a self-contained, trimmed or AOT build: the embedded RavenDB
# server runs as a separate `dotnet Raven.Server.dll` process and needs the full shared .NET and
# ASP.NET Core runtimes, so a release has to ship them anyway. The app then runs on that same
# runtime (framework-dependent, ReadyToRun-compiled for start-up speed) rather than carrying a
# second, trimmed copy. Native AOT is not an option: Blazor Server, RavenDB, YamlDotNet and
# DynamicExpresso all rely on reflection or runtime code generation.
set -euo pipefail

rid="${1:?usage: package.sh <rid> <version> [out-dir]}"
version="${2:?usage: package.sh <rid> <version> [out-dir]}"
repo=$(cd "$(dirname "$0")/.." && pwd)
out="${3:-$repo/dist}"
mkdir -p "$out"
out=$(cd "$out" && pwd)
stage="$out/$rid"
app="$stage/app"
name="TTRPG.Codex-$version-$rid"

case "$rid" in
    linux-x64) os=linux; arch=x64; deb_arch=amd64 ;;
    linux-arm64) os=linux; arch=arm64; deb_arch=arm64 ;;
    osx-arm64) os=osx; arch=arm64 ;;
    osx-x64) os=osx; arch=x64 ;;
    *) echo "Unsupported runtime identifier: $rid" >&2; exit 1 ;;
esac

rm -rf "$stage"
mkdir -p "$app"

echo "==> Publishing the app for $rid ($version)"
dotnet publish "$repo/src/Codex.Web/Codex.Web.csproj" -c Release -r "$rid" --self-contained false \
    -p:UseAppHost=false -p:PublishReadyToRun=true -p:Version="$version" -o "$app"

# The embedded RavenDB server ships native libraries for every platform (over 400 MB); it only
# loads its own platform's, so keep that one plus the generic OS folders.
echo "==> Trimming RavenDB native libraries to $rid"
for dir in "$app"/RavenDBServer/runtimes/*; do
    case "$(basename "$dir")" in
        "$rid" | "$os" | unix) ;;
        *) rm -rf "$dir" ;;
    esac
done

echo "==> Adding content packs and the launcher"
cp -R "$repo/content" "$app/content"
cp "$repo/packaging/launchers/ttrpg-codex" "$app/ttrpg-codex"
chmod +x "$app/ttrpg-codex"
cp "$repo/LICENSE" "$repo/LEGAL.md" "$app/"

echo "==> Bundling the .NET and ASP.NET Core runtimes"
installer="$stage/dotnet-install.sh"
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
for runtime in dotnet aspnetcore; do
    bash "$installer" --channel 10.0 --runtime "$runtime" --os "$os" --architecture "$arch" --install-dir "$app/runtime" --no-path
done
rm -f "$installer"

echo "==> Archiving"
(cd "$stage" && mv app "$name" && tar -czf "$out/$name.tar.gz" "$name" && mv "$name" app)

if [ "$os" = linux ]; then
    echo "==> Building the .deb"
    deb_version="${version//-/\~}" # a pre-release sorts before its release
    root="$stage/deb"
    mkdir -p "$root/DEBIAN" "$root/opt/ttrpg-codex" "$root/usr/bin" "$root/usr/share/applications"
    cp -R "$app/." "$root/opt/ttrpg-codex/"
    ln -s /opt/ttrpg-codex/ttrpg-codex "$root/usr/bin/ttrpg-codex"
    cat > "$root/usr/share/applications/ttrpg-codex.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=TTRPG.Codex
Comment=Run tabletop campaigns: characters, rules, world wiki and a live table
Exec=ttrpg-codex
Terminal=true
Categories=Game;RolePlaying;
EOF
    cat > "$root/DEBIAN/control" <<EOF
Package: ttrpg-codex
Version: $deb_version
Architecture: $deb_arch
Maintainer: TTRPG.Codex <noreply@github.com>
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl1.1, libicu74 | libicu72 | libicu70 | libicu67
Section: games
Priority: optional
Homepage: https://github.com/myarichuk/TTRPG.Codex
Description: Tabletop RPG campaign manager
 Characters with data-driven rules, a world wiki, journals, a bestiary and a live
 table. Run "ttrpg-codex" and open http://localhost:5080. Campaign data is kept in
 ~/.local/share/ttrpg-codex.
EOF
    dpkg-deb --build --root-owner-group "$root" "$out/ttrpg-codex_${deb_version}_${deb_arch}.deb"
fi

if [ "$os" = osx ]; then
    echo "==> Building the .pkg"
    root="$stage/pkgroot"
    mkdir -p "$root/TTRPG.Codex"
    cp -R "$app/." "$root/TTRPG.Codex/"
    # Double-clickable in Finder: opens Terminal, starts the server and the browser.
    cp "$app/ttrpg-codex" "$root/TTRPG.Codex/Start TTRPG.Codex.command"
    pkg_version=$(echo "$version" | sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+).*/\1/')
    pkgbuild --root "$root" --install-location /Applications --identifier io.github.myarichuk.ttrpg-codex \
        --version "$pkg_version" "$out/$name.pkg"
fi

echo "==> Done: $out"
ls -lh "$out" | grep -E "$version|ttrpg-codex_" || true
