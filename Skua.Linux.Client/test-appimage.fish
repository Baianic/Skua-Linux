#!/usr/bin/env fish

set -l appimage "$argv[1]"
if test -z "$appimage"
    set appimage (find "$HOME/Downloads/GPT BOT AQW/AppImage" -maxdepth 1 -type f -name 'Skua-Linux-*.AppImage' -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -n1 | cut -d' ' -f2-)
end

if test -z "$appimage"; or not test -f "$appimage"
    echo "ERROR: AppImage not found. Pass its path as the first argument."
    exit 1
end

set -l log_dir "$HOME/Downloads/GPT BOT AQW/Logs/appimage-v4.1"
mkdir -p "$log_dir"
set -l runtime_log "$log_dir/runtime-appimage.log"

echo "Testing: $appimage"
echo "Runtime log: $runtime_log"
chmod +x "$appimage"
"$appimage" &| tee -i "$runtime_log"
