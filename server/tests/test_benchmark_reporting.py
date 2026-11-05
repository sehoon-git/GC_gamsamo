"""Small or contaminated performance samples must not be presented as tail guarantees."""
import importlib.util
import copy

import pytest

from app.core.config import ROOT


def benchmark():
    spec = importlib.util.spec_from_file_location("quality_benchmark", ROOT / "ai/evaluation/reliability_benchmark.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def rows(count, operation="turn"):
    return [{"operation": operation, "ok": True, "shared_server_busy": False, "wall_seconds": index + 1}
            for index in range(count)]


def test_quantiles_require_enough_successful_uncontaminated_samples_per_operation():
    summary = benchmark().summary
    assert "p50_seconds" not in summary(rows(19))["turn"]
    assert summary(rows(20))["turn"]["p50_seconds"] == 10.5
    assert "p95_seconds" not in summary(rows(99))["turn"]
    assert summary(rows(100))["turn"]["p95_seconds"] == 95
    samples = rows(100)
    samples[0]["shared_server_busy"] = True
    samples[1]["ok"] = False
    result = summary(samples + rows(5, "evaluate"))
    assert result["turn"]["n"] == 98 and result["turn"]["failed"] == result["turn"]["excluded_busy"] == 1
    assert "p95_seconds" not in result["turn"] and "p50_seconds" not in result["evaluate"]
    cached = [{**r, "response_cache": "hit", "wall_seconds": .001} for r in rows(100, "evaluate")]
    result = summary(rows(5, "evaluate") + cached)
    assert result["evaluate"]["n"] == 5 and result["evaluate_cached"]["n"] == 100
    assert "p50_seconds" not in result["evaluate"]


def test_private_measurements_are_written_without_group_read_permissions(tmp_path):
    module = benchmark()
    path = tmp_path / "report.json"
    path.write_text("old")
    path.chmod(0o644)
    module.write_private(path, {"synthetic": "test only"})
    assert path.stat().st_mode & 0o777 == 0o600
    assert not list(tmp_path.glob(".measurement-*"))


def test_paired_comparison_refuses_different_input_or_weights(monkeypatch):
    monkeypatch.syspath_prepend(str(ROOT / "ai/evaluation"))
    from compare_runs import compare
    row = {**rows(1)[0], "patient_id": "harin", "scenario": "supportive", "step": 1, "status": 200, "request_sha256": "identical"}
    before = {"label": "before", "model": {"model_id": "fixed", "revision": "pinned", "max_model_len": 16384, "roles": {}},
              "hardware_inventory": ["test GPU"], "rows": [row]}
    after = copy.deepcopy(before); after["label"] = "after"
    assert compare(before, after)["paired_requests"] == 1
    after["rows"][0]["request_sha256"] = "different"
    with pytest.raises(ValueError, match="Input snapshot"):
        compare(before, after)
    after = copy.deepcopy(before); after["model"]["revision"] = "different"
    with pytest.raises(ValueError, match="Different model"):
        compare(before, after)
    after = copy.deepcopy(before); after["rows"][0]["ok"] = False; after["rows"][0]["status"] = 504
    result = compare(before, after)
    assert result["paired_requests"] == 0 and len(result["excluded_pairs"]) == 1
