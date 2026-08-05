from __future__ import annotations

import argparse
import json
import platform

from docpivot_ocr import PROTOCOL_VERSION


def main() -> int:
    parser = argparse.ArgumentParser(prog="docpivot-ocr")
    parser.add_argument("--probe", action="store_true")
    args = parser.parse_args()

    if not args.probe:
        parser.error("--probe is required until OCR engines are installed")

    result = {
        "v": PROTOCOL_VERSION,
        "type": "probe-result",
        "worker": "ocr",
        "status": "foundation-ready",
        "metadata": {"pythonVersion": platform.python_version()},
        "capabilities": {
            "localOcrInstalled": False,
            "digitalTableParserInstalled": False,
        },
    }
    print(json.dumps(result, ensure_ascii=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
