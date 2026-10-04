"""Select the available Windows GN host binary in Skia's pinned fetch script."""

import argparse
from pathlib import Path


CPU_SELECTION = (
    "  cpu = {'aarch64': 'arm64', 'amd64': 'amd64', 'arm64': 'arm64', "
    "'x86_64': 'amd64'}[platform.machine().lower()]"
)
WINDOWS_ARM64_SELECTION = CPU_SELECTION + (
    "\n  # LauncherX: the pinned GN revision has no windows-arm64 package."
    "\n  if OS == 'windows' and cpu == 'arm64':"
    "\n    cpu = 'amd64'"
)


def patch_fetch_gn(skia_source: Path) -> None:
    script = skia_source / "bin" / "fetch-gn"
    source = script.read_text(encoding="utf-8")
    if WINDOWS_ARM64_SELECTION in source:
        print("Skia GN Windows ARM64 host selection is already patched.")
        return
    if source.count(CPU_SELECTION) != 1:
        raise RuntimeError(
            f"Unexpected GN CPU selection in {script}; review the pinned Skia bootstrap."
        )
    script.write_text(
        source.replace(CPU_SELECTION, WINDOWS_ARM64_SELECTION),
        encoding="utf-8",
    )
    print("Patched Skia fetch-gn: Windows ARM64 uses the pinned windows-amd64 GN.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("skia_source", type=Path)
    patch_fetch_gn(parser.parse_args().skia_source)
