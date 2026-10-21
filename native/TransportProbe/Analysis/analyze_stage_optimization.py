"""Same-host stages and matched SRTP notification intervals; never mouth-to-ear."""
import argparse
import json
import math
from pathlib import Path
from statistics import median


def stats(values):
    values = sorted(values)
    return {"count": len(values), "p50": median(values) if values else None,
            "p95": values[math.ceil(len(values) * .95) - 1] if values else None,
            "max": values[-1] if values else None}


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def summarize(path):
    document = read(path)
    trace = document["trace"]
    if document.get("error") or trace["EvictedRecords"]:
        raise ValueError(f"Incomplete trace: {path}")
    sends = trace["Sends"]
    receives = trace["Receives"]
    if len(sends) < 6000 or len(receives) < 6000:
        raise ValueError(f"Insufficient media: {path}")
    source = [p for p in sends if sends[0]["SendEnd"] + 5 < p["SendEnd"] < sends[-1]["SendEnd"] - 1
              and p.get("Capture") and p["Capture"].get("FirstSampleAt") is not None]
    stages = {}
    for name, start, end in [("sampleToCallback", "FirstSampleAt", "FirstCallbackAt"),
                             ("callbackToFrame", "FirstCallbackAt", "FrameReadyAt"),
                             ("sampleToFrame", "FirstSampleAt", "FrameReadyAt"),
                             ("encode", "EncodeStart", "EncodeEnd")]:
        stages[name] = stats([(p["Capture"][end] - p["Capture"][start]) * 1000 for p in source])
    stages["sendQueue"] = stats([(p["DequeuedAt"] - p["QueuedAt"]) * 1000 for p in source])
    stages["sendApi"] = stats([(p["SendEnd"] - p["SendStart"]) * 1000 for p in source])
    receive = {(p["Ssrc"], p["Timestamp"]): p for p in receives
               if receives[0]["At"] + 5 < p["At"] < receives[-1]["At"] - 1}
    played = [p for p in trace["Playout"] if p["Outcome"] == "played"
              and (p["Ssrc"], p["Timestamp"]) in receive]
    stages["receiveToSubmit"] = stats([(p["SubmittedAt"] - receive[p["Ssrc"], p["Timestamp"]]["At"]) * 1000
                                       for p in played])
    markers = document["measurements"]
    if len(markers) < 4 or any(m.get("Stages") is None for m in markers):
        raise ValueError(f"Insufficient verified markers: {path}")
    for marker in markers:
        a, b = marker.get("SourceStages"), marker.get("ReceiverStages")
        if not a or not b or a["Hash"] != b["Hash"] or b["Receipts"] != 1 or b["Discarded"]:
            raise ValueError(f"Unverified or discarded marker: {path}")
        if any(b.get(key) is None for key in ("DecodeStart", "DecodeEnd", "OutputSubmittedAt")):
            raise ValueError(f"Missing marker playout stages: {path}")
    stages["markerSubmitToLoopbackQpc"] = stats([m["Stages"]["OutputSubmissionToDetectionMs"] for m in markers])
    final = document["samples"][-1]
    audio = final["Peers"][0]["Audio"]
    playout = audio["RtpPlayout"]
    return {"source": path.name, "reserveMs": document["reserveMs"], "align": document.get("align", False),
            "startupDiscardedSamples": document.get("startupDiscardedSamples"), "stages": stages,
            "underfillMs": audio["UnderfillMs"], "concealedMs": playout["ConcealedMs"],
            "latePackets": playout["LatePackets"], "rebuffers": playout["Rebuffers"],
            "render": audio["Render"], "captureDiscontinuities": final["Microphone"]["CaptureDiscontinuities"],
            "sender": final["Sender"], "markers": len(markers)}


def network(source, target, low, high):
    sends = source["trace"]["Sends"]
    receives = {(p["Ssrc"], p["Timestamp"]): p for p in target["trace"]["Receives"]}
    matched = [(receives[p["Ssrc"], p["Timestamp"]]["At"] - p["SendEnd"]) * 1000
               for p in sends if sends[0]["SendEnd"] + 5 < p["SendEnd"] < sends[-1]["SendEnd"] - 1
               and (p["Ssrc"], p["Timestamp"]) in receives]
    return {"minimumMs": stats([d - high * 1000 for d in matched]),
            "maximumMs": stats([d - low * 1000 for d in matched]),
            "negativeUpperBounds": sum(d - low * 1000 < 0 for d in matched)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    paths = sorted(args.directory.glob("*-call.json"))
    clock_paths = sorted(args.directory.glob("clock-*.json"))
    speech_paths = sorted((args.directory / "speech").glob("*-speech-*.json"))
    if not paths or args.output.resolve() in {p.resolve() for p in paths + clock_paths + speech_paths}:
        parser.error("Need inputs and a separate output path")
    summary = {"calls": [summarize(p) for p in paths], "network": [], "speech": []}
    clocks = [read(p) for p in clock_paths]
    if len(clocks) >= 2:
        # Conservative endpoint envelope. Its use between exchanges additionally
        # assumes clock offset stays inside it; this is not a drift guarantee.
        low, high = min(c["low"] for c in clocks), max(c["high"] for c in clocks)
        for suffix in ("align", "return"):
            pc, laptop = args.directory / f"pc-{suffix}-call.json", args.directory / f"laptop-{suffix}-call.json"
            if pc.exists() and laptop.exists():
                a, b = read(pc), read(laptop)
                summary["network"].append({"round": suffix, "offsetEnvelopeWidthMs": (high-low)*1000,
                    "pcToLaptop": network(a, b, low, high), "laptopToPc": network(b, a, -high, -low)})
    for path in speech_paths:
        document = read(path)
        if document.get("error") or not document.get("forceRelay") or not document.get("speechFixture"):
            raise ValueError(f"Incomplete speech trial: {path}")
        samples = document["samples"]
        if len(samples) < 110 or document["seconds"] != 120:
            raise ValueError(f"Short speech trial: {path}")
        final = samples[-1]
        audio = final["Peers"][0]["Audio"]
        detail = audio["RtpPlayout"]
        summary["speech"].append({"source": path.name, "reserveMs": document["reserveMs"],
            "queueMs": stats([s["Peers"][0]["Audio"]["QueueMs"] for s in samples[5:-1]]),
            "underfillMs": audio["UnderfillMs"], "concealedMs": detail["ConcealedMs"],
            "latePackets": detail["LatePackets"], "rebuffers": detail["Rebuffers"],
            "maxArrivalGapMs": max(s["Peers"][0]["Precision"]["ArrivalGapMaxMs"] or 0 for s in samples),
            "captureDiscontinuities": final["Microphone"]["CaptureDiscontinuities"],
            "render": audio["Render"], "sender": final["Sender"], "frames": final["Peers"][0]["TxFrames"]})
    summary["notes"] = ("Same-host differences use each host QPC. Injection begins after frame assembly; "
        "sampleToFrame describes timestamped source framing, not captured test waveform. "
        "Loopback QPC is a digital collection timestamp, not DAC time. Detector resolution 20ms. "
        "Network is send API completion to decrypted receive notification, not wire-only transit. "
        "SSH endpoint clock envelope assumes no intervening offset excursion or host restart. "
        "Separate percentiles must not be summed. Sequential short trials do not establish population performance.")
    args.output.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    for call in summary["calls"]:
        print(call["source"], {k: round(v["p50"], 3) if v["p50"] is not None else None
                               for k, v in call["stages"].items()},
              "PLC", call["concealedMs"], "underfill", call["underfillMs"])


if __name__ == "__main__":
    main()
