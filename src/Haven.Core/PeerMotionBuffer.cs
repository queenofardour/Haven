using System.Numerics;

namespace Haven.Core;

/// <summary>
/// Bounded interpolation of a peer's movement. Call under the peer's history lock.
/// Samples are placed on the relay's clock when the frame carries the time the relay forwarded it (Unix ms): arrival
/// times carry all the jitter of the relay-to-us leg and of our own frame timing, which showed as speed wobble and
/// stop-then-snap. That clock is mapped to ours by the smallest transit seen (it creeps up slowly, following drift).
/// Without a stamp, arrival times are used.
/// </summary>
public sealed class PeerMotionBuffer<T> where T : class
{
    private readonly List<(T Value, Vector3 Position, double Time)> frames = [];
    private long sequence = long.MinValue;
    private double delay = .15, lastRender = double.NegativeInfinity, offset = double.NaN;
    private const long SenderClockFloor = 1_000_000_000_000; // Unix ms since 2001: a real stamp.

    public bool Add(T value, Vector3 position, long seq, double now, bool isMotionLane, long stampMs = 0)
    {
        // State-only lanes inherit the last motion sequence/position; they are not new samples.
        if (!isMotionLane || seq <= sequence || !double.IsFinite(now)) return false;
        var time = now;
        var senderClock = stampMs > SenderClockFloor;
        if (senderClock)
        {
            var sent = stampMs / 1000.0;
            var transit = now - sent;
            offset = double.IsNaN(offset) || transit < offset ? transit : offset + Math.Min(.001, transit - offset);
            time = sent + offset;
            if (frames.Count > 0) time = Math.Max(time, frames[^1].Time + .001);
        }
        if (frames.Count > 0)
        {
            var previous = frames[^1];
            if (time < previous.Time) return false;
            if (Vector3.DistanceSquared(position, previous.Position) > 64)
            {
                frames.Clear(); lastRender = double.NegativeInfinity; delay = .15;
            }
            else if (senderClock)
            {
                // Enough delay for the latest lateness plus one 10 Hz interval, so there is always a next sample to
                // move toward; after a late packet it recovers within about a second (10 ms a sample).
                var lateness = Math.Max(0, now - time);
                delay = Math.Max(Math.Clamp(.1 + lateness + .03, .13, .35), delay - .01);
            }
            else
            {
                var gap = now - previous.Time;
                // Ignore idle heartbeats/outages. Absorb ordinary Internet jitter, then recover slowly.
                if (gap > .001 && gap < .5)
                    delay = Math.Max(Math.Clamp(gap + .05, .15, .30), delay - .002);
            }
        }
        sequence = seq;
        frames.Add((value, position, time));
        while (frames.Count > 2 && (frames.Count > 32 || frames[0].Time < now - 1)) frames.RemoveAt(0);
        return true;
    }

    public bool TrySample(double now, out T a, out T b, out float amount)
    {
        a = b = null!; amount = 0;
        if (frames.Count == 0) return false;
        // A growing jitter buffer may pause briefly, but must never rewind a walking character.
        var render = Math.Clamp(Math.Max(now - delay, lastRender), frames[0].Time, frames[^1].Time);
        lastRender = render;
        a = b = frames[0].Value;
        for (var i = 1; i < frames.Count; i++)
        {
            if (render > frames[i].Time) continue;
            a = frames[i - 1].Value; b = frames[i].Value;
            var span = frames[i].Time - frames[i - 1].Time;
            amount = span > .00001 ? (float)((render - frames[i - 1].Time) / span) : 1;
            return true;
        }
        a = b = frames[^1].Value;
        return true;
    }
}
