// Diagnostic-only helper compiled into the isolated SDK, never the product project.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;

namespace SIPSorcery.Net;

public static class UdpReceiveExperiment
{
    private static readonly object RegistrySync = new object();
    private static readonly List<State> States = new List<State>();
    internal static State Create(IPEndPoint local, byte[] buffer)
    {
        if (Environment.GetEnvironmentVariable("VOICE_SDK_RECEIVE_DIAGNOSTICS") != "1") return null;
        var state = new State(local?.ToString(), RuntimeHelpers.GetHashCode(buffer));
        lock (RegistrySync)
        {
            if (States.Count == 1024) throw new InvalidOperationException("Diagnostic receiver limit reached");
            States.Add(state);
        }
        return state;
    }

    public static object Snapshot()
    {
        lock (RegistrySync) return new {
            gate = Environment.GetEnvironmentVariable("VOICE_SDK_RECEIVE_GATE") ?? "original",
            qpcFrequency = Stopwatch.Frequency,
            receivers = States.Select(s => s.Snapshot()).ToArray(),
            limits = "Test SDK only. Operations track async receive through handler/drain completion, not physical packets. Bounded first 64 overlap starts; no payload data. Sync drain calls share their parent operation."
        };
    }

    internal sealed class State
    {
        private readonly object sync = new object();
        private readonly string local;
        private readonly int buffer;
        private readonly Dictionary<long, Operation> owners = new Dictionary<long, Operation>();
        private readonly Dictionary<long, Operation> observed = new Dictionary<long, Operation>();
        private readonly List<object> overlaps = new List<object>();
        private long nextId, starts, finishes, overlapStarts;
        private int outstanding, maxOutstanding, maxOwners;
        internal State(string local, int buffer) { this.local = local; this.buffer = buffer; }
        internal Operation Begin()
        {
            lock (sync)
            {
                var id = ++nextId; var operation = new Operation(this, id);
                starts++; outstanding++; owners.Add(id, operation);
                maxOutstanding = Math.Max(maxOutstanding, outstanding);
                maxOwners = Math.Max(maxOwners, owners.Count);
                if (owners.Count > 1)
                {
                    overlapStarts++;
                    if (overlaps.Count < 64)
                    {
                        overlaps.Add(new { id, buffer, at = Stopwatch.GetTimestamp(), outstanding, owners = owners.Keys.ToArray() });
                        foreach (var owner in owners.Values)
                            if (observed.Count < 128 || observed.ContainsKey(owner.Id)) observed[owner.Id] = owner;
                    }
                }
                return operation;
            }
        }
        internal void CompleteIo(Operation operation)
        {
            lock (sync)
            {
                if (operation.IoCompleted) return;
                operation.IoCompleted = true; operation.IoCompletedAt = Stopwatch.GetTimestamp(); outstanding--;
            }
        }
        internal void Finish(Operation operation)
        {
            lock (sync)
            {
                CompleteIo(operation);
                if (owners.Remove(operation.Id)) { finishes++; operation.FinishedAt = Stopwatch.GetTimestamp(); }
            }
        }
        internal object Snapshot()
        {
            lock (sync) return new {
                local, buffer, starts, finishes, outstanding, activeOwners = owners.Count,
                maxOutstanding, maxOwners, overlapStarts, overlaps = overlaps.ToArray(),
                operationLifetimes = observed.Values.Select(o => new {
                    id = o.Id, buffer, beginAt = o.BeginAt, ioCompletedAt = o.IoCompletedAt, finishedAt = o.FinishedAt
                }).ToArray()
            };
        }
    }

    internal sealed class Operation
    {
        private readonly State state;
        internal readonly long Id;
        internal readonly long BeginAt = Stopwatch.GetTimestamp();
        internal long IoCompletedAt, FinishedAt;
        internal bool IoCompleted;
        internal Operation(State state, long id) { this.state = state; Id = id; }
        internal void CompleteIo() => state.CompleteIo(this);
        internal void Finish() => state.Finish(this);
    }
}
