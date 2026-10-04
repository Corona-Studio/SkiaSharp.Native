"""Package the complete flattened GN archive set and upstream license notices."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def package(rid):
    lock = json.loads((ROOT / "native-lock.json").read_text())
    libraries = ROOT / "artifacts/native" / rid
    extension = ".lib" if rid.startswith("win-") else ".a"
    archives = sorted(libraries.glob("*" + extension))
    if len(archives) <= 2 or any(p.stat().st_size == 0 for p in archives):
        raise RuntimeError("Incomplete or empty static archive set")
    for name in ("libSkiaSharp", "libHarfBuzzSharp"):
        if not (libraries / (name + extension)).is_file():
            raise RuntimeError("Missing " + name)
    if (libraries / "skiasharp.commit").read_text().strip() != lock["skiaCommit"]:
        raise RuntimeError("Source revision mismatch")
    source = ROOT / (".native/skia-" + lock["skiaCommit"])
    output = ROOT / "artifacts/release"
    output.mkdir(parents=True, exist_ok=True)
    archive = output / ("skiasharp-native-" + rid + ".zip")
    manifest = dict(lock, rid=rid, libraries={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in archives})
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
        for path in archives:
            bundle.write(path, path.name)
        bundle.write(libraries / "skiasharp.commit", "skiasharp.commit")
        bundle.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
        bundle.write(ROOT / "LICENSE", "licenses/build-scripts-MIT.txt")
        # Retain notices for Skia and every bundled dependency from the synced tree.
        for path in sorted(source.rglob("*")):
            if path.is_file() and path.name.upper().startswith(("LICENSE", "COPYING", "NOTICE")) and ".git" not in path.parts:
                bundle.write(path, "licenses/skia/" + path.relative_to(source).as_posix())
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    (output / (archive.name + ".sha256")).write_text(digest + "  " + archive.name + "\n")
    print(archive, digest)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rid", choices=["win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64"])
    package(parser.parse_args().rid)
