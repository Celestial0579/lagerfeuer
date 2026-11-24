#!/usr/bin/env python3
"""Feste Farbwerte in den XAML-Dateien des Repos ablehnen (#94, #96).

Jeder Farbwert gehört in eine Rolle von firestrike-branding, die Pinsel zeigen darauf. Erlaubt ist ein Hex-Wert nur
als Wert eines <Color x:Key="…"> in einem Themenwörterbuch Light oder Dark von src/Lagerfeuer/App.xaml, und nur für
Schlüssel, die der WinUI-Export von firestrike-branding selbst mit einem Hex-Wert führt: die Rollen (FsGrundColor …)
und die Stufen der Akzentreihe. Überall sonst ist ein Hex-Wert ein Fehler, ebenso ein Farbname außer Transparent.

Die Dateien werden als XML gelesen. Anführungszeichen, Leerzeichen, Zeilenumbrüche und Kommentare spielen also keine
Rolle.

Dazu das Kontrastdesign von App.xaml: Das Wörterbuch HighContrast muss es geben, jede Rolle aus Light und Dark zeigt
dort auf eine Systemfarbe, und jeder eigene Pinsel (Lf…Brush) per ThemeResource auf dieselbe Systemfarbe wie seine
Rolle. Ob die Systemfarbe je Rolle die des Exports ist, prüft `marke.py abgleichen`.

Und Hell und Dunkel vollständig (#104): Light und Dark führen dieselben Schlüssel, beide die ganze Akzentreihe des
Exports, und jeder Verweis auf eine Rolle zeigt auf eine Rolle im selben Wörterbuch.

    python3 build/farben-pruefen.py --export PFAD/exporte/winui/Firestrike.xaml

Exit-Code 0: keine festen Farbwerte, 1: Funde (je Fund Datei, Zeile und Wert), 2: Aufruf, Export oder eine Datei
unbrauchbar, oder App.xaml fehlt, 3: Fehler in der Prüfung selbst (mit Traceback), damit ein Absturz nie wie ein Fund
aussieht. Nur Standardbibliothek; die Dateien werden nur gelesen.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
import traceback
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
    """Color, Background … oder ein Name, der so endet, etwa PlaceholderForeground (#99). Klammern um angehängte
    Eigenschaften wie „(TextBlock.Foreground)“ zählen nicht."""
    name = lokal(name.strip().strip("()")).rsplit(".", 1)[-1]
    return any(name.endswith(e) for e in FARB_EIGENSCHAFTEN | {"Brush"})


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
        eintrag = {"tag": tag, "zeile": leser.CurrentLineNumber, "text": [], "attribute": attribute,
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


def setter_farbe(eintrag: dict) -> bool:
    """Ist das Element ein Setter für eine Farb-Eigenschaft? Im Stil Property="Foreground", im VisualState
    Target="Titel.Foreground" (#99)."""
    attribute = eintrag["attribute"]
    ziel = attribute.get("Property") or attribute.get("Target", "")
    return lokal(eintrag["tag"]) == "Setter" and ist_farb_eigenschaft(ziel)


def animation_farbe(stapel: list[dict]) -> bool:
    """
    Animiert das Schlüsselbild am Ende des Stapels eine Farb-Eigenschaft (Storyboard.TargetProperty="Foreground" …)?
    Es zählt der nächste Vorfahr mit Storyboard.TargetProperty, meist die Animation selbst. Steht der Wert nur weiter
    oben, etwa am Storyboard oder über einem Element <….KeyFrames>, zählt er auch (#102); so bleibt die Prüfung
    auf der sicheren Seite.
    """
    for vorfahr in reversed(stapel[:-1]):
        ziel = vorfahr["attribute"].get("Storyboard.TargetProperty", "").strip()
        if ziel:
            return ist_farb_eigenschaft(ziel)
    return False


def farbwert_attribute(stapel: list[dict], eintrag: dict) -> set[str]:
    """
    Attribute, die bei diesem Element eine Farbe tragen, obwohl ihr Name es nicht sagt: Value eines Setters für eine
    Farb-Eigenschaft, To/From/By einer ColorAnimation, Value eines Farb-Schlüsselbilds oder eines Schlüsselbilds in
    einer Animation einer Farb-Eigenschaft (#99). Der Stapel endet mit dem Element.
    """
    tag = lokal(eintrag["tag"])
    if tag == "Setter":
        return {"Value"} if setter_farbe(eintrag) else set()
    if tag.endswith("ColorAnimation"):
        return {"To", "From", "By"}
    if tag.endswith("ColorKeyFrame"):
        return {"Value"}
    if tag.endswith("KeyFrame") and animation_farbe(stapel):
        return {"Value"}
    return set()


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
            elif NAME.fullmatch(w) and w not in ERLAUBTE_NAMEN and (
                    ist_farb_eigenschaft(k) or lokal(k) in farbwert_attribute(stapel, eintrag)):
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
        elif NAME.fullmatch(text) and text not in ERLAUBTE_NAMEN and ist_farb_element(eintrag["tag"]) and (
                not tag.endswith(".Value") or (len(stapel) >= 2 and "Value" in farbwert_attribute(stapel[:-1], stapel[-2]))):
            # Ein Setter oder Schlüsselbild kann auch „Collapsed“ oder „Bold“ tragen; ein Name zählt nur bei einer
            # Farb-Eigenschaft (#99).
            funde.append(f"{name}:{eintrag['zeile']}: <{tag}>{text}: Farbname")

    lesen(pfad, start, ende)
    return funde, rollen


VERWEIS = re.compile(r"\{\s*(StaticResource|ThemeResource)\s+(?:ResourceKey\s*=\s*)?([A-Za-z0-9_.]+)\s*\}")


def kontrast_pruefen(pfad: Path, erlaubt: set[str]) -> tuple[list[str], int, int]:
    """
    Das Kontrastdesign von App.xaml (#93, #96): Jede Rolle, die Light oder Dark mit einem Wert führen, braucht im
    Wörterbuch HighContrast einen Verweis auf eine Systemfarbe. Jeder eigene Pinsel (Lf…Brush), der in Light oder Dark
    auf eine Rolle zeigt, muss dort per ThemeResource auf dieselbe Systemfarbe zeigen wie diese Rolle; sonst folgte er
    einem Wechsel des Kontrastdesigns nicht, oder ein Nachschlagen schlüge im Kontrastdesign fehl.
    """
    woerter: dict[str, dict[str, dict[str, tuple[str, int]]]] = {}

    def start(stapel, eintrag, attribute):
        if len(stapel) < 2:
            return
        eltern = stapel[-2]
        if lokal(eltern["tag"]) != "ResourceDictionary" or eltern["key"] not in THEMEN | {"HighContrast"}:
            return
        wort = woerter.setdefault(eltern["key"], {"Color": {}, "SolidColorBrush": {}, "StaticResource": {}})
        tag = lokal(eintrag["tag"])
        if tag == "SolidColorBrush" and eintrag["key"]:
            wort["SolidColorBrush"][eintrag["key"]] = (attribute.get("Color", "").strip(), eintrag["zeile"])
        elif tag == "StaticResource" and eintrag["key"]:
            wort["StaticResource"][eintrag["key"]] = (attribute.get("ResourceKey", "").strip(), eintrag["zeile"])

    def ende(stapel, eintrag, text):
        if len(stapel) >= 2 and lokal(eintrag["tag"]) == "Color" and eintrag["key"]:
            eltern = stapel[-2]
            if lokal(eltern["tag"]) == "ResourceDictionary" and eltern["key"] in THEMEN | {"HighContrast"}:
                woerter.setdefault(eltern["key"], {"Color": {}, "SolidColorBrush": {}, "StaticResource": {}})[
                    "Color"][eintrag["key"]] = (text, eintrag["zeile"])

    lesen(pfad, start, ende)
    name = APP
    if not THEMEN & woerter.keys():
        return [], 0, 0
    if "HighContrast" not in woerter:
        return [f"{name}: Wörterbuch HighContrast fehlt; im Kontrastdesign blieben die eigenen Farben stehen (#93)"], 0, 0
    kontrast = woerter["HighContrast"]
    funde: list[str] = []
    rollen = sorted({k for t in THEMEN & woerter.keys() for k in woerter[t]["Color"] if k in erlaubt and k.startswith("Fs")})
    systemfarbe: dict[str, str] = {}
    for rolle in rollen:
        ziel = kontrast["StaticResource"].get(rolle)
        if ziel and ziel[0].startswith("SystemColor"):
            systemfarbe[rolle] = ziel[0]
        else:
            funde.append(f"{name}: Rolle {rolle} hat im Wörterbuch HighContrast keinen Verweis auf eine Systemfarbe")
    pinsel = 0
    for thema in sorted(THEMEN & woerter.keys()):
        for schluessel, (wert, zeile) in sorted(woerter[thema]["SolidColorBrush"].items()):
            if not schluessel.startswith("Lf"):
                continue
            if thema != "Light" and schluessel in woerter.get("Light", {}).get("SolidColorBrush", {}):
                continue  # schon über Light geprüft
            treffer = VERWEIS.fullmatch(wert)
            if treffer and treffer.group(2) in rollen and treffer.group(2) not in systemfarbe:
                continue  # Rolle ohne Systemfarbe, schon gemeldet
            pinsel += 1
            im_kontrast = kontrast["SolidColorBrush"].get(schluessel)
            if not treffer or treffer.group(2) not in systemfarbe:
                # Ohne Rolle gibt es keine Systemfarbe zum Vergleich; fehlen darf der Pinsel trotzdem nicht (#99).
                if im_kontrast is None:
                    funde.append(f"{name}:{zeile}: Pinsel {schluessel} fehlt im Wörterbuch HighContrast")
                continue
            soll = f"{{ThemeResource {systemfarbe[treffer.group(2)]}}}"
            if im_kontrast is None:
                funde.append(f"{name}:{zeile}: Pinsel {schluessel} fehlt im Wörterbuch HighContrast (soll Color=\"{soll}\")")
                continue
            ist = VERWEIS.fullmatch(im_kontrast[0])
            if not ist or ist.group(1) != "ThemeResource" or ist.group(2) != systemfarbe[treffer.group(2)]:
                funde.append(f"{name}:{im_kontrast[1]}: Pinsel {schluessel} im Wörterbuch HighContrast: "
                             f"Color=\"{im_kontrast[0]}\", soll \"{soll}\" wie die Rolle {treffer.group(2)}")
    return funde, len(rollen), pinsel


def themen_pruefen(pfad: Path, erlaubt: set[str]) -> tuple[list[str], int]:
    """
    Hell und Dunkel vollständig (#104): Light und Dark von App.xaml führen dieselben Schlüssel, und beide alle Stufen
    der Akzentreihe aus dem Export. Jeder Verweis auf eine Rolle (Fs…) in Light oder Dark zeigt auf eine Rolle
    desselben Wörterbuchs. Sonst gälte in einem Design die Akzentfarbe von Windows statt Glut, oder ein Pinsel zeigte
    auf eine Rolle, die es dort nicht gibt. Gibt die Funde und die Zahl der Schlüssel zurück, die beide Wörterbücher
    führen.
    """
    zeile_von: dict[str, int] = {}
    schluessel: dict[str, dict[str, int]] = {}
    verweise: list[tuple[str, str, int, str]] = []

    def thema_von(stapel: list[dict]) -> str | None:
        """Das nächste umgebende Themenwörterbuch, HighContrast eingeschlossen."""
        return next((e["key"] for e in reversed(stapel[:-1])
                     if lokal(e["tag"]) == "ResourceDictionary" and e["key"] in THEMEN | {"HighContrast"}), None)

    def start(stapel, eintrag, attribute):
        tag = lokal(eintrag["tag"])
        if tag == "ResourceDictionary" and eintrag["key"] in THEMEN:
            zeile_von.setdefault(eintrag["key"], eintrag["zeile"])
            schluessel.setdefault(eintrag["key"], {})
            return
        thema = thema_von(stapel)
        if thema not in THEMEN:
            return
        eltern = stapel[-2]
        if lokal(eltern["tag"]) == "ResourceDictionary" and eltern["key"] == thema and eintrag["key"]:
            schluessel[thema].setdefault(eintrag["key"], eintrag["zeile"])
        ziele = [t.group(2) for wert in attribute.values() if (t := VERWEIS.fullmatch(wert.strip()))]
        if tag in {"StaticResource", "ThemeResource"} and attribute.get("ResourceKey", "").strip():
            ziele.append(attribute["ResourceKey"].strip())
        element = f"{tag} {eintrag['key']}" if eintrag["key"] else tag
        verweise.extend((thema, ziel, eintrag["zeile"], element) for ziel in ziele if ziel.startswith("Fs"))

    lesen(pfad, start, lambda *_: None)
    name = APP
    funde = [f"{name}: Wörterbuch {thema} fehlt, das Design hätte keine eigenen Farben"
             for thema in sorted(THEMEN - schluessel.keys())]
    akzent = sorted(k for k in erlaubt if k.startswith("SystemAccentColor"))
    if THEMEN <= schluessel.keys():
        hell, dunkel = schluessel["Light"], schluessel["Dark"]
        for k in sorted((hell.keys() ^ dunkel.keys()) - set(akzent)):
            mit, ohne = ("Light", "Dark") if k in hell else ("Dark", "Light")
            funde.append(f"{name}:{zeile_von[ohne]}: Schlüssel {k} fehlt im Wörterbuch {ohne} "
                         f"(in {mit} Zeile {schluessel[mit][k]})")
    for thema in sorted(THEMEN & schluessel.keys()):
        fehlt = [k for k in akzent if k not in schluessel[thema]]
        if fehlt:
            funde.append(f"{name}:{zeile_von[thema]}: Akzentreihe im Wörterbuch {thema} unvollständig, "
                         f"fehlt: {', '.join(fehlt)}")
    for thema, rolle, zeile, element in verweise:
        if rolle not in schluessel[thema]:
            funde.append(f"{name}:{zeile}: {element} im Wörterbuch {thema} verweist auf {rolle}, die Rolle fehlt dort")
    return funde, len(schluessel.get("Light", {}).keys() & schluessel.get("Dark", {}).keys())


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="farben-pruefen.py", description=__doc__.split("\n\n")[0])
    parser.add_argument("--export", type=Path, required=True, help="exporte/winui/Firestrike.xaml von firestrike-branding")
    parser.add_argument("--wurzel", type=Path, help="statt des Git-Repos alle *.xaml unter diesem Ordner (Prüffälle)")
    args = parser.parse_args(argv)
    wurzel = args.wurzel or Path(".")
    try:
        erlaubt = schluessel_des_exports(args.export)
        if not (wurzel / APP).is_file():
            raise Unbrauchbar(f"{APP} fehlt (im Wurzelverzeichnis des Repos aufrufen)")
        if args.wurzel:
            namen = sorted(p.relative_to(wurzel).as_posix() for p in wurzel.rglob("*.xaml")
                           if not {"bin", "obj", ".git"} & set(p.relative_to(wurzel).parts))
        else:
            dateien = subprocess.run(["git", "ls-files", "-z", "--", "*.xaml"], capture_output=True, check=True).stdout
            namen = [n for n in dateien.decode("utf-8").split("\0") if n]
        funde: list[str] = []
        rollen = 0
        for name in namen:
            f, r = pruefen(wurzel / name, name, erlaubt)
            funde += f
            rollen += r
        f, kontrast_rollen, kontrast_pinsel = kontrast_pruefen(wurzel / APP, erlaubt)
        funde += f
        f, beide = themen_pruefen(wurzel / APP, erlaubt)
        funde += f
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
          f"({len(erlaubt)} Schlüssel im Export). Kontrastdesign: {kontrast_rollen} Rollen und {kontrast_pinsel} "
          f"Pinsel auf Systemfarben. Light und Dark: je {beide} Schlüssel, Akzentreihe vollständig.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception:  # noqa: BLE001 – ein Fehler der Prüfung selbst darf nie wie ein Fund (1) aussehen
        traceback.print_exc()
        print("Fehler in der Prüfung selbst, kein Ergebnis (Exit-Code 3).", file=sys.stderr)
        sys.exit(3)
