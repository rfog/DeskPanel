#!/usr/bin/env python3
"""
FASE 4 - GENERACION DE CONTENIDO CON GEMINI API
Utiliza la API de Google Gemini para transformar la transcripcion.
"""

import sys
import os
import json
import argparse
import google.generativeai as genai

from utils import load_config, get_output_path, get_temp_path, copy_to_devonthink

STATE_FILE = "state.json"
TRANSCRIPT_CACHE = "02_transcript_cache.json"

AI_DISCLAIMER = (
    "<small><em>Este texto ha sido generado por Gemini 2.5/3.1 a partir "
    "del audio del autor. El contenido y las ideas son íntegramente del autor; "
    "la redacción ha sido asistida por IA.</em></small>\n\n"
    "---\n\n"
)

def load_state(cfg: dict) -> dict:
    state_path = get_temp_path(cfg, STATE_FILE)
    if not os.path.exists(state_path):
        print("❌ No se encontró state.json.")
        sys.exit(1)
    with open(state_path) as f:
        return json.load(f)

def call_gemini(prompt: str, system_instruction: str, cfg: dict) -> str:
    """Configura y llama a la API de Gemini."""
    gemini_cfg = cfg.get("gemini", {})
    api_key = gemini_cfg.get("api_key")
    model_name = gemini_cfg.get("model_name", "gemini-2.5-pro")
    
    if not api_key:
        print("❌ API Key de Gemini no encontrada en config.yaml")
        sys.exit(1)

    genai.configure(api_key=api_key)
    
    model = genai.GenerativeModel(
        model_name=model_name,
        system_instruction=system_instruction,
        generation_config={
            "temperature": gemini_cfg.get("temperature", 0.7),
            "response_mime_type": "application/json"
        }
    )

    try:
        response = model.generate_content(prompt)
        return response.text
    except Exception as e:
        print(f"❌ Error en la API de Gemini: {e}")
        sys.exit(1)

def build_system_prompt(cfg: dict) -> str:
    lang = cfg["content"]["output_language"]
    lang_name = "español" if lang == "es" else lang
    tone = cfg["content"]["blog_tone"]

    return f"""Eres un escritor profesional de blogs en {lang_name}.
Tu especialidad es tomar contenido hablado y convertirlo en texto escrito
de alta calidad, manteniendo la voz y personalidad del autor original.

ESTILO Y REGLAS CRÍTICAS:
- Tono {tone} y cercano.
- COBERTURA TOTAL: No omitas NI UNA SOLA idea, ejemplo, anécdota o digresión presente en el audio.
- REORGANIZACIÓN: Puedes reordenar los temas libremente para mejorar el flujo narrativo.
- Formato: Párrafos de 3-5 líneas, frases directas y uso de subtítulos (##).

FORMATO DE SALIDA:
Debes responder exclusivamente con un objeto JSON válido."""

def build_content_prompt(transcript: str, cfg: dict) -> str:
    n_keywords = cfg["content"]["num_keywords"]

    return f"""Convierte esta transcripción en una entrada de blog.

REGLA DE ORO: No te dejes nada fuera. Si el autor habla de hardware, pajaritos o anécdotas personales, inclúyelo todo pero de forma bien escrita y organizada.

TRANSCRIPCIÓN:
{transcript}

RESPONDE CON ESTE JSON:
{{
  "titulo": "Título atractivo (max 70 chars)",
  "slug": "url-slug-formato",
  "palabras_clave": ["kw1", "kw2", "kw3", "kw4", "kw5"],
  "meta_descripcion": "Descripción SEO (max 155 chars)",
  "entrada_blog": "Texto completo en Markdown con ## para subtítulos. Mínimo 400 palabras."
}}

Genera exactamente {n_keywords} palabras clave."""

def parse_llm_response(response: str) -> dict:
    try:
        return json.loads(response)
    except json.JSONDecodeError:
        # Fallback simple si el JSON viene envuelto en markdown
        cleaned = response.strip().replace("```json", "").replace("```", "")
        try:
            return json.loads(cleaned)
        except:
            print("⚠️ Error crítico parseando JSON de Gemini.")
            return {"entrada_blog": response}

def save_outputs(content: dict, base_name: str, cfg: dict):
    blog_content = AI_DISCLAIMER + content.get("entrada_blog", "")
    content["entrada_blog"] = blog_content

    json_path = get_output_path(cfg, f"{base_name}_content.json")
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(content, f, ensure_ascii=False, indent=2)

    md_path = get_output_path(cfg, f"{base_name}.md")
    md_full = f"""---
titulo: {content.get('titulo', '')}
slug: {content.get('slug', '')}
keywords: {', '.join(content.get('palabras_clave', []))}
meta: {content.get('meta_descripcion', '')}
---

# {content.get('titulo', '')}

{blog_content}
"""
    with open(md_path, "w", encoding="utf-8") as f:
        f.write(md_full)
    return json_path, md_path

def main():
    parser = argparse.ArgumentParser(description="Fase 4: Generación con Gemini API")
    parser.add_argument("--input", default=None)
    args = parser.parse_args()

    cfg = load_config()
    state = load_state(cfg)
    base_name = state["base_name"]
    
    # Carga de transcripción (Cache > File)
    cache_path = get_temp_path(cfg, TRANSCRIPT_CACHE)
    if os.path.exists(cache_path):
        with open(cache_path, "r", encoding="utf-8") as f:
            transcript = json.load(f).get("text", "")
    else:
        with open(get_output_path(cfg, f"{base_name}.txt"), "r", encoding="utf-8") as f:
            transcript = f.read()

    print(f"\n🚀 Iniciando generación con {cfg['gemini']['model_name']}")
    
    system = build_system_prompt(cfg)
    prompt = build_content_prompt(transcript, cfg)
    
    response_text = call_gemini(prompt, system, cfg)
    content = parse_llm_response(response_text)
    
    json_path, md_path = save_outputs(content, base_name, cfg)
    
    # Integración con DEVONthink
    copy_to_devonthink(md_path, cfg, dest_filename=f"{base_name} (blog).md")
    
    print(f"✅ Proceso completado. Entrada generada en: {md_path}")

if __name__ == "__main__":
    main()