#!/usr/bin/env python3
"""
FASE 1 - LIMPIEZA DE RUIDO
Usa DeepFilterNet3 para eliminar ruido de fondo del audio.
Guarda el nombre del archivo original en tmp/state.json
para que los pasos siguientes lo usen sin necesidad de parametros.

Uso:
    python 01_denoise.py input/mi_audio.m4a
"""

import sys
import os
import json
import argparse
import numpy as np
import soundfile as sf
from tqdm import tqdm

from utils import load_config, get_temp_path, get_output_path

OUTPUT_FILENAME = "01_denoised.wav"
STATE_FILE = "state.json"


def save_state(cfg: dict, input_path: str):
    """Guarda el nombre del archivo original para los pasos siguientes."""
    state = {
        "input_path": input_path,
        "base_name": os.path.splitext(os.path.basename(input_path))[0],
    }
    state_path = get_temp_path(cfg, STATE_FILE)
    with open(state_path, "w") as f:
        json.dump(state, f, indent=2)


def load_audio(path: str):
    import subprocess
    import tempfile
    tmp = tempfile.mktemp(suffix=".wav")
    cmd = [
        "ffmpeg", "-y", "-i", path,
        "-ar", "48000",
        "-ac", "1",
        "-f", "wav", tmp,
        "-loglevel", "error"
    ]
    subprocess.run(cmd, check=True)
    audio, sr = sf.read(tmp)
    os.remove(tmp)
    return audio, sr


def process_with_deepfilter(audio: np.ndarray, sr: int, chunk_seconds: int):
    try:
        import torch
        import warnings
        warnings.filterwarnings("ignore", category=UserWarning, module="df")
        from df.enhance import enhance, init_df

        device = "cpu"
        if torch.cuda.is_available():
            device = "cuda"
        print(f"  🔧 Dispositivo: {device.upper()}")

        model, df_state, _ = init_df()
        chunk_samples = chunk_seconds * sr
        total_chunks = int(np.ceil(len(audio) / chunk_samples))
        processed_chunks = []

        for i in tqdm(range(total_chunks), desc="  Limpiando ruido", unit="chunk"):
            start = i * chunk_samples
            end = min(start + chunk_samples, len(audio))
            chunk = audio[start:end]
            for attempt in range(3):
                try:
                    chunk_tensor = torch.from_numpy(chunk).float()
                    if chunk_tensor.dim() == 1:
                        chunk_tensor = chunk_tensor.unsqueeze(0)
                    enhanced_tensor = enhance(model, df_state, chunk_tensor)
                    enhanced = enhanced_tensor.squeeze(0).numpy()
                    processed_chunks.append(enhanced)
                    break
                except RuntimeError as e:
                    if "out of memory" in str(e).lower() and attempt < 2:
                        print(f"\n  Sin memoria en chunk {i}, limpiando cache...")
                        if torch.cuda.is_available():
                            torch.cuda.empty_cache()
                    else:
                        raise

        return np.concatenate(processed_chunks)

    except ImportError as e:
        print(f"  Error de importacion: {e}")
        print("     Prueba: pip install deepfilternet torch==2.1.0 torchaudio==2.1.0")
        sys.exit(1)
    except Exception as e:
        print(f"  Error inesperado en DeepFilterNet: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


def apply_ffmpeg_mastering(input_wav: str, output_wav: str):
    import subprocess
    print("  Aplicando masterizacion...")
    cmd = [
        "ffmpeg", "-y", "-i", input_wav,
        "-af", (
            "highpass=f=80,"
            "lowpass=f=15000,"
            "acompressor=threshold=-18dB:ratio=3:attack=5:release=50,"
            "loudnorm=I=-16:TP=-1.5:LRA=11"
        ),
        "-ar", "48000",
        output_wav,
        "-loglevel", "error"
    ]
    subprocess.run(cmd, check=True)
    print("  Masterizacion aplicada")


def main():
    parser = argparse.ArgumentParser(description="Fase 1: Limpieza de ruido")
    parser.add_argument("input", help="Archivo de audio de entrada")
    parser.add_argument("--skip-mastering", action="store_true")
    args = parser.parse_args()

    if not os.path.exists(args.input):
        print(f"Archivo no encontrado: {args.input}")
        sys.exit(1)

    cfg = load_config()
    chunk_seconds = cfg["denoise"]["chunk_seconds"]
    output_path = get_temp_path(cfg, OUTPUT_FILENAME)

    # Guardar estado para pasos siguientes
    save_state(cfg, args.input)

    print(f"\n🎙️  Fase 1: Limpieza de ruido")
    print(f"   Entrada : {args.input}")
    print(f"   Salida  : {output_path}")
    print()

    print("  Cargando audio...")
    audio, sr = load_audio(args.input)
    duration = len(audio) / sr
    print(f"  Audio cargado: {duration:.1f}s a {sr}Hz")

    print("\n  Aplicando DeepFilterNet3...")
    cleaned = process_with_deepfilter(audio, sr, chunk_seconds)

    tmp_clean = get_temp_path(cfg, "01_denoised_raw.wav")
    sf.write(tmp_clean, cleaned, sr)

    if not args.skip_mastering:
        print("\n  Masterizando...")
        apply_ffmpeg_mastering(tmp_clean, output_path)
        os.remove(tmp_clean)
    else:
        os.rename(tmp_clean, output_path)

    print(f"\n✅ Fase 1 completada -> {output_path}\n")
    return output_path


if __name__ == "__main__":
    main()
