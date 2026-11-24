"""Translate the PixelForge catalog with a glossary and structural checks.

The generated JSON files are embedded resources. Google Translate is used as a
drafting engine only: glossary overrides, placeholder validation, plural rules
and manual review decide what is written.
"""
from __future__ import annotations

import html
import json
import pathlib
import re
import sys
import time
import urllib.parse
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "scripts" / "localization"
SOURCE = SCRIPTS / "source-strings.json"
PLURALS = SCRIPTS / "plural-forms.json"
GLOSSARY = SCRIPTS / "glossary.json"
OVERRIDES = SCRIPTS / "overrides.json"
REPLACEMENTS = SCRIPTS / "replacements.json"
CACHE = SCRIPTS / "cache"
RESOURCES = ROOT / "src" / "PixelForge.Presentation.WinUI" / "Localization" / "Resources"
LOCALES = ["en-US", "de-DE", "es-ES", "pt-BR", "ko-KR", "it-IT", "fr-FR", "ja-JP", "zh-CN", "ru-RU"]
MARKER = "\n@@PF_SEGMENT@@\n"
PLACEHOLDER = re.compile(r"\{\d+\}")
TECHNICAL = re.compile(
    r"^(?:PNG|JPG|JPEG|WEBP|AVIF|TIFF|GIF|ICO|BMP|JXL|SVG|MP3|WAV|FLAC|AAC|M4A|OGG|OPUS|MP4|MKV|"
    r"MOV|AVI|WEBM|HEVC|H\.264|H\.265|AV1|VP9|CPU|GPU|DIRECTML|VULKAN|ONNX RUNTIME|FFMPEG|"
    r"IMAGEMAGICK|LLAMA\.CPP|REAL-ESRGAN|WAIFU2X|RIFE|OCR|MB|KB|GB|TB|FPS|KHZ|KBPS|MS|PX|DPI|"
    r"AI|HDR|VBR|CBR|SHA-256|PP-OCR|PP-OCRV6|PIXELFORGE)$",
    re.I,
)


def load_json(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8"))


def placeholders(value: str) -> list[str]:
    return PLACEHOLDER.findall(value)


def is_preserved(value: str) -> bool:
    stripped = value.strip()
    if not stripped:
        return True
    if TECHNICAL.match(stripped):
        return True
    if re.fullmatch(r"[a-z]{2}-[A-Z]{2}", stripped):
        return True
    if re.fullmatch(r"[a-z0-9_.:-]+", stripped) and ("." in stripped or ":" in stripped):
        return True
    if stripped.startswith(("http://", "https://", "ms-appx://", "pack://")):
        return True
    if stripped.startswith(("#", "/")) or "\\" in stripped:
        return True
    return False


def translate(text: str, locale: str) -> str:
    if is_preserved(text) or not re.search(r"[A-Za-z]", text):
        return text
    cache_path = CACHE / f"{locale}.json"
    cache = load_json(cache_path) if cache_path.exists() else {}
    if text in cache:
        return cache[text]

    url = "https://translate.googleapis.com/translate_a/single?" + urllib.parse.urlencode({
        "client": "gtx", "sl": "en", "tl": locale.split("-")[0], "dt": "t", "q": text,
    })
    request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 PixelForge-Localization/1.0"})
    result = None
    for attempt in range(5):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                payload = json.loads(response.read().decode("utf-8"))
            result = "".join(segment[0] for segment in payload[0] if segment and segment[0])
            break
        except Exception as exc:  # noqa: BLE001 - network retries are intentional
            if attempt == 4:
                print(f"  ! {locale}: {exc} :: {text[:80]!r}", file=sys.stderr)
                result = text
            else:
                time.sleep(1.5 * (attempt + 1))
    cache[text] = result
    cache_path.write_text(json.dumps(cache, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    return result


def translate_batch(items: list[str], locale: str) -> list[str]:
    if not items:
        return []
    cache_path = CACHE / f"{locale}.json"
    cache = load_json(cache_path) if cache_path.exists() else {}
    pending = [item for item in items if item not in cache]
    if pending:
        # Keep request URLs comfortably below common gateway limits.
        pos = 0
        while pos < len(pending):
            batch: list[str] = []
            length = 0
            while pos < len(pending) and len(batch) < 12 and length < 2600:
                candidate = pending[pos]
                if "\n@@PF_SEGMENT@@\n" in candidate:
                    pos += 1
                    continue
                if batch and length + len(candidate) > 2600:
                    break
                batch.append(candidate)
                length += len(candidate) + len(MARKER)
                pos += 1
            if not batch:
                continue
            joined = MARKER.join(batch)
            url = "https://translate.googleapis.com/translate_a/single?" + urllib.parse.urlencode({
                "client": "gtx", "sl": "en", "tl": locale.split("-")[0], "dt": "t", "q": joined,
            })
            request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 PixelForge-Localization/1.0"})
            translated = None
            for attempt in range(5):
                try:
                    with urllib.request.urlopen(request, timeout=45) as response:
                        payload = json.loads(response.read().decode("utf-8"))
                    translated = "".join(segment[0] for segment in payload[0] if segment and segment[0])
                    break
                except Exception as exc:  # noqa: BLE001
                    if attempt == 4:
                        print(f"  ! batch {locale}: {exc}", file=sys.stderr)
                        translated = None
                    else:
                        time.sleep(1.5 * (attempt + 1))
            if translated is None or translated.count("@@PF_SEGMENT@@") != len(batch) - 1:
                for item in batch:
                    cache[item] = translate(item, locale)
            else:
                pieces = [piece.strip() for piece in translated.split("@@PF_SEGMENT@@")]
                if len(pieces) != len(batch):
                    for item in batch:
                        cache[item] = translate(item, locale)
                else:
                    for source, target in zip(batch, pieces):
                        cache[source] = target
            cache_path.write_text(json.dumps(cache, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")
            time.sleep(0.12)
    if pending:
        cache = load_json(cache_path)
    return [cache[item] for item in items]


def apply_glossary(source: str, target: str, locale: str, glossary: dict) -> str:
    exact = glossary.get(locale, {}).get(source)
    if exact:
        return exact
    return target


def validate(source: str, target: str) -> str:
    if not target or not target.strip():
        return source
    source_placeholders = sorted(placeholders(source))
    target_placeholders = sorted(placeholders(target))
    if source_placeholders != target_placeholders:
        return source
    return target


def main() -> None:
    source_doc = load_json(SOURCE)
    strings = [item for item in source_doc["strings"] if isinstance(item, str) and item.strip()]
    # Deduplicate while preserving the sorted source order.
    strings = sorted(set(strings), key=lambda value: (value.casefold(), value))
    plural_sources = load_json(PLURALS)
    glossary = load_json(GLOSSARY)
    overrides = load_json(OVERRIDES)
    replacements = load_json(REPLACEMENTS)
    CACHE.mkdir(parents=True, exist_ok=True)
    RESOURCES.mkdir(parents=True, exist_ok=True)

    english: dict[str, str] = {value: value for value in strings}
    for key, forms in plural_sources.items():
        for category, template in forms.items():
            english[f"{key}.{category}"] = template
    (RESOURCES / "en-US.json").write_text(
        json.dumps(english, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    print(f"en-US: {len(english)} entries")

    targets = [arg for arg in sys.argv[1:] if arg in LOCALES] or LOCALES[1:]
    for locale in targets:
        print(f"translating {locale} ({len(strings)} strings + plurals)")
        translated = translate_batch(strings, locale)
        catalog: dict[str, str] = {}
        for source, target in zip(strings, translated):
            catalog[source] = validate(source, apply_glossary(source, target, locale, glossary))

        # Plural forms: translate the English one/other templates, then keep the
        # categories the target locale actually uses.
        for key, forms in plural_sources.items():
            for category in ("one", "other"):
                if category not in forms:
                    continue
                source = forms[category]
                target = translate(source, locale)
                catalog[f"{key}.{category}"] = validate(source, target)
        if locale == "ru-RU":
            russian = {
                "Count.File": ("{0} файл", "{0} файла", "{0} файлов"),
                "Count.Category": ("{0} категория", "{0} категории", "{0} категорий"),
                "Count.Image": ("{0} изображение", "{0} изображения", "{0} изображений"),
                "Count.Audio": ("{0} аудиофайл", "{0} аудиофайла", "{0} аудиофайлов"),
                "Count.Video": ("{0} видео", "{0} видео", "{0} видео"),
                "Count.Document": ("{0} документ", "{0} документа", "{0} документов"),
                "Count.Ebook": ("{0} электронная книга", "{0} электронные книги", "{0} электронных книг"),
                "Count.Archive": ("{0} архив", "{0} архива", "{0} архивов"),
                "Count.Page": ("{0} страница", "{0} страницы", "{0} страниц"),
                "Count.Item": ("{0} элемент", "{0} элемента", "{0} элементов"),
                "Count.Rule": ("{0} правило", "{0} правила", "{0} правил"),
                "Count.WatchRule": ("{0} правило наблюдения", "{0} правила наблюдения", "{0} правил наблюдения"),
                "Count.RecordedOperation": ("{0} записанная операция", "{0} записанные операции", "{0} записанных операций"),
                "Count.Failed": ("{0} неудачный элемент", "{0} неудачных элемента", "{0} неудачных элементов"),
                "Count.Job": ("{0} задание", "{0} задания", "{0} заданий"),
                "Relative.MinutesAgo": ("{0} мин назад", "{0} мин назад", "{0} мин назад"),
                "Relative.HoursAgo": ("{0} ч назад", "{0} ч назад", "{0} ч назад"),
                "Relative.DaysAgo": ("{0} дн. назад", "{0} дн. назад", "{0} дн. назад"),
                "Status.JobsActive": ("Активное задание: {0}", "Активных задания: {0}", "Активных заданий: {0}"),
                "Status.JobsCompact": ("{0} задание", "{0} задания", "{0} заданий"),
                "Status.ProcessingLocally": ("{0}% · обработка локально", "{0}% · обработка локально", "{0}% · обработка локально"),
                "Scan.CheckedFiles": ("проверен {0} файл", "проверено {0} файла", "проверено {0} файлов"),
                "Upscaler.Inspecting": ("Проверка изображения: {0}…", "Проверка изображений: {0}…", "Проверка изображений: {0}…"),
                "Upscaler.SkippedFiles": ("Не удалось увеличить {0} файл", "Не удалось увеличить {0} файла", "Не удалось увеличить {0} файлов"),
            }
            for key, (one, few, many) in russian.items():
                catalog[f"{key}.one"] = one
                catalog[f"{key}.few"] = few
                catalog[f"{key}.many"] = many
                catalog[f"{key}.other"] = many

        # Exact glossary corrections are applied after machine translation.
        for source in list(catalog):
            if source in glossary.get(locale, {}):
                catalog[source] = glossary[locale][source]

        # Reviewed overrides for UI copy where machine translation is ambiguous
        # (for example "upscale" as a verb rather than an adjective).
        for source, target in overrides.get(locale, {}).items():
            if source in catalog:
                catalog[source] = target

        if "OK" in catalog:
            catalog["OK"] = "OK"

        for source in list(catalog):
            value = catalog[source]
            for old, new in replacements.get(locale, {}).items():
                if old.isascii() and re.fullmatch(r"[\w .'()-]+", old):
                    value = re.sub(r"(?<!\w)" + re.escape(old) + r"(?!\w)", new, value)
                else:
                    value = value.replace(old, new)
            catalog[source] = value

        path = RESOURCES / f"{locale}.json"
        path.write_text(json.dumps(catalog, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")
        print(f"  wrote {len(catalog)} entries to {path.name}")


if __name__ == "__main__":
    main()
