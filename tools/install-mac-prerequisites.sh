#!/bin/zsh
# Installs the external components DeskPanel needs on a Mac (Apple Silicon) and reports what is still missing.
#
#   Downloaded here (to ~/.DeskPanel/tools, or the folder given as the first argument):
#     - deep-filter 0.5.6 (DeepFilterNet): noise removal, the same model the Windows build uses
#     - the whisper.cpp ggml model (second argument; default ggml-large-v3-turbo.bin, about 1.6 GB)
#   Installed with Homebrew when missing:
#     - ffmpeg
#   Only checked (install them yourself):
#     - Google Chrome (PDF generation), DEVONthink, the .NET 10 SDK (to build)
#
# Safe to run again: anything already in place is kept. Downloads go to a .part file first, so an interrupted run
# never leaves a truncated file behind.

set -eu

TOOLS="${1:-$HOME/.DeskPanel/tools}"
MODEL="${2:-ggml-large-v3-turbo.bin}"
DEEP_FILTER_URL="https://github.com/Rikorose/DeepFilterNet/releases/download/v0.5.6/deep-filter-0.5.6-aarch64-apple-darwin"
MODEL_URL="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$MODEL"

if [[ "$(uname -m)" != "arm64" ]]; then
  echo "AVISO: este script descarga la versión para Apple Silicon (arm64); esta máquina es $(uname -m)."
fi

mkdir -p "$TOOLS/models"

download() {
  local url="$1" target="$2"
  if [[ -s "$target" ]]; then
    echo "ya existe: $target"
    return
  fi
  echo "descargando $(basename "$target")…"
  curl --fail --location --retry 3 --silent --show-error -o "$target.part" "$url"
  mv "$target.part" "$target"
}

download "$DEEP_FILTER_URL" "$TOOLS/deep-filter"
chmod +x "$TOOLS/deep-filter"
download "$MODEL_URL" "$TOOLS/models/$MODEL"

find_ffmpeg() {
  command -v ffmpeg 2>/dev/null || { [[ -x /opt/homebrew/bin/ffmpeg ]] && echo /opt/homebrew/bin/ffmpeg; } || { [[ -x /usr/local/bin/ffmpeg ]] && echo /usr/local/bin/ffmpeg; } || true
}

if [[ -z "$(find_ffmpeg)" ]]; then
  if command -v brew >/dev/null 2>&1 || [[ -x /opt/homebrew/bin/brew ]]; then
    echo "instalando ffmpeg con Homebrew…"
    "$(command -v brew || echo /opt/homebrew/bin/brew)" install ffmpeg
  else
    echo "AVISO: falta ffmpeg y no hay Homebrew (https://brew.sh); instálalo con: brew install ffmpeg"
  fi
fi

check() {
  if [[ "$2" == "ok" ]]; then echo "  ✔ $1"; else echo "  ✘ $1"; fi
}

echo
echo "Estado:"
check "ffmpeg" "$([[ -n "$(find_ffmpeg)" ]] && echo ok)"
check "deep-filter ($TOOLS/deep-filter)" "$([[ -x "$TOOLS/deep-filter" ]] && echo ok)"
check "modelo de Whisper ($MODEL)" "$([[ -s "$TOOLS/models/$MODEL" ]] && echo ok)"
check "Google Chrome" "$([[ -d "/Applications/Google Chrome.app" ]] && echo ok)"
check "DEVONthink" "$([[ -d "/Applications/DEVONthink.app" ]] && echo ok)"
check ".NET SDK 10" "$(dotnet --list-sdks 2>/dev/null | grep -q '^10\.' && echo ok)"
