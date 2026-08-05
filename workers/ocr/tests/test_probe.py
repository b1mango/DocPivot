from __future__ import annotations

import json
import sys
from typing import Any

import pytest

from docpivot_ocr import PROTOCOL_VERSION
from docpivot_ocr.__main__ import main


def test_probe_emits_canonical_worker_contract(
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    monkeypatch.setattr(sys, "argv", ["docpivot-ocr", "--probe"])

    assert main() == 0

    payload: dict[str, Any] = json.loads(capsys.readouterr().out)
    assert payload["v"] == PROTOCOL_VERSION
    assert payload["type"] == "probe-result"
    assert payload["worker"] == "ocr"
    assert payload["status"] == "foundation-ready"
    assert payload["capabilities"] == {
        "localOcrInstalled": False,
        "digitalTableParserInstalled": False,
    }
