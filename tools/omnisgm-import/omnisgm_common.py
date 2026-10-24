"""Helpers shared by the OmnisGM import scripts."""
import collections, json, os, re


def blocks(path, level=3, italic=True, named=False):
    """The "### Heading" blocks (or "#### " with level=4) as (heading, body). With italic (the default) the body must open with an
    italic line; with named the heading must end with the English name in parentheses."""
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
    return [(h, "\n".join(b).strip("\n")) for h, b in out
            if b and (not italic or re.match(r"^\s*\*[^*]", "\n".join(b).strip("\n"))) and (not named or re.search(r"\([^()]*\)\s*$", h))]


def norm(s):
    return re.sub(r"[^a-z0-9]", "", s.lower().replace("’", "'"))


def sentences(text):
    text = re.sub(r"\*\*[^*]*\*\*|\*|_|\|[^\n]*", "", text)
    return len([x for x in re.split(r"(?<=[.!?])\s+", text) if len(x.strip()) > 3])


def scalar(value):
    return json.dumps(value, ensure_ascii=False)


def english_name(heading):
    m = re.search(r"\(([^()]*)\)\s*$", heading)
    return m.group(1) if m else heading


def russian_name(heading):
    return re.sub(r"\s*\([^()]*\)\s*$", "", heading).strip()


def our_names(pack, folder):
    """norm(English name) -> entry id for a pack folder of YAML files."""
    found = {}
    for f in os.listdir(os.path.join(pack, folder)):
        text = open(os.path.join(pack, folder, f), encoding="utf-8").read()
        found[norm(re.search(r"^name: (.+)$", text, re.M).group(1).strip().strip("'\""))] = f[:-5]
    return found


def write_items(path, items):
    """items: (id, name, description or None, {property: value}); one YAML list, descriptions as block scalars."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        for sid, name, desc, props in sorted(items, key=lambda i: i[0]):
            f.write(f"- id: {sid}\n  name: {scalar(name)}\n")
            if desc:
                f.write("  description: |-\n" + "".join(("    " + l if l else "") + "\n" for l in desc.split("\n")))
            if props:
                f.write("  properties:\n")
                for k, v in props.items():
                    if isinstance(v, list) and not v:
                        f.write(f"    {k}: []\n")  # an empty list: the translated text already covers these rows
                    elif isinstance(v, list):
                        f.write(f"    {k}:\n")
                        for row in v:
                            f.write(f"    - name: {scalar(row['name'])}\n")
                            if "level" in row:
                                f.write(f"      level: {row['level']}\n")
                            f.write("      description: |-\n" + "".join(("        " + l if l else "") + "\n" for l in row["description"].split("\n")))
                    else:
                        f.write(f"    {k}: {scalar(v)}\n")


def _numbers(text):
    text = re.sub(r"(?<=\d)[,\u00a0\u202f\u2009 ](?=\d{3}(?!\d))", "", text)
    return collections.Counter(re.findall(r"\d+", text)), collections.Counter(m.lower() for m in re.findall(r"(?<![A-Za-z\d])\d*[dD]\d+(?![A-Za-z\d])", text))


def faithful(english, russian):
    """True when the Russian keeps every number and dice expression of OmnisGM's own English (it may add some)."""
    en_n, en_d = _numbers(english)
    ru_n, ru_d = _numbers(russian)
    return not (en_n - ru_n) and not (en_d - ru_d)
