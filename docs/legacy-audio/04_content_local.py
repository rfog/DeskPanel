#!/usr/bin/env python3
"""
FASE 4 - GENERACION DE CONTENIDO CON LLM LOCAL
Carga el modelo en LM Studio via 'lms', genera la entrada de blog
y descarga el modelo al terminar.

Requiere LM Studio instalado y 'lms' disponible en el PATH.

Uso:
    python 04_content.py
    python 04_content.py --input ruta/transcripcion.txt
"""

import sys
import os
import json
import argparse
import subprocess
import requests

from utils import load_config, get_output_path, get_temp_path, copy_to_devonthink

STATE_FILE = "state.json"
TRANSCRIPT_CACHE = "02_transcript_cache.json"

AI_DISCLAIMER = (
    "<small><em>Este texto ha sido generado con un LLM a partir "
    "del audio del autor. El contenido y las ideas son del autor; "
    "la redaccion ha sido asistida por inteligencia artificial.</em></small>\n\n"
    "---\n\n"
)


def load_state(cfg: dict) -> dict:
    state_path = get_temp_path(cfg, STATE_FILE)
    if not os.path.exists(state_path):
        print("❌ No se encontro state.json. Ejecuta primero 01_denoise.py")
        sys.exit(1)
    with open(state_path) as f:
        return json.load(f)


def load_model(model_id: str):
    print(f"  📤 Descargando modelos")
    try:
        subprocess.run(
            ["lms", "unload", "--all"],
            check=True,
            capture_output=True
        )
        print(f"  ✅ Modelos descargados")
    except subprocess.CalledProcessError as e:
        print(f"  ❌ Error descargando modelos")
        sys.exit(1)
    except FileNotFoundError:
        print("  ❌ 'lms' no encontrado en el PATH.")
        print("     Instala LM Studio y asegurate de que 'lms' esta disponible.")
        sys.exit(1)

    print(f"  📥 Cargando modelo: {model_id}")
    try:
        subprocess.run(
            ["lms", "load", model_id],
            check=True,
            capture_output=True
        )
        print(f"  ✅ Modelo cargado")
    except subprocess.CalledProcessError as e:
        print(f"  ❌ Error cargando modelo: {e.stderr.decode().strip()}")
        sys.exit(1)
    except FileNotFoundError:
        print("  ❌ 'lms' no encontrado en el PATH.")
        print("     Instala LM Studio y asegurate de que 'lms' esta disponible.")
        sys.exit(1)


def unload_model():
    print(f"  📤 Descargando modelo...")
    try:
        subprocess.run(
            ["lms", "unload", "--all"],
            check=True,
            capture_output=True
        )
        print(f"  ✅ Modelo descargado")
    except Exception as e:
        print(f"  ⚠️  No se pudo descargar el modelo: {e}")


def call_llm(prompt: str, system: str, cfg: dict) -> str:
    url = cfg["content"]["lm_studio_url"] + "/chat/completions"
    model = cfg["content"].get("model_name_content", cfg["content"]["model_name"])
    temperature = cfg["content"]["temperature"]

    payload = {
        "model": model,
        "temperature": temperature,
        "max_tokens": 4096,
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": prompt},
        ],
    }

    try:
        response = requests.post(url, json=payload, timeout=300)
        response.raise_for_status()
        data = response.json()
        return data["choices"][0]["message"]["content"].strip()
    except requests.exceptions.ConnectionError:
        print("  ❌ No se puede conectar al LLM local.")
        print(f"     URL: {url}")
        sys.exit(1)
    except Exception as e:
        print(f"  ❌ Error al llamar al LLM: {e}")
        sys.exit(1)


def build_system_prompt(cfg: dict) -> str:
    lang = cfg["content"]["output_language"]
    lang_name = "español" if lang == "es" else lang
    tone = cfg["content"]["blog_tone"]

    return f"""Eres un escritor profesional de blogs en {lang_name}.
Tu especialidad es tomar contenido hablado y convertirlo en texto escrito
de alta calidad, manteniendo la voz y personalidad del autor original.

TU ESTILO:
- Tono {tone} y cercano, igual que el autor en el audio
- Parrafos de 3-5 lineas, bien construidos y con coherencia interna
- Frases directas, sin giros propios del habla oral
- Subtitulos (##) para separar bloques tematicos cuando el contenido lo pide
- Nunca añades informacion que no este en la transcripcion

FORMATO DE RESPUESTA:
Respondes UNICAMENTE con un objeto JSON valido.
Sin texto antes del JSON. Sin texto despues del JSON.
Sin bloques de codigo. Sin explicaciones.
El JSON empieza con {{ y termina con }}."""


def build_content_prompt(transcript: str, cfg: dict) -> str:
    n_keywords = cfg["content"]["num_keywords"]
    tone = cfg["content"]["blog_tone"]

    return f"""Convierte esta transcripcion de audio en una entrada de blog en español.

INSTRUCCIONES:
1. Reescribe el contenido completo con estilo de blog {tone}
2. Conserva TODAS las ideas, ejemplos y anecdotas del audio
3. Organiza el texto en parrafos logicos con subtitulos si hay secciones claras
4. Elimina muletillas, repeticiones y frases inacabadas propias del habla
5. Mantén el tono personal y la voz del autor
6. La entrada debe tener al menos 400 palabras

TRANSCRIPCION A CONVERTIR:
{transcript}

RESPONDE CON ESTE JSON EXACTO (sin texto adicional):
{{
  "titulo": "Titulo atractivo para el post (maximo 70 caracteres)",
  "slug": "titulo-en-formato-slug-sin-acentos",
  "palabras_clave": ["kw1", "kw2", "kw3", "kw4", "kw5"],
  "meta_descripcion": "Descripcion SEO de maximo 155 caracteres que invite a leer",
  "entrada_blog": "Texto completo del post en Markdown. Usa ## para subtitulos. Minimo 400 palabras."
}}

Genera exactamente {n_keywords} palabras clave relevantes.
IMPORTANTE: Responde SOLO con el JSON. Nada antes. Nada despues."""


def parse_llm_response(response: str) -> dict:
    cleaned = response.strip()

    if "```" in cleaned:
        parts = cleaned.split("```")
        for part in parts:
            part = part.strip()
            if part.startswith("json"):
                part = part[4:].strip()
            try:
                return json.loads(part)
            except Exception:
                continue

    try:
        return json.loads(cleaned)
    except json.JSONDecodeError:
        pass

    start = cleaned.find("{")
    end = cleaned.rfind("}") + 1
    if start >= 0 and end > start:
        try:
            return json.loads(cleaned[start:end])
        except json.JSONDecodeError as e:
            print(f"  ⚠️  Error parseando JSON: {e}")

    print(f"  ⚠️  No se pudo parsear la respuesta.")
    print(f"  Primeros 300 chars:\n{response[:300]}")
    return {
        "titulo": "Sin titulo",
        "slug": "sin-titulo",
        "palabras_clave": [],
        "meta_descripcion": "",
        "entrada_blog": response,
    }


def save_outputs(content: dict, base_name: str, cfg: dict):
    blog_content = AI_DISCLAIMER + content.get("entrada_blog", "")
    content["entrada_blog"] = blog_content

    json_path = get_output_path(cfg, f"{base_name}_content.json")
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(content, f, ensure_ascii=False, indent=2)

    md_path = get_output_path(cfg, f"{base_name}.md")
    titulo = content.get("titulo", "")
    keywords = ", ".join(content.get("palabras_clave", []))
    meta = content.get("meta_descripcion", "")
    slug = content.get("slug", "")

    md_full = f"""---
titulo: {titulo}
slug: {slug}
keywords: {keywords}
meta: {meta}
---

# {titulo}

{blog_content}
"""
    with open(md_path, "w", encoding="utf-8") as f:
        f.write(md_full)

    return json_path, md_path


def main():
    parser = argparse.ArgumentParser(description="Fase 4: Generacion de contenido con LLM")
    parser.add_argument("--input", default=None,
                        help="Ruta al .txt de transcripcion (opcional)")
    args = parser.parse_args()

    cfg = load_config()
    state = load_state(cfg)
    base_name = state["base_name"]
    model = cfg["content"].get("model_name_content", cfg["content"]["model_name"])

    # Cargar transcripcion
    if args.input:
        with open(args.input, "r", encoding="utf-8") as f:
            transcript = f.read()
        print(f"  📄 Transcripcion desde {args.input}")
    else:
        cache_path = get_temp_path(cfg, TRANSCRIPT_CACHE)
        if os.path.exists(cache_path):
            with open(cache_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            transcript = data.get("text", "")
            print(f"  📄 Transcripcion desde cache ({data.get('word_count', 0)} palabras)")
        else:
            txt_path = get_output_path(cfg, f"{base_name}.txt")
            if os.path.exists(txt_path):
                with open(txt_path, "r", encoding="utf-8") as f:
                    transcript = f.read()
                print(f"  📄 Transcripcion desde {txt_path}")
            else:
                print("❌ No se encontro transcripcion. Ejecuta primero 03_transcribe.py")
                sys.exit(1)

    if not transcript.strip():
        print("❌ La transcripcion esta vacia.")
        sys.exit(1)

    word_count = len(transcript.split())

    print(f"\n🤖 Fase 4: Generacion de contenido")
    print(f"   LLM     : {cfg['content']['lm_studio_url']}")
    print(f"   Modelo  : {model}")
    print(f"   Palabras: {word_count}")
    print()

    if word_count > 6000:
        print(f"  ⚠️  Transcripcion larga ({word_count} palabras).")
        print(f"     Command R 32B admite hasta 128k tokens, no deberia ser problema.")
        print()

    # Cargar modelo, generar contenido, descargar modelo
    load_model(model)
    try:
        print("\n  ✍️  Reescribiendo como entrada de blog...")
        system = build_system_prompt(cfg)
        prompt = build_content_prompt(transcript, cfg)
        response = call_llm(prompt, system, cfg)
        print("  ✅ Respuesta recibida del LLM")
    finally:
        # Descargar siempre, aunque haya fallado la generacion
        unload_model()

    content = parse_llm_response(response)
    json_path, md_path = save_outputs(content, base_name, cfg)
    
    # Copiar el markdown del blog a DEVONthink con sufijo (blog)
    blog_dt_name = f"{base_name} (blog).md"
    copy_to_devonthink(md_path, cfg, dest_filename=blog_dt_name)
    
    blog_words = len(content.get("entrada_blog", "").split())
    print(f"\n✅ Fase 4 completada")
    print(f"   📰 Titulo   : {content.get('titulo', 'N/A')}")
    print(f"   📝 Palabras : {blog_words} (transcripcion: {word_count})")
    print(f"   🏷️  Keywords : {', '.join(content.get('palabras_clave', []))}")
    print(f"   → {md_path}")
    print(f"   → {json_path}\n")

    return md_path


if __name__ == "__main__":
    main()
