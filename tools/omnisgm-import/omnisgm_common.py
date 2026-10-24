"""Helpers shared by the OmnisGM import scripts."""
import json, os, re


def blocks(path, level=3):
    """The "### Heading" blocks (or "#### " with level=4) whose body opens with an italic line, as (heading, body)."""
    marker, stop = "#" * level + " ", re.compile(r"^#{1,%d} " % (level - 1))
    out, current = [], None
    for line in open(path, encoding="utf-8").read().replace("\r\n", "\n").split("\n"):
        if line.startswith(marker):
            current = [line[len(marker):].strip(), []]
            out.append(current)
        elif current is not None:
            if stop.match(line):
                current = None
            else:
                current[1].append(line)
    return [(h, "\n".join(b).strip("\n")) for h, b in out if b and re.match(r"^\s*\*[^*]", "\n".join(b).strip("\n"))]


def norm(s):
    return re.sub(r"[^a-z0-9]", "", s.lower().replace("’", "'"))


def sentences(text):
    text = re.sub(r"\*\*[^*]*\*\*|\*|_|\|[^\n]*", "", text)
    return len([x for x in re.split(r"(?<=[.!?])\s+", text) if len(x.strip()) > 3])


def scalar(value):
    return json.dumps(value, ensure_ascii=False)
