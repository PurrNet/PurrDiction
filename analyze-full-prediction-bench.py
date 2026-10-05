"""Summarize the measured client simulation work of run-full-prediction-bench.ps1.

Uses only the standard library. Cases remain the independent repeat unit; clients
in one case share a server and are averaged before repeat ranges are computed.
The *_cpu_ms_per_s columns contain instrumented elapsed spans, including waits
and descheduling; they are not process CPU counters or core utilization.
"""
import argparse
import csv
import json
import statistics
from pathlib import Path


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def client_metrics(report):
    t = report["telemetry"]
    duration = t["durationSeconds"]
    phases = [t[k] for k in ("forward", "verified", "gapCatchup", "speculativeReplay")]
    count = sum(p["count"] for p in phases)
    physics = sum(p["physicsMs"]["total"] for p in phases)
    gameplay = sum(p["simulateMs"]["total"] for p in phases)
    work = t["forward"]["totalMs"]["total"] + t["batchMs"]["total"]
    return {
        "duration_s": duration,
        "world_passes_per_s": count / duration,
        "passes_per_forward_tick": count / max(1, t["forward"]["count"]),
        "forward_ticks_per_s": t["forward"]["count"] / duration,
        "verified_ticks_per_s": t["verified"]["count"] / duration,
        "gap_ticks_per_s": t["gapCatchup"]["count"] / duration,
        "speculative_ticks_per_s": t["speculativeReplay"]["count"] / duration,
        "batches_per_s": t["correctionBatches"] / duration,
        "batches_per_frame_mean": t["frameBatches"]["mean"],
        "batches_per_frame_max": t["frameBatches"]["max"],
        "passes_per_frame_mean": t["framePasses"]["mean"],
        "passes_per_frame_max": t["framePasses"]["max"],
        "batch_work_mean_ms": t["batchMs"]["mean"],
        "batch_work_p95_ms": t["batchMs"]["p95"],
        "replay_depth_mean": t["replayDepthTicks"]["mean"],
        "replay_depth_p95": t["replayDepthTicks"]["p95"],
        "physics_cpu_ms_per_s": physics / duration,
        "gameplay_cpu_ms_per_s": gameplay / duration,
        "prediction_cpu_ms_per_s": work / duration,
        "physics_us_per_pass": physics * 1000 / max(1, count),
        "gameplay_us_per_pass": gameplay * 1000 / max(1, count),
        "forward_physics_us": t["forward"]["physicsMs"]["mean"] * 1000,
        "restored_physics_us": t["firstPhysicsAfterRestore"]["mean"] * 1000,
        "speculative_physics_us": t["speculativeReplay"]["physicsMs"]["mean"] * 1000,
        "prediction_work_mean_ms_per_frame": t["frameWorkMs"]["mean"],
        "prediction_work_p95_ms_per_frame": t["frameWorkMs"]["p95"],
        "prediction_work_max_ms_per_frame": t["frameWorkMs"]["max"],
        "frame_interval_p95_ms": t["frameDeltaMs"]["p95"],
        "frame_interval_mean_ms": t["frameDeltaMs"]["mean"],
        "contacts_per_s": report["contacts"] / duration,
        "body_contacts_per_s": report["bodyContacts"] / duration,
        "ground_queries_per_s": report["groundingQueries"] / duration,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    cases = []
    failures = []
    seen = set()
    for directory in args.directories:
        for path in sorted(directory.rglob("case.json")):
            if path.resolve() in seen:
                continue
            seen.add(path.resolve())
            case = read(path)
            if case.get("elapsedSeconds", 0) <= 0:
                continue  # The runner writes its planned case before launching processes.
            if case.get("success") is not True:
                failures.append({"path": str(path), "reason": case.get("failure")})
                continue
            reports = []
            for process in case.get("processes", []):
                if process["role"] != "client":
                    continue
                if not Path(process["metricsPath"]).is_file():
                    failures.append({"path": process["metricsPath"], "reason": "missing physics metrics (regression cases have no timing report)"})
                    break
                report = read(process["metricsPath"])
                if not report.get("success") or not report.get("verifiedStateMatch") or not report.get("telemetry"):
                    failures.append({"path": process["metricsPath"], "reason": report.get("message")})
                    break
                reports.append(report)
            else:
                if len(reports) != case["clients"]:
                    failures.append({"path": str(path), "reason": "client metric count mismatch"})
                    continue
                samples = [client_metrics(r) for r in reports]
                row = {
                    "case": case["caseId"], "repeat": case["repeat"],
                    "clients": case["clients"], "bodies": case["totalBodies"],
                    "one_way_latency_ms": case["nominalEffectiveAddedOneWayMs"],
                    "reconcile_interval_ms": case["clientReconcileMs"],
                    "workers": case["jobWorkerCount"], "source": str(path),
                    **{key: statistics.mean(s[key] for s in samples) for key in samples[0]},
                }
                cases.append(row)
    if not cases:
        raise SystemExit("No successful measured cases found: " + json.dumps(failures))
    args.output.mkdir(parents=True, exist_ok=True)
    with (args.output / "case-means.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(cases[0]))
        writer.writeheader()
        writer.writerows(cases)
    groups = {}
    for row in cases:
        key = tuple(row[k] for k in ("clients", "bodies", "one_way_latency_ms", "reconcile_interval_ms", "workers"))
        groups.setdefault(key, []).append(row)
    aggregate = []
    lines = ["# Full prediction benchmark measurements", "",
             "Client means within each case; medians across independent case repeats. Timings are instrumented elapsed spans, including waits and descheduling, not processor utilization. "
             "Headless Development Mono players on one machine; these are not rendered-game FPS estimates. "
             "Physics includes native collision callbacks. Percentiles have approximately 2.2% histogram quantization.", "",
             "| Clients | Bodies | One-way ms | Reconcile interval ms | Repeats | Passes/s | Depth | Physics µs/pass | Physics ms/s | Gameplay ms/s | Prediction ms/s | Prediction p95 ms/frame |", 
             "|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for key, repeats in sorted(groups.items()):
        metrics = {}
        numeric = [k for k in repeats[0] if isinstance(repeats[0][k], (float, int)) and k not in ("repeat", "clients", "bodies", "workers")]
        for name in numeric:
            values = [row[name] for row in repeats]
            metrics[name] = {"median": statistics.median(values), "min": min(values), "max": max(values)}
        aggregate.append({"clients": key[0], "bodies": key[1], "one_way_latency_ms": key[2],
                          "reconcile_interval_ms": key[3], "workers": key[4], "repeats": len(repeats), "metrics": metrics})
        names = ("world_passes_per_s", "replay_depth_mean", "physics_us_per_pass", "physics_cpu_ms_per_s",
                 "gameplay_cpu_ms_per_s", "prediction_cpu_ms_per_s", "prediction_work_p95_ms_per_frame")
        values = [str(key[0]), str(key[1]), str(key[2]), str(key[3]), str(len(repeats))]
        values += [f"{metrics[name]['median']:.2f}" for name in names]
        lines.append("| " + " | ".join(values) + " |")
    lines += ["", f"Successful cases: {len(cases)}. Failed cases/reports excluded from timing conclusions: {len(failures)}.", "",
              "Exact case means and repeat ranges are in the adjacent CSV and JSON. "
              "`prediction_cpu_ms_per_s` sums forward work and full correction batches; nested replay work is not counted twice.", ""]
    (args.output / "measurements.md").write_text("\n".join(lines), encoding="utf-8")
    (args.output / "aggregate.json").write_text(json.dumps({"groups": aggregate, "failures": failures}, indent=2), encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
