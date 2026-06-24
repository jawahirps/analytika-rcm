#!/usr/bin/env bash
set -euo pipefail

ALT_APP="/Applications/AltServer.app"
SEARCH_DIRS=("$HOME/Downloads" "$HOME/Desktop" "$HOME/Documents")

if [[ ! -d "$ALT_APP" ]]; then
  echo "AltServer is not installed at $ALT_APP"
  echo "Install it with: brew install --cask altserver"
  exit 1
fi

echo "AltServer: $ALT_APP"

if ! pgrep -x AltServer >/dev/null 2>&1; then
  echo "Starting AltServer..."
  open -a AltServer
  sleep 2
fi

if ! pgrep -x AltServer >/dev/null 2>&1; then
  echo "AltServer did not start."
  exit 1
fi

echo "AltServer is running."

device_info="$(system_profiler SPUSBDataType 2>/dev/null | awk '/iPhone|iPad|iPod/{print; for (i=0; i<18; i++) { if (getline line) print line }}' || true)"
finder_device="$(osascript -e 'tell application "Finder" to get name of every Finder window' 2>/dev/null | tr ',' '\n' | awk '{$1=$1}; /^iPhone$|^iPad$|^iPod$/ { print; exit }' || true)"
if [[ -z "$device_info" ]]; then
  if [[ -z "$finder_device" ]]; then
    echo "No iPhone/iPad is visible over USB or in Finder."
    echo "Connect the device, unlock it, and tap Trust This Computer, then run this script again."
    exit 2
  fi

  echo "Finder shows an iOS device window: $finder_device"
  echo "USB hardware details were not exposed through system_profiler, continuing."
else
  echo "Detected iOS device:"
  echo "$device_info"
fi

ipa_path="${1:-}"
if [[ -z "$ipa_path" ]]; then
  ipa_path="$(find "${SEARCH_DIRS[@]}" -maxdepth 4 \( -iname '*whatsapp*.ipa' -o -iname 'WhatsApp.ipa' \) -print -quit 2>/dev/null || true)"
fi

if [[ -z "$ipa_path" ]]; then
  echo "No WhatsApp .ipa file found in Downloads, Desktop, or Documents."
  echo "Usage: $0 /absolute/path/to/WhatsApp.ipa"
  exit 3
fi

if [[ ! -f "$ipa_path" ]]; then
  echo "IPA file does not exist: $ipa_path"
  exit 4
fi

echo "IPA: $ipa_path"
echo "AltServer does not expose a sideload CLI in this install."
echo "Opening the IPA in Finder. Use AltServer's menu bar item: Install .ipa -> select this file."
open -R "$ipa_path"
