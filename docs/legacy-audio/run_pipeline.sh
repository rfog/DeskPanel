#!/bin/bash
# ============================================================
# AUDIO PIPELINE - ORQUESTADOR PRINCIPAL
# Ejecuta todas las fases en orden para un archivo de audio.
#
# Uso:
#   ./run_pipeline.sh audio.wav              # Todas las fases
#   ./run_pipeline.sh "mi audio.wav"         # Nombre con espacios
#   ./run_pipeline.sh audio.wav --from 3     # Desde la fase 3
#   ./run_pipeline.sh audio.wav --only 4     # Solo la fase 4
#   ./run_pipeline.sh audio.wav --no-publish # Sin publicar en WP
# ============================================================

set -e

PIPELINE_DIR="$(cd "$(dirname "$0")" && pwd)"
VENV="$PIPELINE_DIR/.venv/bin/python"
LOG_DIR="$PIPELINE_DIR/logs"
LOG_FILE="$LOG_DIR/pipeline_$(date +%Y%m%d_%H%M%S).log"

# ── Argumentos ───────────────────────────────────────────────
INPUT_FILE=""
FROM_PHASE=1
ONLY_PHASE=""
NO_PUBLISH=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --from)    FROM_PHASE="$2"; shift 2 ;;
    --only)    ONLY_PHASE="$2"; shift 2 ;;
    --no-publish) NO_PUBLISH=true; shift ;;
    --*)       shift ;;
    *)         if [[ -z "$INPUT_FILE" ]]; then INPUT_FILE="$1"; fi; shift ;;
  esac
done

# ── Validaciones ─────────────────────────────────────────────
if [[ -z "$INPUT_FILE" && -z "$ONLY_PHASE" ]]; then
  echo "Uso: ./run_pipeline.sh <archivo_audio> [opciones]"
  echo ""
  echo "Opciones:"
  echo "  --from N        Empezar desde la fase N (1-5)"
  echo "  --only N        Ejecutar solo la fase N"
  echo "  --no-publish    Saltar la publicacion en WordPress"
  echo ""
  echo "Ejemplos:"
  echo "  ./run_pipeline.sh grabacion.wav"
  echo "  ./run_pipeline.sh \"mi podcast episodio 1.m4a\""
  echo "  ./run_pipeline.sh grabacion.wav --no-publish"
  echo "  ./run_pipeline.sh grabacion.wav --from 3"
  exit 1
fi

if [[ ! -f "$VENV" ]]; then
  echo "Entorno virtual no encontrado. Ejecuta primero: ./install.sh"
  exit 1
fi

mkdir -p "$LOG_DIR"

# ── Funcion para ejecutar fase ───────────────────────────────
# $1 = numero de fase
# $2 = script python
# $3 = argumento de entrada (puede tener espacios, se pasa entre comillas)
# $4 = nombre descriptivo
run_phase() {
  local phase="$1"
  local script="$2"
  local input="$3"
  local name="$4"

  if [[ -n "$ONLY_PHASE" && "$phase" != "$ONLY_PHASE" ]]; then
    return
  fi

  if [[ -z "$ONLY_PHASE" && "$phase" -lt "$FROM_PHASE" ]]; then
    echo "Fase $phase omitida (--from $FROM_PHASE)"
    return
  fi

  echo ""
  echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
  echo "  Fase $phase: $name"
  echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

  START_TIME=$(date +%s)

  # Solo la fase 1 recibe el archivo de entrada como argumento
  # Las fases 2-5 leen el nombre desde tmp/state.json
  if [[ -n "$input" ]]; then
    "$VENV" "$PIPELINE_DIR/$script" "$input" 2>&1 | tee -a "$LOG_FILE"
  else
    "$VENV" "$PIPELINE_DIR/$script" 2>&1 | tee -a "$LOG_FILE"
  fi

  EXIT_CODE=${PIPESTATUS[0]}
  END_TIME=$(date +%s)
  ELAPSED=$((END_TIME - START_TIME))

  if [[ $EXIT_CODE -ne 0 ]]; then
    echo ""
    echo "Error en la fase $phase. Pipeline detenido."
    echo "Revisa el log: $LOG_FILE"
    exit $EXIT_CODE
  fi

  echo "   Completada en ${ELAPSED}s"
}

# ── Inicio ───────────────────────────────────────────────────
echo ""
echo "╔══════════════════════════════════════════╗"
echo "║        Audio Pipeline - Inicio           ║"
echo "╚══════════════════════════════════════════╝"
echo "  Archivo : ${INPUT_FILE:-'(usando archivos tmp existentes)'}"
echo "  Log     : $LOG_FILE"
echo "  Fecha   : $(date '+%Y-%m-%d %H:%M:%S')"

START_TOTAL=$(date +%s)

# ── Ejecutar fases ───────────────────────────────────────────
# Solo fase 1 recibe INPUT_FILE; las demas leen state.json
run_phase 1 "01_denoise.py"  "$INPUT_FILE" "Limpieza de ruido"
run_phase 2 "02_edit.py"     ""            "Transcripcion y exportacion"
run_phase 3 "03_transcribe.py" ""          "Transcripcion final"
# run_phase 4 "04_content.py"  ""            "Generacion de contenido (LLM)"

# if [[ "$NO_PUBLISH" == false ]]; then
#   run_phase 5 "05_publish.py" "" "Publicacion en WordPress"
# else
  echo ""
  echo "Fase 5 omitida (--no-publish)"
# fi

# ── Limpieza de temporales ───────────────────────────────────
if [[ -z "$ONLY_PHASE" ]]; then
  echo ""
  echo "Limpiando archivos temporales..."
  TMP_DIR="$PIPELINE_DIR/tmp"
  [[ -f "$TMP_DIR/01_denoised.wav" ]]     && rm "$TMP_DIR/01_denoised.wav"
  [[ -f "$TMP_DIR/01_denoised_raw.wav" ]] && rm "$TMP_DIR/01_denoised_raw.wav"
  [[ -f "$TMP_DIR/02_transcript_cache.json" ]] && rm "$TMP_DIR/02_transcript_cache.json"
  [[ -f "$TMP_DIR/state.json" ]]          && rm "$TMP_DIR/state.json"
  echo "  Temporales eliminados"
fi

END_TOTAL=$(date +%s)
TOTAL=$((END_TOTAL - START_TOTAL))

echo ""
echo "╔══════════════════════════════════════════╗"
echo "║     Pipeline completado                  ║"
printf "║     Tiempo total: %-23s ║\n" "${TOTAL}s"
echo "╚══════════════════════════════════════════╝"
echo ""
echo "  Resultados en: $PIPELINE_DIR/output/"
echo ""
