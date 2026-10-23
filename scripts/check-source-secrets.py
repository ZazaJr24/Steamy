"""Fail before publishing source containing provider credentials or private keys.

Reports file locations only, never the matching secret. This is a narrow guard,
not a replacement for reviewing source archives and rotating exposed secrets.
"""
import re
import sys
from pathlib import Path

EXCLUDED = {".git", ".cache", "bin", "obj", "artifacts", "publish", "node_modules", ".vs"}
PATTERNS = [
    re.compile(rb"RYUUMANIFEST[A-Za-z0-9]{6,}"),
    re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    re.compile(rb"(?i)depotbox(?:_?api)?_?key[\"'\s]*[=:][\"'\s]*[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}"),
]


def main():
    root = Path(__file__).resolve().parent.parent
    problems = []
    for path in root.rglob("*"):
        relative = path.relative_to(root)
        if any(part in EXCLUDED for part in relative.parts) or not path.is_file():
            continue
        if path.name == ".env" or path.name.startswith(".env.") and path.name != ".env.example":
            problems.append(f"{relative}: private environment file in source tree")
            continue
        if path.suffix.lower() not in {".cs", ".py", ".ps1", ".sh", ".md", ".txt", ".json", ".yml", ".yaml", ".xaml", ".xml", ".config", ".example", ".csproj"}:
            continue
        data = path.read_bytes()
        for pattern in PATTERNS:
            match = pattern.search(data)
            if match:
                problems.append(f"{relative}:{data[:match.start()].count(bytes([10])) + 1}: possible embedded secret")
    if problems:
        print("Source secret check failed:")
        print("\n".join(problems))
        return 1
    print("Source secret check passed (provider credential patterns, private keys, environment files).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
