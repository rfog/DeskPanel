#!/bin/bash
# ============================================================
# AUDIO PIPELINE - MODO AUTOMÁTICO (WATCHER)
# Vigila la carpeta input/ y procesa automáticamente
# cualquier archivo de audio que aparezca.
#
# Uso:
#   ./watch.sh                  # Vigila con publicación WP
#   ./watch.sh --no-publish     # Sin publicar en WordPress
#
# Para detener: Ctrl+C
# ============================================================

PIPELINE_DIR="$(cd "$(dirname "$0")" && pwd)"
INPUT_DIR="$PIPELINE_DIR/input"
PROCESSED_DIR="$PIPELINE_DIR/input/processed"
NO_PUBLISH_FLAG=""

for arg in "$@"; do
  case $arg in
    --no-publish) NO_PUBLISH_FLAG="--no-publish" ;;
  esac
done

# Verificar fswatch
if ! command -v fswatch &>/dev/null; then
  echo "❌ fswatch no está instalado."
  echo "   Instala con: brew install fswatch"
  exit 1
fi

mkdir -p "$INPUT_DIR"
mkdir -p "$PROCESSED_DIR"

AUDIO_EXTENSIONS="wav|mp3|m4a|aac|flac|ogg|opus"

echo ""
echo "👁️  Audio Pipeline - Modo automático"
echo "   Vigilando: $INPUT_DIR"
echo "   Formatos : wav, mp3, m4a, aac, flac, ogg, opus"
[[ -n "$NO_PUBLISH_FLAG" ]] && echo "   WordPress: desactivado"
echo "   (Ctrl+C para detener)"
echo ""

process_file() {
  local filepath="$1"
  local filename="$(basename "$filepath")"
  local ext="${filename##*.}"

  # Filtrar solo archivos de audio
  if ! echo "$ext" | grep -qiE "^($AUDIO_EXTENSIONS)$"; then
    return
  fi

  # Esperar a que el archivo esté completamente escrito
  sleep 2
  if [[ ! -f "$filepath" ]]; then
    return
  fi

  echo "🎙️  Nuevo archivo detectado: $filename"
  echo "   $(date '+%Y-%m-%d %H:%M:%S')"
  echo ""

  # Ejecutar pipeline
  "$PIPELINE_DIR/run_pipeline.sh" "$filepath" $NO_PUBLISH_FLAG

  # Mover a procesados
  mv "$filepath" "$PROCESSED_DIR/${filename%.*}_$(date +%Y%m%d_%H%M%S).$ext"
  echo "   📦 Archivo movido a: input/processed/"
  echo ""
  echo "👁️  Esperando nuevos archivos..."
  echo ""
}

export -f process_file
export PIPELINE_DIR NO_PUBLISH_FLAG

# Lanzar fswatch
fswatch -0 --event Created --event Renamed "$INPUT_DIR" | \
  xargs -0 -I{} bash -c 'process_file "$@"' _ {}
