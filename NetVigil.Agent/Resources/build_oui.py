import csv, os, re, sys

SUFFIX_RE = re.compile(
    r"\s*,?\s*(Inc\.?|Corporation|Corp\.?|Limited|Ltd\.?|"
    r"Co\.\,?\s*Ltd\.?|GmbH|S\.A\.|S\.r\.l\.|B\.V\.|N\.V\.|"
    r"LLC|LLP|AG|Pte\.?\s*Ltd\.?|Pty\.?\s*Ltd\.?|PLC|SAS|"
    r"S\.p\.A\.|S\.L\.|OY|AB|HQ|Headquarters|"
    r"Technologies?|Technology|Electronics?)"
    r"\.?\s*$",
    re.IGNORECASE,
)

def clean(name: str) -> str:
    n = re.sub(r"\s+", " ", name.strip())
    for _ in range(3):
        new = SUFFIX_RE.sub("", n).strip(" .,;-")
        if new == n:
            break
        n = new
    return (n or "Unknown")[:48]


def main(src: str) -> None:
    rows = []
    with open(src, newline="", encoding="utf-8") as f:
        r = csv.reader(f)
        next(r) 
        for row in r:
            if len(row) < 3:
                continue
            oui = row[1].strip().upper()
            if len(oui) != 6 or not all(c in "0123456789ABCDEF" for c in oui):
                continue
            rows.append((oui, clean(row[2])))

    rows.sort()
    out = os.path.join(os.path.dirname(__file__), "oui-vendors.tsv")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        for oui, name in rows:
            f.write(f"{oui}\t{name}\n")
    print(f"wrote {len(rows)} entries -> {out} ({os.path.getsize(out)/1024:.0f} KB)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "oui.csv")
