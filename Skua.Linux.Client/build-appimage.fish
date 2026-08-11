#!/usr/bin/env fish

set -l backend_root "$HOME/Projetos/skua-linux"
set -l electron_root "$HOME/Projetos/aquastar-ruffle"
set -l backend_project "$backend_root/Skua.Backend.Linux/Skua.Backend.Linux.csproj"
set -l backend_stage "$electron_root/appimage-resources/backend"
set -l dist_dir "$electron_root/dist-appimage"
set -l final_dir "$HOME/Downloads/GPT BOT AQW/AppImage"
set -l log_dir "$HOME/Downloads/GPT BOT AQW/Logs/appimage-v4.3-release"
set -l log_file "$log_dir/build-appimage.log"
set -g SKUA_BUILD_LOG_FILE "$log_file"
set -l size_report "$log_dir/size-report.txt"

mkdir -p "$log_dir" "$final_dir"
printf "" > "$SKUA_BUILD_LOG_FILE"

function log
    echo $argv | tee -a "$SKUA_BUILD_LOG_FILE"
end

function require_command
    if not command -q "$argv[1]"
        log "ERROR: required command not found: $argv[1]"
        exit 1
    end
end

function run_logged
    log "> $argv"
    command $argv &| tee -a "$SKUA_BUILD_LOG_FILE"
    set -l status_list $pipestatus
    if test "$status_list[1]" -ne 0
        log "ERROR: command failed with exit code $status_list[1]: $argv"
        exit "$status_list[1]"
    end
end

require_command dotnet
require_command node
require_command npm
require_command sha256sum
require_command find

if test (uname -m) != "x86_64"
    log "ERROR: this first AppImage target is validated only for x86_64."
    exit 1
end

if not test -f "$backend_project"
    log "ERROR: backend project not found: $backend_project"
    exit 1
end

if not test -f "$electron_root/package.json"
    log "ERROR: Electron project not found: $electron_root"
    exit 1
end

log "Skua Linux AppImage v4.3 release candidate build"
log "Backend:  $backend_root"
log "Electron: $electron_root"
log "Logs:     $log_file"
log "Automatic FPS monitoring: removed. Optional PERF/GPU traces remain OFF by default."

# Keep the build deterministic and make sure the pinned self-hosted Ruffle
# package is installed locally before electron-builder collects production
# dependencies.
cd "$electron_root"
run_logged npm install --no-audit --no-fund

if test -d "$electron_root/node_modules/@ruffle-rs/ruffle"
    log "Ruffle package size: "(du -sh "$electron_root/node_modules/@ruffle-rs/ruffle" | awk '{print $1}')
end

# Publish the .NET backend with its own .NET 10 runtime. Do NOT trim or create
# a single-file bundle: Skua uses Roslyn scripting, reflection and dynamically
# loaded assemblies, all of which are safer in the normal multi-file publish
# layout.
rm -rf "$backend_stage"
mkdir -p "$backend_stage"

run_logged dotnet publish "$backend_project" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "$backend_stage" \
    -p:PublishTrimmed=false \
    -p:PublishSingleFile=false \
    -p:DebugType=None \
    -p:DebugSymbols=false

set -l backend_binary "$backend_stage/Skua.Backend.Linux"
if not test -f "$backend_binary"
    log "ERROR: self-contained backend executable was not produced."
    exit 1
end
chmod +x "$backend_binary"

if not test -f "$backend_stage/libcoreclr.so"
    log "ERROR: libcoreclr.so is missing; backend does not look self-contained."
    exit 1
end
if not test -f "$backend_stage/libhostfxr.so"
    log "ERROR: libhostfxr.so is missing; backend does not look self-contained."
    exit 1
end

log "Self-contained backend staged successfully."
log "Backend size: "(du -sh "$backend_stage" | awk '{print $1}')

rm -rf "$dist_dir"

set -l builder "$electron_root/node_modules/.bin/electron-builder"
if not test -x "$builder"
    log "ERROR: electron-builder was not installed by npm."
    exit 1
end

run_logged "$builder" --linux AppImage --x64 --publish never

set -l appimage (find "$dist_dir" -maxdepth 1 -type f -name '*.AppImage' -print -quit)
if test -z "$appimage"
    log "ERROR: electron-builder finished but no AppImage was found in $dist_dir"
    exit 1
end

chmod +x "$appimage"
set -l output_name (basename "$appimage")
set -l final_appimage "$final_dir/$output_name"
cp -f "$appimage" "$final_appimage"
chmod +x "$final_appimage"
sha256sum "$final_appimage" | tee "$final_appimage.sha256" | tee -a "$log_file"

# Build-size audit. This is informational only: no runtime files are deleted
# based on this report. It gives us evidence for future size optimizations.
begin
    echo "Skua Linux AppImage v4.2.1 size report"
    echo "Generated: "(date -Iseconds)
    echo
    echo "== Final artifact =="
    du -h "$final_appimage"
    echo
    echo "== Backend publish =="
    du -sh "$backend_stage"
    echo
    echo "Largest backend files (bytes path):"
    find "$backend_stage" -type f -printf '%s %p\n' | sort -nr | head -n 20
    echo
    echo "== Ruffle package =="
    if test -d "$electron_root/node_modules/@ruffle-rs/ruffle"
        du -sh "$electron_root/node_modules/@ruffle-rs/ruffle"
        echo "Largest Ruffle files (bytes path):"
        find "$electron_root/node_modules/@ruffle-rs/ruffle" -type f -printf '%s %p\n' | sort -nr | head -n 20
    else
        echo "Ruffle package not found."
    end
    echo
    echo "== Electron app payload =="
    for file in main.js index.html skua-parity-ui.js skua.swf Icon.png package.json
        if test -f "$electron_root/$file"
            du -h "$electron_root/$file"
        end
    end
    if test -d "$dist_dir/linux-unpacked"
        echo
        echo "linux-unpacked total:"
        du -sh "$dist_dir/linux-unpacked"
    end
end > "$size_report"

log "Size report: $size_report"

log ""
log "SUCCESS"
log "AppImage: $final_appimage"
log "SHA256:   $final_appimage.sha256"
log ""
log "Run it with:"
log "  \"$final_appimage\""
