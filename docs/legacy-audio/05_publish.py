#!/usr/bin/env python3
"""
FASE 5 - PUBLICACION EN WORDPRESS
Sube la entrada de blog a WordPress via REST API.
Usa la ruta ?rest_route= en lugar de /wp-json/ porque nginx
no tiene configurado el rewrite para URLs bonitas de la API.

Uso:
    python 05_publish.py
    python 05_publish.py --draft     # Fuerza borrador
    python 05_publish.py --publish   # Publica directamente
"""

import sys
import os
import json
import argparse
import requests
from base64 import b64encode

from utils import load_config, get_output_path, get_temp_path

STATE_FILE = "state.json"


def load_state(cfg: dict) -> dict:
    state_path = get_temp_path(cfg, STATE_FILE)
    if not os.path.exists(state_path):
        print("❌ No se encontro state.json. Ejecuta primero 01_denoise.py")
        sys.exit(1)
    with open(state_path) as f:
        return json.load(f)


def get_auth_header(cfg: dict) -> dict:
    username = cfg["wordpress"]["username"]
    password = cfg["wordpress"]["app_password"].replace(" ", "")
    token = b64encode(f"{username}:{password}".encode()).decode()
    return {
        "Authorization": f"Basic {token}",
        "Content-Type": "application/json",
        "User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    }


def api_url(base_url: str, route: str) -> str:
    """Construye la URL de la API usando index.php?rest_route= ."""
    return f"{base_url}/index.php?rest_route=/wp/v2/{route}"


def test_connection(base_url: str, headers: dict):
    url = f"{base_url}/index.php?rest_route=/wp/v2/users/me"
    try:
        r = requests.get(url, headers=headers, timeout=15)
        if r.status_code == 200:
            user = r.json()
            print(f"  ✅ Conectado como: {user.get('name', 'desconocido')}")
            return True
        elif r.status_code == 401:
            print("  ❌ Credenciales incorrectas.")
            print("     Revisa username y app_password en config.yaml")
            sys.exit(1)
        else:
            print(f"  ❌ Error de conexion: HTTP {r.status_code}")
            print(f"     {r.text[:300]}")
            sys.exit(1)
    except requests.exceptions.ConnectionError:
        print(f"  ❌ No se puede conectar a {base_url}")
        sys.exit(1)


def get_or_create_category(name: str, base_url: str, headers: dict) -> int:
    r = requests.get(
        api_url(base_url, "categories"),
        params={"search": name, "per_page": 10},
        headers=headers,
        timeout=15
    )
    results = r.json() if r.ok else []
    match = next((c for c in results if c["name"].lower() == name.lower()), None)
    if match:
        print(f"  📁 Categoria: {name} (ID: {match['id']})")
        return match["id"]

    r = requests.post(
        api_url(base_url, "categories"),
        json={"name": name},
        headers=headers,
        timeout=15
    )
    if r.ok:
        cat_id = r.json()["id"]
        print(f"  📁 Categoria creada: {name} (ID: {cat_id})")
        return cat_id

    print(f"  ⚠️  No se pudo crear la categoria '{name}', usando la por defecto")
    return 1


def get_or_create_tags(tag_names: list, base_url: str, headers: dict) -> list:
    tag_ids = []
    for name in tag_names:
        name = name.strip()
        if not name:
            continue
        r = requests.get(
            api_url(base_url, "tags"),
            params={"search": name, "per_page": 10},
            headers=headers,
            timeout=15
        )
        results = r.json() if r.ok else []
        match = next((t for t in results if t["name"].lower() == name.lower()), None)
        if match:
            tag_ids.append(match["id"])
        else:
            r = requests.post(
                api_url(base_url, "tags"),
                json={"name": name},
                headers=headers,
                timeout=15
            )
            if r.ok:
                tag_ids.append(r.json()["id"])
                print(f"  🏷️  Tag creado: {name}")
    return tag_ids


def markdown_to_html(md_text: str) -> str:
    try:
        import markdown
        return markdown.markdown(md_text, extensions=["extra", "nl2br"])
    except ImportError:
        import re
        html = md_text
        html = re.sub(r"^### (.+)$", r"<h3>\1</h3>", html, flags=re.MULTILINE)
        html = re.sub(r"^## (.+)$",  r"<h2>\1</h2>", html, flags=re.MULTILINE)
        html = re.sub(r"^# (.+)$",   r"<h1>\1</h1>", html, flags=re.MULTILINE)
        html = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", html)
        html = re.sub(r"\*(.+?)\*",     r"<em>\1</em>", html)
        html = re.sub(r"^---$", r"<hr>", html, flags=re.MULTILINE)
        paragraphs = html.split("\n\n")
        html = "\n".join(
            p.strip() if p.strip().startswith("<h") or p.strip().startswith("<hr")
            else f"<p>{p.strip()}</p>"
            for p in paragraphs if p.strip()
        )
        return html


def publish_post(content: dict, cfg: dict, status: str) -> dict:
    base_url = cfg["wordpress"]["url"].rstrip("/")
    headers = get_auth_header(cfg)
    category_name = cfg["wordpress"]["default_category"]

    print(f"  🔗 Conectando con {base_url}...")
    test_connection(base_url, headers)

    cat_id = get_or_create_category(category_name, base_url, headers)

    keywords = content.get("palabras_clave", [])
    tag_ids = get_or_create_tags(keywords, base_url, headers) if keywords else []

    blog_md = content.get("entrada_blog", "")
    blog_html = markdown_to_html(blog_md)

    post_data = {
        "title":      content.get("titulo", "Sin titulo"),
        "content":    blog_html,
        "excerpt":    content.get("meta_descripcion", ""),
        "status":     status,
        "slug":       content.get("slug", ""),
        "tags":       tag_ids,
        "categories": [cat_id],
    }

    print(f"  📤 Publicando como '{status}'...")
    r = requests.post(
        api_url(base_url, "posts"),
        json=post_data,
        headers=headers,
        timeout=30
    )

    if not r.ok:
        print(f"  ❌ Error al crear el post: HTTP {r.status_code}")
        print(f"     {r.text[:500]}")
        sys.exit(1)

    return r.json()


def main():
    parser = argparse.ArgumentParser(description="Fase 5: Publicacion en WordPress")
    parser.add_argument("--draft",   action="store_true", help="Fuerza borrador")
    parser.add_argument("--publish", action="store_true", help="Publica directamente")
    args = parser.parse_args()

    cfg = load_config()
    state = load_state(cfg)
    base_name = state["base_name"]

    wp_url = cfg["wordpress"].get("url", "")
    if not wp_url or "tu-blog" in wp_url:
        print("❌ Configura la URL de WordPress en config.yaml")
        sys.exit(1)

    if args.draft:
        status = "draft"
    elif args.publish:
        status = "publish"
    else:
        status = cfg["wordpress"].get("post_status", "draft")

    content_path = get_output_path(cfg, f"{base_name}_content.json")
    if not os.path.exists(content_path):
        print(f"❌ No se encontro {content_path}")
        print("   Ejecuta primero 04_content.py")
        sys.exit(1)

    with open(content_path, "r", encoding="utf-8") as f:
        content = json.load(f)

    print(f"\n🌐 Fase 5: Publicacion en WordPress")
    print(f"   Blog    : {wp_url}")
    print(f"   Titulo  : {content.get('titulo', 'N/A')}")
    print(f"   Estado  : {status}")
    print(f"   Cat.    : {cfg['wordpress']['default_category']}")
    print()

    result = publish_post(content, cfg, status)

    post_id     = result.get("id", "N/A")
    post_url    = result.get("link", "N/A")
    post_status = result.get("status", "N/A")

    ref_path = get_output_path(cfg, f"{base_name}_wordpress.json")
    with open(ref_path, "w", encoding="utf-8") as f:
        json.dump({"id": post_id, "url": post_url, "status": post_status}, f, indent=2)

    print(f"\n✅ Fase 5 completada")
    print(f"   🆔 Post ID : {post_id}")
    print(f"   📊 Estado  : {post_status}")
    print(f"   🔗 URL     : {post_url}\n")


if __name__ == "__main__":
    main()
