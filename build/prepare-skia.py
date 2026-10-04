"""Restore pinned RAW codec dependencies enabled by our GN configuration."""
import argparse
from pathlib import Path
import re


def prepare(source):
    path = source / 'DEPS'
    deps = path.read_text()
    for dependency in ('dng_sdk', 'piex'):
        pattern = rf'^(  )# ("third_party/externals/{dependency}"\s*: .+)$'
        deps, count = re.subn(pattern, r'\1\2', deps, flags=re.MULTILINE)
        if count != 1 and not re.search(rf'^  "third_party/externals/{dependency}"\s*:', deps, re.MULTILINE):
            raise RuntimeError(f'Unexpected pinned {dependency} dependency definition')
    path.write_text(deps)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    prepare(parser.parse_args().source)
