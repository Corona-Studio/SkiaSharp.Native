"""Live Windows regression check for git-sync-deps -> fetch-gn.

Requires an unpatched checkout of the pinned Skia source and internet access.
Only architecture detection is simulated. Upstream scripts, child processes,
HTTP downloads, extraction and GN execution are real. Unrelated dependency
repositories are omitted from the isolated fixture's DEPS file.
"""

import argparse
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile


def run(command: list[str], cwd: Path, env: dict[str, str]) -> subprocess.CompletedProcess:
    result = subprocess.run(
        command, cwd=cwd, env=env, text=True, capture_output=True, timeout=120
    )
    print(result.stdout, end="", flush=True)
    if result.returncode:
        print(result.stderr, end="", flush=True)
    return result


def verify(skia_source: Path) -> None:
    if sys.platform != "win32":
        raise RuntimeError("Run this check on Windows so the downloaded gn.exe can execute.")
    patcher = Path(__file__).resolve().parents[1] / "patch-skia-gn.py"
    with tempfile.TemporaryDirectory(prefix="launcherx-gn-regression-") as temporary:
        root = Path(temporary)
        for relative in ("tools/git-sync-deps", "bin/fetch-gn", "bin/activate-emsdk"):
            destination = root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(skia_source / relative, destination)
        if "LauncherX:" in (root / "bin/fetch-gn").read_text(encoding="utf-8"):
            raise RuntimeError("Provide an unpatched Skia source for the failing baseline.")
        (root / "DEPS").write_text("deps = {}\n", encoding="utf-8")

        simulation = root / "simulation"
        simulation.mkdir()
        (simulation / "sitecustomize.py").write_text(
            "import platform\n"
            "import urllib.request\n"
            "platform.machine = lambda: 'ARM64'\n"
            "original_urlopen = urllib.request.urlopen\n"
            "def logged_urlopen(url, *args, **kwargs):\n"
            "    print('GN_REQUEST_URL=' + str(url), flush=True)\n"
            "    kwargs.setdefault('timeout', 45)\n"
            "    return original_urlopen(url, *args, **kwargs)\n"
            "urllib.request.urlopen = logged_urlopen\n",
            encoding="utf-8",
        )
        env = os.environ.copy()
        env["PYTHONPATH"] = str(simulation)
        env["PYTHONUTF8"] = "1"
        env["GIT_SYNC_DEPS_PATH"] = str(root / "DEPS")
        env.pop("GIT_SYNC_DEPS_SKIP_EMSDK", None)
        command = [sys.executable, str(root / "tools/git-sync-deps")]

        # Pre-installing GN cannot prevent the unconditional upstream download.
        gn = root / "bin/gn.exe"
        gn.write_bytes(b"preinstalled GN placeholder")
        baseline = run(command, root, env)
        assert baseline.returncode != 0, "Expected the original bootstrap to fail"
        assert "windows-arm64" in baseline.stdout
        assert "HTTP Error 404" in baseline.stderr
        print("PASS: reproduced child-process 404 even with GN preinstalled.", flush=True)

        gn.unlink()
        patched = run([sys.executable, str(patcher), str(root)], root, env)
        assert patched.returncode == 0
        patch_bytes = (root / "bin/fetch-gn").read_bytes()
        repeated = run([sys.executable, str(patcher), str(root)], root, env)
        assert repeated.returncode == 0
        assert (root / "bin/fetch-gn").read_bytes() == patch_bytes

        for cache_state in ("cold", "warm"):
            result = run(command, root, env)
            assert result.returncode == 0, f"{cache_state} bootstrap failed"
            assert "windows-amd64" in result.stdout
            payload = gn.read_bytes()
            assert payload[:2] == b"MZ"
            pe_offset = struct.unpack_from("<I", payload, 0x3C)[0]
            assert struct.unpack_from("<H", payload, pe_offset + 4)[0] == 0x8664
            assert payload == (root / "third_party/gn/gn.exe").read_bytes()
            version = run([str(gn), "--version"], root, env)
            assert version.returncode == 0
            print(f"PASS: {cache_state} sync, amd64 PE, depot_tools copy and GN execution.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("skia_source", type=Path)
    verify(parser.parse_args().skia_source.resolve())
