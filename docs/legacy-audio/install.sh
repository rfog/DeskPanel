#!/bin/bash
# ============================================================
# AUDIO PIPELINE - INSTALACION
# Ejecutar una sola vez en el Mac
# ============================================================

set -e

echo ""
echo "=========================================="
echo "  Audio Pipeline - Instalacion inicial"
echo "=========================================="
echo ""

# --- Comprobaciones previas ---
if [[ $(uname -m) != "arm64" ]]; then
    echo "Advertencia: Este pipeline esta optimizado para Apple Silicon (M1/M2/M3)."
    echo "En Intel Mac funcionara pero mas lento."
fi

if ! command -v brew &>/dev/null; then
    echo "Homebrew no esta instalado. Instalalo primero:"
    echo "  /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\""
    exit 1
fi

echo "Homebrew detectado"

# --- Dependencias del sistema ---
echo ""
echo "Instalando dependencias del sistema con Homebrew..."

brew install ffmpeg fswatch python@3.11

echo "ffmpeg, fswatch y Python 3.11 instalados"

# --- Entorno virtual Python aislado ---
echo ""
echo "Creando entorno virtual Python aislado..."

PIPELINE_DIR="$(cd "$(dirname "$0")" && pwd)"
VENV_DIR="$PIPELINE_DIR/.venv"

python3.11 -m venv "$VENV_DIR"
source "$VENV_DIR/bin/activate"

echo "Entorno virtual creado en $VENV_DIR"

# --- Dependencias Python ---
echo ""
echo "Instalando dependencias Python..."

pip install --upgrade pip

# Audio y DSP
pip install deepfilternet
pip install soundfile
pip install numpy

# PyTorch - FIJADO a 2.1.0
# No usar versiones superiores: torch 2.10+ rompe la API interna
# de torchaudio.backend que usa DeepFilterNet 0.5.6
pip install torch==2.1.0 torchaudio==2.1.0

# Whisper - se usa openai-whisper directamente
# mlx-whisper descartado: falla al descargar modelos de HuggingFace
# openai-whisper large-v3 no es compatible con MPS (matrices sparse),
# se ejecuta en CPU pero funciona correctamente
pip install openai-whisper

# Generacion de contenido y publicacion
# markdown convierte el post generado a HTML para WordPress REST API
pip install markdown
pip install requests
pip install pyyaml
pip install tqdm

echo "Dependencias Python instaladas"

# --- Crear estructura de carpetas ---
echo ""
echo "Creando estructura de carpetas..."

mkdir -p "$PIPELINE_DIR/input"
mkdir -p "$PIPELINE_DIR/output"
mkdir -p "$PIPELINE_DIR/tmp"
mkdir -p "$PIPELINE_DIR/logs"
mkdir -p "$PIPELINE_DIR/input/processed"

echo "Carpetas creadas"

# --- Hacer ejecutables los scripts shell ---
chmod +x "$PIPELINE_DIR/run_pipeline.sh"
chmod +x "$PIPELINE_DIR/watch.sh"

# --- Verificacion final ---
echo ""
echo "Verificando instalacion..."

python3 -c "
import sys
ok = True

try:
    from df.enhance import enhance, init_df
    print('  OK  DeepFilterNet')
except Exception as e:
    print(f'  FAIL DeepFilterNet: {e}')
    ok = False

try:
    import torch
    mps = torch.backends.mps.is_available()
    print(f'  OK  PyTorch {torch.__version__} (MPS disponible: {mps})')
    if torch.__version__ != '2.1.0':
        print(f'  WARN torch version es {torch.__version__}, se recomienda 2.1.0')
except Exception as e:
    print(f'  FAIL PyTorch: {e}')
    ok = False

try:
    import whisper
    print('  OK  openai-whisper')
except Exception as e:
    print(f'  FAIL openai-whisper: {e}')
    ok = False

try:
    import soundfile
    print('  OK  soundfile')
except Exception as e:
    print(f'  FAIL soundfile: {e}')
    ok = False

try:
    import markdown
    print('  OK  markdown')
except Exception as e:
    print(f'  FAIL markdown: {e}')
    ok = False

try:
    import yaml
    print('  OK  pyyaml')
except Exception as e:
    print(f'  FAIL pyyaml: {e}')
    ok = False

sys.exit(0 if ok else 1)
"

VERIFY_EXIT=$?

echo ""
ffmpeg -version &>/dev/null && echo "  OK  ffmpeg" || echo "  FAIL ffmpeg"
command -v fswatch &>/dev/null && echo "  OK  fswatch" || echo "  FAIL fswatch"

echo ""
echo "=========================================="

if [[ $VERIFY_EXIT -eq 0 ]]; then
    echo "  Instalacion completada correctamente"
else
    echo "  Instalacion completada con advertencias"
    echo "  Revisa los FAIL anteriores antes de continuar"
fi

echo ""
echo "  Proximos pasos:"
echo "  1. Edita config.yaml con tus datos de WordPress"
echo "  2. Abre LM Studio, carga tu modelo y activa el servidor local"
echo "  3. Activa el entorno: source .venv/bin/activate"
echo "  4. Prueba fase a fase: python 01_denoise.py input/audio.wav"
echo "  5. Modo automatico cuando todo funcione: ./watch.sh"
echo "=========================================="
echo ""
