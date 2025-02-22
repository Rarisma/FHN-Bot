using System.Diagnostics;

namespace Scraperhose;

public class Metrics
{
    private int grandTotal, alreadySeen, perHour, currentHour;
    private readonly Stopwatch stopwatch;

    public Metrics()
    {
        grandTotal = 0;
        alreadySeen = 0;
        perHour = 0;
        currentHour = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600);
        stopwatch = Stopwatch.StartNew();
    }

    public void IncrementGrandTotal()
    {
        Interlocked.Increment(ref grandTotal);
        IncrementPerHour();
    }

    public void IncrementAlreadySeen() =>
        Interlocked.Increment(ref alreadySeen);

    public void IncrementPerHour()
    {
        int nowHour = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600);
        if (nowHour > currentHour)
        {
            currentHour = nowHour;
            Interlocked.Exchange(ref perHour, 0);
        }
        Interlocked.Increment(ref perHour);
    }

    public (int grandTotal, int perHour, int alreadySeen, TimeSpan elapsed) GetMetrics() =>
        (grandTotal, perHour, alreadySeen, stopwatch.Elapsed);
}
