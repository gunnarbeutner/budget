#!/usr/bin/env python3
"""Write a component's .resx and .de.resx from a JSON map {key: [english, german]}.

Usage: gen_resx.py <strings.json> <dest-without-extension>
The standard resx schema header is copied from an existing resource file.
"""
import json
import re
import sys
from xml.sax.saxutils import escape

TEMPLATE = "src/OpenBudget/Resources/Shared/UserMenu.resx"


def header():
    text = open(TEMPLATE, encoding="utf-8").read()
    return text[: text.index("<data")]


def write(path, entries, index):
    parts = [header()]
    for key, values in entries.items():
        parts.append(f'<data name="{key}" xml:space="preserve">\n    <value>{escape(values[index])}</value>\n  </data>\n  ')
    out = "".join(parts).rstrip() + "\n</root>\n"
    open(path, "w", encoding="utf-8").write(out)


if __name__ == "__main__":
    data = json.load(open(sys.argv[1], encoding="utf-8"))
    write(sys.argv[2] + ".resx", data, 0)
    write(sys.argv[2] + ".de.resx", data, 1)
