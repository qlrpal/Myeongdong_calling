"""Summarize paired same-host digital render observations, never mouth-to-ear estimates."""
import argparse
import json
import math
from pathlib import Path
from statistics import median


def summary(values):
    values = sorted(values)
    if not values:
        return {"count": 0, "p50": None, "p95": None}
    return {"count": len(values), "p50": median(values), "p95": values[math.ceil(len(values) * .95) - 1]}


def analyze(path):
    document = json.loads(path.read_text(encoding="utf-8-sig"))
    rounds = document["rounds"]
    if len(rounds) != 3 or document.get("complete") is not True:
        raise ValueError(f"Incomplete A/B/A: {path.name}")
    observations = []
    by_frame = []
    for trial in rounds:
        valid = {point["Frame"]: point for point in trial["measurements"]
                 if point["error"] is None and point.get("callbackReceiptTotalMs") is not None}
        if len(valid) < 10 or trial.get("loopbackError") or trial.get("evicted") or trial["snapshot"].get("DeviceError"):
            raise ValueError(f"Insufficient valid callback observations: {path.name}/{trial['mode']}")
        by_frame.append(valid)
        observations.append({
            "mode": trial["mode"], "valid": len(valid), "attempts": len(trial["measurements"]),
            "encoder": trial.get("encoder"),
            "referenceKind": trial.get("referenceKind", "decoded-pcm"),
            "callbackReceiptMs": summary([point["callbackReceiptTotalMs"] for point in valid.values()]),
            "admissionToSubmitMs": summary([point["beforeSubmitMs"] for point in valid.values()
                                             if point.get("beforeSubmitMs") is not None]),
            "submitToCallbackReceiptMs": summary([
                point["callbackReceiptTotalMs"] - point["beforeSubmitMs"] for point in valid.values()
                if point.get("beforeSubmitMs") is not None]),
            "nativeQpcTrustedMarkers": sum(point.get("nativeQpcTrusted") is True for point in valid.values()),
            "rejected": [{"frame": point["Frame"], "reason": point["error"]}
                         for point in trial["measurements"] if point["error"] is not None],
            "render": trial["snapshot"].get("Render"),
            "rtpPlayout": trial["snapshot"].get("RtpPlayout"),
            "underfillMs": trial["snapshot"].get("UnderfillMs"),
        })
    if len({(trial["device"], trial["sampleRate"]) for trial in rounds}) != 1:
        raise ValueError(f"Endpoint name/rate changed: {path.name}")
    common = sorted(set.intersection(*(set(points) for points in by_frame)))
    paired = []
    for frame in common:
        baseline = (by_frame[0][frame]["callbackReceiptTotalMs"] + by_frame[2][frame]["callbackReceiptTotalMs"]) / 2
        candidate = by_frame[1][frame]["callbackReceiptTotalMs"]
        paired.append({"frame": frame, "baselineMs": baseline, "candidateMs": candidate,
                       "reductionMs": baseline - candidate})
    return {"source": path.name, "rounds": observations, "paired": paired,
            "pairedReductionMs": summary([point["reductionMs"] for point in paired]),
            "notes": "Same synthetic marker frames. Baseline is the mean of A1/A2 per marker; positive reduction means lower observed sample-delivery delay. Callback time includes loopback delivery/packet phase/scheduling and is not physical speaker time. Sequential nonrandom rounds; no population guarantee. Native QPC values with nativeQpcTrusted=false are not valid absolute timing evidence."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("inputs", type=Path, nargs="+")
    args = parser.parse_args()
    if args.output.resolve() in {path.resolve() for path in args.inputs}:
        parser.error("Output must not overwrite an input")
    results = [analyze(path) for path in args.inputs]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    for result in results:
        print(result["source"], result["pairedReductionMs"])


if __name__ == "__main__":
    main()
