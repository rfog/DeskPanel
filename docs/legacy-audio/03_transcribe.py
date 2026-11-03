#!/usr/bin/env python3
"""
FASE 3 - TRANSCRIPCION FINAL
Reutiliza la cache de fase 2 si existe (evita re-transcribir).
El nombre de los archivos de salida se toma de tmp/state.json.
Copia la transcripcion a la carpeta inbox de DEVONthink si esta configurada.

Uso:
    python 03_transcribe.py
    python 03_transcribe.py --force   # Re-transcribe aunque haya cache
"""

import sys
import os
import json
import shutil
import argparse
import warnings
warnings.filterwarnings("ignore")

from utils import load_config, get_temp_path, get_output_path, copy_to_devonthink

TRANSCRIPT_CACHE = "02_transcript_cache.json"
STATE_FILE = "state.json"


def load_state(cfg: dict) -> dict:
    state_path = get_temp_path(cfg, STATE_FILE)
    if not os.path.exists(state_path):
        print("❌ No se encontro state.json. Ejecuta primero 01_denoise.py")
        sys.exit(1)
    with open(state_path) as f:
        return json.load(f)


def load_from_cache(cfg: dict) -> dict:
    cache_path = get_temp_path(cfg, TRANSCRIPT_CACHE)
    if os.path.exists(cache_path):
        with open(cache_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        print(f"  ✅ Transcripcion cargada desde cache de fase 2")
        print(f"     ({data.get('word_count', 0)} palabras, sin re-transcribir)")
        return data
    return None


def transcribe_fresh(audio_path: str, cfg: dict) -> dict:
    model_name = cfg["transcribe"]["model"]
    language = cfg["transcribe"]["language"]
    print(f"  🤖 Transcribiendo con Whisper ({model_name}) en CPU...")
    try:
        import whisper
        model = whisper.load_model(model_name, device="cpu")
        result = model.transcribe(
            audio_path,
            language=language,
            word_timestamps=cfg["transcribe"]["word_timestamps"],
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
    if "text" in result and "\n\n" in result.get("text", ""):
        return result["text"]
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

def main():
    parser = argparse.ArgumentParser(description="Fase 3: Transcripcion final")
    parser.add_argument("--force", action="store_true",
                        help="Ignorar cache y transcribir de nuevo")
    args = parser.parse_args()

    cfg = load_config()
    state = load_state(cfg)
    base_name = state["base_name"]

    output_txt = get_output_path(cfg, f"{base_name}.txt")
    output_json = get_output_path(cfg, f"{base_name}.json")

    print(f"\n📝 Fase 3: Transcripcion final")
    print(f"   Salida  : {output_txt}")
    print()

    data = None
    if not args.force:
        data = load_from_cache(cfg)

    if data is None:
        input_path = get_temp_path(cfg, "01_denoised.wav")
        if not os.path.exists(input_path):
            print("❌ No se encontro audio en tmp/. Ejecuta primero 01_denoise.py")
            sys.exit(1)
        print(f"   Entrada : {input_path}")
        result = transcribe_fresh(input_path, cfg)
        transcript = format_transcript(result)
        data = {
            "text": transcript,
            "language": result.get("language", cfg["transcribe"]["language"]),
            "segments": result.get("segments", []),
            "word_count": len(transcript.split()),
        }
    else:
        transcript = data["text"]

    with open(output_txt, "w", encoding="utf-8") as f:
        f.write(transcript)
    with open(output_json, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    # Copiar a DEVONthink inbox
    copy_to_devonthink(output_txt, cfg)

    print(f"\n✅ Fase 3 completada")
    print(f"   📄 {data['word_count']} palabras")
    print(f"   → {output_txt}")
    print(f"   → {output_json}\n")

    return output_txt


if __name__ == "__main__":
    main()
