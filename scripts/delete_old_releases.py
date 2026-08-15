#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PISMO — удаление старых релизов на GitHub.

Удаляет все релизы репозитория, кроме перечисленных в KEEP, и вместе с ними
их git-теги (иначе теги остаются висеть во вкладке Tags).

Токен НЕ хранится в файле: скрипт спрашивает его при запуске и не показывает
при вводе. Так он не попадёт ни в историю команд, ни в сам файл.

Нужен токен с правом записи в репозиторий:
  classic      — область (scope) «repo»
  fine-grained — Contents: Read and write
Создать: https://github.com/settings/tokens/new
После работы токен лучше сразу удалить там же.

Запуск:  python delete_old_releases.py
Зависимостей нет — только стандартная библиотека.
"""

import getpass
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

# ── Что и где чистим ────────────────────────────────────────────────────
OWNER = "pismodb78-bit"
REPO = "PISMO_MESSENDGER"

# Эти релизы НЕ трогаем. Оставьте текущую версию; полезно оставить и
# предыдущую как точку отката, если в новой вылезет что-то серьёзное.
KEEP = {
    "2.8.3.2",
    # "2.8.2",
}

API = "https://api.github.com"


def request(method, url, token, expect_json=True):
    """Запрос к GitHub API. Возвращает разобранный JSON или None."""
    req = urllib.request.Request(url, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    req.add_header("User-Agent", "pismo-release-cleanup")
    with urllib.request.urlopen(req) as resp:
        if not expect_json:
            return None
        body = resp.read()
        return json.loads(body) if body else None


def list_releases(token):
    """Все релизы репозитория (с постраничной выборкой)."""
    out, page = [], 1
    while True:
        url = f"{API}/repos/{OWNER}/{REPO}/releases?per_page=100&page={page}"
        chunk = request("GET", url, token) or []
        out.extend(chunk)
        if len(chunk) < 100:
            return out
        page += 1


def delete_release(token, release):
    """Удаляет релиз и его тег. Возвращает текст результата."""
    rid, tag = release["id"], release["tag_name"]
    request("DELETE", f"{API}/repos/{OWNER}/{REPO}/releases/{rid}", token, expect_json=False)

    # Тег живёт отдельно от релиза: без этого он останется во вкладке Tags.
    safe = urllib.parse.quote(tag, safe="")
    try:
        request("DELETE", f"{API}/repos/{OWNER}/{REPO}/git/refs/tags/{safe}", token, expect_json=False)
        return "релиз и тег удалены"
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return "релиз удалён (тега уже не было)"
        return f"релиз удалён, тег НЕ удалён: HTTP {e.code}"


def main():
    print(f"Репозиторий: {OWNER}/{REPO}")
    print(f"Оставляем:   {', '.join(sorted(KEEP)) or '(ничего)'}\n")

    token = getpass.getpass("Токен GitHub (ввод не отображается): ").strip()
    if not token:
        print("Токен не введён — выходим.")
        return 1

    try:
        releases = list_releases(token)
    except urllib.error.HTTPError as e:
        if e.code == 401:
            print("Ошибка 401: токен неверный или уже отозван.")
        elif e.code == 404:
            print("Ошибка 404: репозиторий не найден или у токена нет к нему доступа.")
        else:
            print(f"Ошибка HTTP {e.code} при получении списка релизов.")
        return 1
    except urllib.error.URLError as e:
        print(f"Нет связи с GitHub: {e.reason}")
        return 1

    doomed = [r for r in releases if r["tag_name"] not in KEEP]
    kept = [r for r in releases if r["tag_name"] in KEEP]

    print(f"Всего релизов: {len(releases)}")
    print(f"Останутся ({len(kept)}): {', '.join(r['tag_name'] for r in kept) or '—'}")
    print(f"Будут удалены ({len(doomed)}):")
    for r in doomed:
        print(f"   {r['tag_name']:<12} от {r.get('published_at', '?')[:10]}")

    if not doomed:
        print("\nУдалять нечего.")
        return 0

    print("\nЭто необратимо: вместе с релизами пропадут и приложенные к ним архивы.")
    if input('Для подтверждения напишите УДАЛИТЬ: ').strip() != "УДАЛИТЬ":
        print("Отменено, ничего не тронуто.")
        return 0

    print()
    errors = 0
    for r in doomed:
        tag = r["tag_name"]
        try:
            print(f"  {tag:<12} — {delete_release(token, r)}")
        except urllib.error.HTTPError as e:
            errors += 1
            print(f"  {tag:<12} — ОШИБКА HTTP {e.code}")
        except urllib.error.URLError as e:
            errors += 1
            print(f"  {tag:<12} — ОШИБКА связи: {e.reason}")

    print(f"\nГотово. Удалено: {len(doomed) - errors} из {len(doomed)}.")
    if errors:
        print("Часть релизов удалить не удалось — можно просто запустить скрипт ещё раз.")
    print("Не забудьте отозвать токен: https://github.com/settings/tokens")
    return 0


if __name__ == "__main__":
    sys.exit(main())
