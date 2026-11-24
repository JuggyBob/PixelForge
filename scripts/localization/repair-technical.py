"""Repair translations where Google translated a product/engine token.

The normal translation path is allowed to use machine translation for prose,
but brand and engine names must remain invariant. This script re-translates only
the affected entries with explicit <span class="notranslate"> guards.
"""
from __future__ import annotations

import html
import json
import pathlib
import re
import time
import urllib.parse
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
RESOURCES = ROOT / "src" / "PixelForge.Presentation.WinUI" / "Localization" / "Resources"
CACHE = ROOT / "scripts" / "localization" / "cache" / "technical.json"
LOCALES = ["de-DE", "es-ES", "pt-BR", "ko-KR", "it-IT", "fr-FR", "ja-JP", "zh-CN", "ru-RU"]
TERMS = ["PixelForge", "ONNX Runtime", "ImageMagick", "Real-ESRGAN", "Waifu2x",
         "llama.cpp", "DirectML", "FFmpeg", "Vulkan", "PP-OCR", "SHA-256",
         "RIFE", "CPU", "GPU"]
PLACEHOLDER = re.compile(r"\{\d+\}")
TAG = re.compile(r"</?span[^>]*>", re.I)


def load(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8"))


def save(path: pathlib.Path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")


def protect(value: str) -> str:
    for term in sorted(TERMS, key=len, reverse=True):
        value = value.replace(term, f'<span class="notranslate">{term}</span>')
    return value


def translate(value: str, locale: str, cache: dict) -> str:
    key = locale + "\u0000" + value
    if key in cache:
        return cache[key]
    url = "https://translate.googleapis.com/translate_a/single?" + urllib.parse.urlencode({
        "client": "gtx", "sl": "en", "tl": locale.split("-")[0], "dt": "t",
        "q": protect(value),
    })
    request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 PixelForge-Localization/1.0"})
    result = value
    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=40) as response:
                payload = json.loads(response.read().decode("utf-8"))
            result = "".join(segment[0] for segment in payload[0] if segment and segment[0])
            break
        except Exception as exc:  # noqa: BLE001
            if attempt == 3:
                print(f"! {locale}: {exc} :: {value[:80]!r}")
            else:
                time.sleep(1.5 * (attempt + 1))
    result = html.unescape(TAG.sub("", result))
    cache[key] = result
    return result


def main() -> None:
    cache = load(CACHE) if CACHE.exists() else {}
    for locale in LOCALES:
        path = RESOURCES / f"{locale}.json"
        catalog = load(path)
        repaired = 0
        for source, target in list(catalog.items()):
            if not any(term.lower() in source.lower() for term in TERMS):
                continue
            missing = [term for term in TERMS
                       if term.lower() in source.lower() and term.lower() not in target.lower()]
            if not missing:
                continue
            candidate = translate(source, locale, cache)
            if not all(term.lower() in candidate.lower() for term in TERMS if term.lower() in source.lower()):
                continue
            if sorted(PLACEHOLDER.findall(source)) != sorted(PLACEHOLDER.findall(candidate)):
                continue
            catalog[source] = candidate
            repaired += 1
        save(path, catalog)
        CACHE.parent.mkdir(parents=True, exist_ok=True)
        save(CACHE, cache)
        print(f"{locale}: repaired {repaired}")


if __name__ == "__main__":
    main()
