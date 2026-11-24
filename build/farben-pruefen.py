#!/usr/bin/env python3
"""Feste Farbwerte in den XAML-Dateien des Repos ablehnen (#94, #96).

Jeder Farbwert gehört in eine Rolle von firestrike-branding, die Pinsel zeigen darauf. Erlaubt ist ein Hex-Wert nur
als Wert eines <Color x:Key="…"> in einem Themenwörterbuch Light oder Dark von src/Lagerfeuer/App.xaml, und nur für
Schlüssel, die der WinUI-Export von firestrike-branding selbst mit einem Hex-Wert führt: die Rollen (FsGrundColor …)
und die Stufen der Akzentreihe. Überall sonst ist ein Hex-Wert ein Fehler, ebenso ein Farbname außer Transparent.

Die Dateien werden als XML gelesen. Anführungszeichen, Leerzeichen, Zeilenumbrüche und Kommentare spielen also keine
Rolle.

    python3 build/farben-pruefen.py --export PFAD/exporte/winui/Firestrike.xaml

Exit-Code 0: keine festen Farbwerte, 1: Funde (je Fund Datei, Zeile und Wert), 2: Aufruf, Export oder eine Datei
unbrauchbar, oder App.xaml fehlt. Nur Standardbibliothek; die Dateien werden nur gelesen.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path
from xml.parsers import expat

APP = "src/Lagerfeuer/App.xaml"
THEMEN = {"Light", "Dark"}
HEX = re.compile(r"#[0-9A-Fa-f]{3,8}")
NAME = re.compile(r"[A-Za-z]+")
ERLAUBTE_NAMEN = {"Transparent"}
# Eigenschaften, deren Wert eine Farbe oder ein Pinsel ist (auch als Eigenschaftselement, etwa SolidColorBrush.Color).
FARB_EIGENSCHAFTEN = {"Color", "Background", "Foreground", "Fill", "Stroke", "BorderBrush"}
# Eigenschaften mit Text für Menschen: Dort darf „#120“ stehen, ohne eine Farbe zu sein.
TEXT_EIGENSCHAFTEN = {"Text", "Content", "Header", "PlaceholderText", "Title", "Description", "Glyph",
                      "ToolTipService.ToolTip", "AutomationProperties.Name"}


class Unbrauchbar(Exception):
    pass


def lokal(name: str) -> str:
    """Name ohne Präfix: „x:Key“ → „Key“, „controls:CoverImage“ → „CoverImage“."""
    return name.rsplit(":", 1)[-1]


def ist_farb_eigenschaft(name: str) -> bool:
    name = lokal(name).rsplit(".", 1)[-1]
    return name in FARB_EIGENSCHAFTEN or name.endswith("Color") or name.endswith("Brush")


def ist_farb_element(tag: str) -> bool:
    """<Color>, ein Eigenschaftselement wie <SolidColorBrush.Color> oder der Wert eines Setters."""
    tag = lokal(tag)
    return tag == "Color" or ("." in tag and (ist_farb_eigenschaft(tag) or tag.endswith(".Value")))


def lesen(pfad: Path, beim_start, beim_ende) -> None:
    """Liest eine XML-Datei mit expat; die Handler bekommen Stapel, Zeile und Werte."""
    try:
        daten = pfad.read_bytes()
    except OSError as fehler:
        raise Unbrauchbar(f"{pfad}: nicht lesbar ({fehler.strerror})") from fehler
    leser = expat.ParserCreate()
    stapel: list[dict] = []

    def start(tag, attribute):
        eintrag = {"tag": tag, "zeile": leser.CurrentLineNumber, "text": [],
                   "key": next((v for k, v in attribute.items() if lokal(k) == "Key"), None)}
        stapel.append(eintrag)
        beim_start(stapel, eintrag, attribute)

    def text(daten):
        if stapel:
            stapel[-1]["text"].append(daten)

    def ende(tag):
        eintrag = stapel[-1]
        beim_ende(stapel, eintrag, "".join(eintrag["text"]).strip())
        stapel.pop()

    leser.StartElementHandler = start
    leser.CharacterDataHandler = text
    leser.EndElementHandler = ende
    try:
        leser.Parse(daten, True)
    except expat.ExpatError as fehler:
        raise Unbrauchbar(f"{pfad}: kein gültiges XML ({fehler})") from fehler


def in_themenwoerterbuch(stapel: list[dict]) -> bool:
    return any(lokal(e["tag"]) == "ResourceDictionary" and e["key"] in THEMEN for e in stapel[:-1])


def schluessel_des_exports(pfad: Path) -> set[str]:
    """Die Schlüssel, die der Export in Light oder Dark mit einem Hex-Wert führt."""
    gefunden: set[str] = set()

    def ende(stapel, eintrag, text):
        if lokal(eintrag["tag"]) == "Color" and eintrag["key"] and HEX.fullmatch(text) and in_themenwoerterbuch(stapel):
            gefunden.add(eintrag["key"])

    lesen(pfad, lambda *_: None, ende)
    if not gefunden:
        raise Unbrauchbar(f"{pfad}: keine Farben in Light oder Dark, ist das der WinUI-Export?")
    return gefunden


def pruefen(pfad: Path, name: str, erlaubt: set[str]) -> tuple[list[str], int]:
    """Funde in einer Datei und die Zahl der erlaubten Rollenwerte."""
    funde: list[str] = []
    rollen = 0

    def start(stapel, eintrag, attribute):
        for k, wert in attribute.items():
            if lokal(k) in TEXT_EIGENSCHAFTEN:
                continue
            w = wert.strip()
            if HEX.fullmatch(w):
                funde.append(f"{name}:{eintrag['zeile']}: {lokal(eintrag['tag'])} {k}=\"{w}\": fester Farbwert")
            elif NAME.fullmatch(w) and ist_farb_eigenschaft(k) and w not in ERLAUBTE_NAMEN:
                funde.append(f"{name}:{eintrag['zeile']}: {lokal(eintrag['tag'])} {k}=\"{w}\": Farbname")

    def ende(stapel, eintrag, text):
        nonlocal rollen
        tag = lokal(eintrag["tag"])
        if HEX.fullmatch(text):
            if name == APP and tag == "Color" and eintrag["key"] in erlaubt and in_themenwoerterbuch(stapel):
                rollen += 1
            elif ist_farb_element(eintrag["tag"]):
                schluessel = f" x:Key=\"{eintrag['key']}\"" if eintrag["key"] else ""
                grund = ""
                if tag == "Color" and name == APP:
                    grund = (" (nur in den Wörterbüchern Light und Dark)" if eintrag["key"] in erlaubt
                             else " (kein Schlüssel des Exports)")
                funde.append(f"{name}:{eintrag['zeile']}: <{tag}{schluessel}>{text}: fester Farbwert{grund}")
        elif (NAME.fullmatch(text) and ist_farb_element(eintrag["tag"]) and not tag.endswith(".Value")
              and text not in ERLAUBTE_NAMEN):
            # Ein Setter kann auch „Collapsed“ oder „Bold“ tragen; dort zählt nur ein Hex-Wert.
            funde.append(f"{name}:{eintrag['zeile']}: <{tag}>{text}: Farbname")

    lesen(pfad, start, ende)
    return funde, rollen


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="farben-pruefen.py", description=__doc__.split("\n\n")[0])
    parser.add_argument("--export", type=Path, required=True, help="exporte/winui/Firestrike.xaml von firestrike-branding")
    args = parser.parse_args(argv)
    try:
        erlaubt = schluessel_des_exports(args.export)
        if not Path(APP).is_file():
            raise Unbrauchbar(f"{APP} fehlt (im Wurzelverzeichnis des Repos aufrufen)")
        dateien = subprocess.run(["git", "ls-files", "-z", "--", "*.xaml"], capture_output=True, check=True).stdout
        namen = [n for n in dateien.decode("utf-8").split("\0") if n]
        funde: list[str] = []
        rollen = 0
        for name in namen:
            f, r = pruefen(Path(name), name, erlaubt)
            funde += f
            rollen += r
    except Unbrauchbar as fehler:
        print(fehler, file=sys.stderr)
        return 2
    except (OSError, subprocess.CalledProcessError) as fehler:
        print(f"git ls-files: {fehler}", file=sys.stderr)
        return 2
    for f in funde:
        print(f)
    if funde:
        print(f"Feste Farbwerte: {len(funde)}. Jeder Farbwert gehört in eine Rolle, die Pinsel zeigen darauf (#94, #96).")
        return 1
    print(f"Keine festen Farbwerte: {len(namen)} XAML-Dateien, {rollen} Werte der Rollen und der Akzentreihe "
          f"({len(erlaubt)} Schlüssel im Export).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
