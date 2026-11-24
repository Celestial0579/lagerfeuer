#!/usr/bin/env python3
"""Prüffälle für build/farben-pruefen.py (#99).

Jeder Fall nimmt die XAML-Dateien des Repos, ändert eine Stelle und erwartet einen Exit-Code. So bleibt die Prüfung
nicht still schwächer, wenn sie später geändert wird. Die Fälle setzen auf den heutigen Dateien auf: Passt eine
Fundstelle nicht mehr, ist der Fall veraltet und schlägt fehl, statt nichts zu prüfen.

    python3 tests/farben/pruefe.py --export PFAD/exporte/winui/Firestrike.xaml

Aus dem Wurzelverzeichnis des Repos aufrufen. Exit-Code 0: alle Fälle wie erwartet, 1 sonst. Nur Standardbibliothek.
"""
from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

APP = "src/Lagerfeuer/App.xaml"
BUCH = "src/Lagerfeuer/Views/BookPage.xaml"
PRUEFUNG = Path("build/farben-pruefen.py")
LIGHT = '<ResourceDictionary x:Key="Light">'
HC = '<ResourceDictionary x:Key="HighContrast">'
STIL = '<Style x:Key="LfTitleStyle"'
GRUND = '<Color x:Key="FsGrundColor">#EFF0ED</Color>'
GLUT_HC = '<StaticResource x:Key="FsGlutColor" ResourceKey="SystemColorHotlightColor" />'
FUNKE_HC = '<StaticResource x:Key="FsFunkeColor" ResourceKey="SystemColorHighlightColor" />'
EMBER_HC = '<SolidColorBrush x:Key="LfEmberBrush" Color="{ThemeResource SystemColorHotlightColor}" />'
SPARK_HC = '<SolidColorBrush x:Key="LfSparkBrush" Color="{ThemeResource SystemColorHighlightColor}" />'
TRANSPARENT = 'Background="Transparent"'

# Name, erwarteter Exit-Code, je Datei eine Liste von Änderungen: (alt, neu) ersetzt das erste Vorkommen,
# ("re:Muster", neu) einen regulären Ausdruck (alle Vorkommen), None löscht die Datei.
FAELLE: list[tuple[str, int, dict]] = [
    ("heutige Dateien", 0, {}),
    ("Pinsel mit festen Werten wie vor #68", 1, {APP: [(r're:Color="\{StaticResource Fs[A-Za-z]+Color\}"', 'Color="#123456"')]}),
    ("einfache Anführungszeichen", 1, {APP: [(LIGHT, LIGHT + "<SolidColorBrush x:Key='X1' Color='#FF0000' />")]}),
    ("Leerzeichen um =", 1, {APP: [(LIGHT, LIGHT + '<SolidColorBrush x:Key="X2" Color = "#FF0000" />')]}),
    ("Wert in eigener Zeile", 1, {APP: [(LIGHT, LIGHT + '<SolidColorBrush x:Key="X3">\n<SolidColorBrush.Color>\n  #FF0000\n'
                                                       '</SolidColorBrush.Color>\n</SolidColorBrush>')]}),
    ("zwei Elemente in einer Zeile", 1, {APP: [(GRUND, GRUND + '<SolidColorBrush x:Key="X4" Color="#FF0000" />')]}),
    ("ausgedachte Rolle", 1, {APP: [(LIGHT, LIGHT + '<Color x:Key="FsSonderColor">#FF0000</Color>')]}),
    ("ausgedachte Akzentstufe", 1, {APP: [(LIGHT, LIGHT + '<Color x:Key="SystemAccentColorX">#FF0000</Color>')]}),
    ("Farbname im Pinsel", 1, {APP: [(LIGHT, LIGHT + '<SolidColorBrush x:Key="X7" Color="Red" />')]}),
    ("Hex-Wert im Setter", 1, {APP: [(STIL, '<Style x:Key="S1" TargetType="TextBlock"><Setter Property="Foreground" '
                                             'Value="#123456" /></Style>' + STIL)]}),
    ("Farbname im Setter", 1, {APP: [(STIL, '<Style x:Key="S2" TargetType="TextBlock"><Setter Property="Foreground" '
                                             'Value="Red" /></Style>' + STIL)]}),
    ("Farbname in Setter.Value", 1, {APP: [(STIL, '<Style x:Key="S3" TargetType="Border"><Setter Property="Background">'
                                                  '<Setter.Value>Red</Setter.Value></Setter></Style>' + STIL)]}),
    ("Setter mit Collapsed", 0, {APP: [(STIL, '<Style x:Key="S4" TargetType="TextBlock"><Setter Property="Visibility" '
                                              'Value="Collapsed" /><Setter Property="FontWeight"><Setter.Value>Bold'
                                              '</Setter.Value></Setter></Style>' + STIL)]}),
    ("x:String mit #120", 0, {APP: [(STIL, '<x:String x:Key="Nummer">#120</x:String>' + STIL)]}),
    ("Kommentar mit Farbwert", 0, {APP: [(LIGHT, LIGHT + '<!-- früher Color="#BE612E" -->')]}),
    ("Text für Menschen mit #120", 0, {BUCH: [(TRANSPARENT, TRANSPARENT + ' AutomationProperties.Name="#120"')]}),
    ("GradientStop in einer anderen Datei", 1, {BUCH: [("re:(<Page\\b[^>]*>)", '\\1<Page.Resources><LinearGradientBrush '
                                                        'x:Key="G"><GradientStop Color="#123456" Offset="0" />'
                                                        '</LinearGradientBrush></Page.Resources>')]}),
    ("Hex-Wert in einer anderen Datei", 1, {BUCH: [(TRANSPARENT, 'Background="#FFF"')]}),
    ("Farbname in PlaceholderForeground", 1, {BUCH: [(TRANSPARENT, TRANSPARENT + ' PlaceholderForeground="Red"')]}),
    ("Rolle mit Alpha", 0, {APP: [(GRUND, '<Color x:Key="FsGrundColor">#FFEFF0ED</Color>')]}),
    ("App.xaml mit CRLF", 0, {APP: [("re:\n", "\r\n")]}),
    ("App.xaml fehlt", 2, {APP: None}),
    ("Kontrast: Rolle mit Hex-Wert", 1, {APP: [(HC, HC + '<Color x:Key="FsGrundColor">#000000</Color>')]}),
    ("Kontrast: Rolle Glut auf Highlight", 1, {APP: [(GLUT_HC, GLUT_HC.replace("Hotlight", "Highlight"))]}),
    ("Kontrast: Pinsel auf falscher Systemfarbe", 1, {APP: [(EMBER_HC, EMBER_HC.replace("Hotlight", "Window"))]}),
    ("Kontrast: Pinsel mit StaticResource", 1, {APP: [(EMBER_HC, EMBER_HC.replace("ThemeResource", "StaticResource"))]}),
    ("Kontrast: Pinsel fehlt", 1, {APP: [(SPARK_HC, "")]}),
    ("Kontrast: Rolle fehlt", 1, {APP: [(FUNKE_HC, "")]}),
    ("Kontrast: Pinsel mit Hex-Wert", 1, {APP: [(EMBER_HC, '<SolidColorBrush x:Key="LfEmberBrush" Color="#FFFF00" />')]}),
    ("Kontrast: Rolle als Color mit Hex-Wert", 1, {APP: [(GLUT_HC, '<Color x:Key="FsGlutColor">#FF0000</Color>')]}),
    ("Kontrast: Wörterbuch entfernt", 1, {APP: [(r're:\s*<ResourceDictionary x:Key="HighContrast">.*?</ResourceDictionary>', "")]}),
    ("Kontrast: Lf-Pinsel ohne Rolle fehlt", 1, {APP: [(LIGHT, LIGHT + '<SolidColorBrush x:Key="LfAkzentBrush" '
                                                       'Color="{ThemeResource SystemAccentColor}" />')]}),
    ("Kontrast: Lf-Pinsel ohne Rolle vorhanden", 0, {APP: [(LIGHT, LIGHT + '<SolidColorBrush x:Key="LfAkzentBrush" '
                                                          'Color="{ThemeResource SystemAccentColor}" />'),
                                                         (HC, HC + '<SolidColorBrush x:Key="LfAkzentBrush" '
                                                          'Color="{ThemeResource SystemColorHighlightColor}" />')]}),
]


def aendern(text: str, aenderungen: list[tuple[str, str]]) -> str:
    for alt, neu in aenderungen:
        if alt.startswith("re:"):
            muster = re.compile(alt[3:], re.S)
            if not muster.search(text):
                raise LookupError(alt)
            text = muster.sub(lambda _: neu, text) if "\\1" not in neu else muster.sub(neu, text)
        else:
            if alt not in text:
                raise LookupError(alt)
            text = text.replace(alt, neu, 1)
    return text


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="pruefe.py", description=__doc__.split("\n\n")[0])
    parser.add_argument("--export", type=Path, required=True)
    args = parser.parse_args(argv)
    dateien = [n for n in subprocess.run(["git", "ls-files", "-z", "--", "*.xaml"], capture_output=True, check=True)
               .stdout.decode("utf-8").split("\0") if n]
    falsch = 0
    with tempfile.TemporaryDirectory(prefix="farben-") as tmp:
        for nummer, (name, erwartet, aenderungen) in enumerate(FAELLE, 1):
            ordner = Path(tmp) / str(nummer)
            for datei in dateien:
                ziel = ordner / datei
                ziel.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(datei, ziel)
            try:
                for datei, liste in aenderungen.items():
                    if liste is None:
                        (ordner / datei).unlink()
                        continue
                    text = (ordner / datei).read_bytes().decode("utf-8")
                    (ordner / datei).write_bytes(aendern(text, liste).encode("utf-8"))
            except LookupError as fehler:
                print(f"VERALTET  {name}: Fundstelle nicht gefunden: {str(fehler)[:80]}")
                falsch += 1
                continue
            lauf = subprocess.run([sys.executable, str(PRUEFUNG), "--export", str(args.export), "--wurzel", str(ordner)],
                                  capture_output=True, text=True)
            ok = lauf.returncode == erwartet
            falsch += not ok
            erste = (lauf.stdout + lauf.stderr).strip().splitlines()[:1]
            print(f"{'ok' if ok else 'FALSCH':8}  {name}: Exit-Code {lauf.returncode}, erwartet {erwartet}"
                  + (f" | {erste[0][:120]}" if erste and not ok else ""))
    print(f"{len(FAELLE) - falsch} von {len(FAELLE)} Fällen wie erwartet.")
    return 1 if falsch else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
