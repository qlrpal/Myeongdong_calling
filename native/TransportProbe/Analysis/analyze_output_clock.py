"""Summarize output-clock controls; do not interpret clock offsets as physical latency."""
import argparse
import json
from pathlib import Path
from statistics import median


def summarize(path):
    document = json.loads(path.read_text(encoding="utf-8-sig"))
    rows = []
    for trial in document["results"]:
        if "unsupported" in trial:
            rows.append({"mode": trial["name"], "unsupported": trial["unsupported"]})
            continue
        valid = [point for point in trial["measurements"] if point["valid"]]
        commits = {point["marker"]: point["releasedAt"] for point in trial["commits"]}
        def middle(values):
            return median(values) if values else None
        rows.append({
            "mode": trial["name"], "valid": len(valid), "attempts": len(trial["measurements"]),
            "device": trial["device"], "endpointId": trial.get("endpointId"),
            "releaseToCaptureCallbackMedianMs": middle([
                (point["callbackAt"] - commits[point["marker"]]) * 1000 for point in valid]),
            "renderClockToLoopbackQpcMedianMs": middle([point["renderClockToLoopbackQpcMs"] for point in valid]),
            "renderClockToCaptureCallbackMedianMs": middle([point["renderClockToCallbackMs"] for point in valid]),
            "captureQpcAgeMedianMs": middle([point["captureQpcAgeMs"] for point in valid]),
            "clockRateRatio": trial["clockRateRatio"], "renderDrains": trial["empty"],
            "captureError": trial["captureError"], "effectsService": trial.get("effects"),
            "rejected": [{"marker": point["marker"], "correlation": point["Correlation"],
                          "discontinuous": point["discontinuous"]}
                         for point in trial["measurements"] if not point["valid"]],
        })
    return {"source": path.name, "comparisons": rows,
            "notes": "Native format/direct render controls. All clock values refer to Windows observation points, not physical DAC time. Rejected markers are excluded, never treated as zero delay. Empty effect lists do not exclude third-party endpoint APOs. Acoustic capture includes microphone/room/input processing."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("inputs", type=Path, nargs="+")
    args = parser.parse_args()
    if args.output.resolve() in {path.resolve() for path in args.inputs}:
        parser.error("Output must not overwrite an input")
    results = [summarize(path) for path in args.inputs]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    for result in results:
        print(result["source"], [(row["mode"], row.get("valid")) for row in result["comparisons"]])


if __name__ == "__main__":
    main()
