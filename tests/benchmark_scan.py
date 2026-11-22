"""Compare two packaged scanners on the same explicit, stable directories."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import statistics
import subprocess
import tempfile
import time


def file_digest(path):
    result = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


# Directory IDs and row insertion order can vary between implementations. Resolve each
# path before comparing, including empty/partial directories and identities of hard links.
PATHS = """
WITH RECURSIVE paths(id, path) AS (
  SELECT id, name FROM dirs WHERE parent IS NULL
  UNION ALL
  SELECT d.id, paths.path || '\\' || d.name FROM dirs AS d JOIN paths ON d.parent=paths.id
)
"""
CHECKS = {
    "folders": """SELECT p.path,d.bytes,d.direct_bytes,d.files,d.flags,d.logical_bytes,d.fsid
               FROM paths p JOIN dirs d ON p.id=d.id ORDER BY p.path""",
    "files": """SELECT p.path || '\\' || e.name AS name,e.bytes,e.logical_bytes,e.flags,e.fsid
             FROM entries e JOIN paths p ON e.parent=p.id ORDER BY name""",
    "volumes": """SELECT p.path,v.total_bytes,v.unique_bytes,v.duplicate_files,v.unknown_files,v.serial
               FROM volumes v JOIN paths p ON p.id=v.root_id ORDER BY p.path""",
}


def snapshot(path):
    manifest = {}
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as db:
        for table, query in CHECKS.items():
            digest = hashlib.sha256()
            count = 0
            for row in db.execute(PATHS + query):
                values = [item.hex() if isinstance(item, bytes) else item for item in row]
                digest.update(json.dumps(values, ensure_ascii=True, separators=(",", ":")).encode("ascii"))
                digest.update(b"\n")
                count += 1
            manifest[table] = (count, digest.hexdigest())
    return manifest


def scan(executable, root, output):
    started = time.perf_counter()
    result = subprocess.run([str(executable), "--root", str(root), "--output", str(output)],
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
    duration = time.perf_counter() - started
    if result.returncode:
        raise RuntimeError(f"{executable.name} exited {result.returncode}: {result.stderr[-1000:]} {result.stdout[-1000:]}")
    return duration


def compare(binaries, root, repeats):
    times = {label: [] for label in binaries}
    reference = None
    with tempfile.TemporaryDirectory(prefix="wdm-scan-bench-") as directory:
        # Fresh snapshots each run; alternate order to reduce cache and thermal bias.
        for index in range(repeats):
            ordered = list(binaries) if index % 2 == 0 else list(reversed(binaries))
            for label in ordered:
                output = Path(directory) / f"{label}-{index}.sqlite"
                elapsed = scan(binaries[label], root, output)
                manifest = snapshot(output)
                if reference is None:
                    reference = manifest
                elif reference != manifest:
                    changed = [table for table in CHECKS if reference[table] != manifest[table]]
                    raise ValueError(f"{root}: semantic mismatch or source changed ({', '.join(changed)}); timings are not comparable.")
                times[label].append(elapsed)
                print(f"{root}  {label} #{index + 1}: {elapsed:.3f}s", flush=True)
                output.unlink()
    typical = {label: statistics.median(samples) for label, samples in times.items()}
    print(json.dumps({"root": str(root), "file_count": reference["files"][0],
                      "directory_count": reference["folders"][0], "median_seconds": typical,
                      "speedup": typical["baseline"] / typical["candidate"],
                      "elapsed_reduction_percent": 100 * (1 - typical["candidate"] / typical["baseline"])},
                     ensure_ascii=False), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--root", type=Path, action="append", required=True)
    parser.add_argument("--repeats", type=int, default=3)
    args = parser.parse_args()
    if args.repeats < 1:
        parser.error("repeats must be positive")
    binaries = {"baseline": args.baseline.resolve(strict=True), "candidate": args.candidate.resolve(strict=True)}
    for label, exe in binaries.items():
        print(f"{label}: {exe}  sha256={file_digest(exe)}", flush=True)
    for root in args.root:
        root = root.resolve(strict=True)
        if not root.is_dir() or root == Path(root.anchor):
            parser.error("select a stable subdirectory for the benchmark")
        compare(binaries, root, args.repeats)


if __name__ == "__main__":
    main()
