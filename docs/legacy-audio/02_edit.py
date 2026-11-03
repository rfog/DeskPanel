#!/usr/bin/env python3
"""
FASE 2 - TRANSCRIPCION Y EXPORTACION
1. Transcribe el audio limpio con Whisper (guarda cache para fase 3)
2. Convierte 01_denoised.wav a m4a en output/ con el nombre original

El nombre del archivo original se lee de tmp/state.json (guardado por fase 1).

Uso:
    python 02_edit.py
"""

import sys
import os
import json
import argparse
import subprocess
import warnings
warnings.filterwarnings("ignore")

from utils import load_config, get_temp_path, get_output_path

TRANSCRIPT_CACHE = "02_transcript_cache.json"
STATE_FILE = "state.json"


def load_state(cfg: dict) -> dict:
    state_path = get_temp_path(cfg, STATE_FILE)
    if not os.path.exists(state_path):
        print("❌ No se encontro state.json. Ejecuta primero 01_denoise.py")
        sys.exit(1)
    with open(state_path) as f:
        return json.load(f)


def transcribe(audio_path: str, cfg: dict) -> dict:
    model_name = cfg["transcribe"]["model"]
    language = cfg["transcribe"]["language"]
    print(f"  🤖 Transcribiendo con Whisper ({model_name})...")
    try:
        import whisper
        model = whisper.load_model(model_name, device="cpu")
        result = model.transcribe(
            audio_path,
            language=language,
            word_timestamps=True,
            verbose=False,
            fp16=False,
        )
        print("  ✅ Transcripcion completada")
        return result
    except ImportError:
        print("  ❌ openai-whisper no instalado: pip install openai-whisper")
        sys.exit(1)
    except Exception as e:
        print(f"  ❌ Error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


def format_transcript(result: dict) -> str:
    segments = result.get("segments", [])
    if not segments:
        return result.get("text", "").strip()
    lines = []
    current_paragraph = []
    prev_end = None
    for seg in segments:
        text = seg["text"].strip()
        if not text:
            continue
        if prev_end is not None and seg["start"] - prev_end > 2.0:
            if current_paragraph:
                lines.append(" ".join(current_paragraph))
                current_paragraph = []
        current_paragraph.append(text)
        prev_end = seg["end"]
    if current_paragraph:
        lines.append(" ".join(current_paragraph))
    return "\n\n".join(lines)


def save_transcript_cache(result: dict, cfg: dict):
    cache_path = get_temp_path(cfg, TRANSCRIPT_CACHE)
    transcript = format_transcript(result)
    cache = {
        "text": transcript,
        "language": result.get("language", cfg["transcribe"]["language"]),
        "segments": result.get("segments", []),
        "word_count": len(transcript.split()),
        "source": "fase_2_cache",
    }
    with open(cache_path, "w", encoding="utf-8") as f:
        json.dump(cache, f, ensure_ascii=False, indent=2)
    print(f"  💾 Transcripcion en cache: {cache_path}")


def export_to_m4a(input_wav: str, output_path: str, cfg: dict):
    bitrate = cfg.get("export", {}).get("bitrate", "128k")
    print(f"  🎵 Exportando a m4a ({bitrate})...")
    subprocess.run([
        "ffmpeg", "-y", "-i", input_wav,
        "-c:a", "aac", "-b:a", bitrate,
        output_path,
        "-loglevel", "error"
    ], check=True)
    print(f"  ✅ Exportado: {output_path}")


def main():
    cfg = load_config()
    state = load_state(cfg)
    base_name = state["base_name"]

    input_wav = get_temp_path(cfg, "01_denoised.wav")
    if not os.path.exists(input_wav):
        print(f"❌ No se encontro {input_wav}")
        print("   Ejecuta primero: python 01_denoise.py <audio>")
        sys.exit(1)

    output_m4a = get_output_path(cfg, f"{base_name}.m4a")

    print(f"\n🎙️  Fase 2: Transcripcion y exportacion")
    print(f"   Entrada : {input_wav}")
    print(f"   Salida  : {output_m4a}")
    print()

    result = transcribe(input_wav, cfg)
    save_transcript_cache(result, cfg)
    export_to_m4a(input_wav, output_m4a, cfg)

    print(f"\n✅ Fase 2 completada\n")
    return output_m4a


if __name__ == "__main__":
    main()
