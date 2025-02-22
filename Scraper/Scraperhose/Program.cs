using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceModel.Syndication;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Scraperhose
{
    class Program
    {
        const string RSS_FILE = "/home/rari/Feeds.txt";
        const int SKIP_FIRST = 0; //14314;
        const int LOW_LIMIT = 3;
        const int HIGH_LIMIT = 10;
        const int ORGIA_LIMIT = 25;

        // Start in low mode.
        static volatile int currentConcurrencyLimit = LOW_LIMIT;
        static volatile int runningTaskCount = 0;
        static int feedsProcessed = 0;
        static int totalFeeds = 0;

        static readonly HttpClient httpClient = new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });

        // Thread-safe collection for all URLs that have been processed.
        static readonly ConcurrentBag<string> allExistingUrls = new();

        static async Task Main(string[] args)
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/133.0.0.0 Safari/537.36");

            string connectionString = "server=localhost;port=3306;user=root;password=Mavik;database=Research;";
            await DB.InitializeDB(connectionString);

            // Preload existing URLs.
            var initialUrls = await DB.GetExistingUrls(connectionString);
            foreach (var url in initialUrls)
            {
                allExistingUrls.Add(url);
            }

            var feedUrls = LoadFeedUrls(RSS_FILE);
            totalFeeds = feedUrls.Count - SKIP_FIRST;

            var metrics = new Metrics();

            // Record start time to compute per-second rates.
            DateTime startTime = DateTime.UtcNow;

            // Start background tasks for mode toggle and performance monitoring.
            var cts = new CancellationTokenSource();
            var toggleTask = Task.Run(() => ListenForModeToggle(cts.Token));
            var perfMetrics = new PerformanceMetrics();
            var perfTask = Task.Run(() => perfMetrics.StartMonitoring(cts.Token));

            var tasks = new List<Task>();
            foreach (var feedUrl in feedUrls.Skip(SKIP_FIRST))
            {
                await WaitForSlotAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var articles = await ProcessFeed(feedUrl, connectionString, metrics);
                        if (articles.Count > 0)
                        {
                            await DB.BulkInsertArticles(connectionString, articles);
                        }
                    }
                    catch { }
                    finally
                    {
                        int processed = Interlocked.Increment(ref feedsProcessed);
                        var (grand, perHour, already, _) = metrics.GetMetrics();
                        var elapsedTime = DateTime.UtcNow - startTime;
                        double feedsPerSec = processed / elapsedTime.TotalSeconds;
                        double articlesPerSec = grand / elapsedTime.TotalSeconds;
                        Console.WriteLine(
                            $"Metrics: Grand: {grand}, This Hour: {perHour}, Already Seen: {already}, " +
                            $"Elapsed: {elapsedTime}, Progress: {processed}/{totalFeeds}, " +
                            $"Mode: {(currentConcurrencyLimit == LOW_LIMIT ? "Low" : (currentConcurrencyLimit == HIGH_LIMIT ? "High" : "ORGIA"))}, " +
                            $"CPU: {perfMetrics.CurrentCpuUsage:F1}% (avg {perfMetrics.AverageCpuUsage:F1}%), " +
                            $"RAM: {perfMetrics.CurrentMemoryUsage:F1} MB (avg {perfMetrics.AverageMemoryUsage:F1} MB), " +
                            $"Feeds/sec: {feedsPerSec:F2}, Articles/sec: {articlesPerSec:F2}");
                        Interlocked.Decrement(ref runningTaskCount);
                        // Force garbage collection at the end of this feed processing.
                        GC.Collect();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            cts.Cancel();
            try { await toggleTask; } catch (OperationCanceledException) { }
            try { await perfTask; } catch (OperationCanceledException) { }
        }

        static List<string> LoadFeedUrls(string path) =>
            File.ReadAllLines(path).ToList();

        static async Task<List<Article>> ProcessFeed(string feedUrl, string connectionString, Metrics metrics)
        {
            var articles = new List<Article>();
            string feedContent = await httpClient.GetStringAsync(feedUrl);
            using (var reader = XmlReader.Create(new StringReader(feedContent)))
            {
                var feed = SyndicationFeed.Load(reader);
                if (feed == null)
                    return articles;

                // Use the first link in each feed item.
                var articleUrls = feed.Items
                    .Where(item => item.Links.Any())
                    .Select(item => item.Links.First().Uri.ToString())
                    .ToList();

                var articleTasks = new List<Task<Article>>();
                foreach (var url in articleUrls)
                {
                    if (allExistingUrls.Contains(url))
                    {
                        metrics.IncrementAlreadySeen();
                        continue;
                    }
                    articleTasks.Add(ScrapeArticle(url, metrics));
                }

                var results = await Task.WhenAll(articleTasks);
                var newArticles = results.Where(a => a != null).ToList();
                // Add the new URLs to the global collection.
                foreach (var article in newArticles)
                {
                    allExistingUrls.Add(article.Url);
                }
                articles.AddRange(newArticles);
            }
            return articles;
        }

        static async Task<Article> ScrapeArticle(string url, Metrics metrics)
        {
            try
            {
                var reader = new SmartReader.Reader(url);
                var srArticle = await reader.GetArticleAsync();
                if (!srArticle.IsReadable)
                {
                    return null;
                }
                metrics.IncrementGrandTotal();
                return new Article
                {
                    Title = srArticle.Title,
                    Content = srArticle.TextContent,
                    PublishDate = srArticle.PublicationDate?.ToString("o") ?? DateTime.UtcNow.ToString("o"),
                    TopImage = srArticle.FeaturedImage,
                    Url = url
                };
            }
            catch
            {
                return new Article
                {
                    Title = "No title",
                    Content = "Article text unavailable",
                    PublishDate = DateTime.UnixEpoch.ToString("o"),
                    TopImage = string.Empty,
                    Url = url
                };
            }
        }

        // Wait until the current number of tasks is below the current limit.
        static async Task WaitForSlotAsync()
        {
            while (true)
            {
                if (Volatile.Read(ref runningTaskCount) < Volatile.Read(ref currentConcurrencyLimit))
                {
                    Interlocked.Increment(ref runningTaskCount);
                    break;
                }
                await Task.Delay(50);
            }
        }

        // Listen for key presses. Press O for orgia mode (50), H for high mode (20) and L for low mode (3).
        static async Task ListenForModeToggle(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.O)
                    {
                        currentConcurrencyLimit = ORGIA_LIMIT;
                    }
                    else if (key.Key == ConsoleKey.H)
                    {
                        currentConcurrencyLimit = HIGH_LIMIT;
                    }
                    else if (key.Key == ConsoleKey.L)
                    {
                        currentConcurrencyLimit = LOW_LIMIT;
                    }
                }
                try
                {
                    await Task.Delay(100, token);
                }
                catch (TaskCanceledException) { break; }
            }
        }
    }

    // A simple performance monitor sampling CPU and memory usage.
    public class PerformanceMetrics
    {
        public double CurrentCpuUsage { get; private set; }
        public double AverageCpuUsage { get; private set; }
        public double CurrentMemoryUsage { get; private set; }
        public double AverageMemoryUsage { get; private set; }

        private double totalCpuUsage;
        private double totalMemoryUsage;
        private int sampleCount;

        public async Task StartMonitoring(CancellationToken token)
        {
            var process = Process.GetCurrentProcess();
            TimeSpan prevCpuTime = process.TotalProcessorTime;
            DateTime prevTime = DateTime.UtcNow;

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                process.Refresh();
                TimeSpan currentCpuTime = process.TotalProcessorTime;
                DateTime currentTime = DateTime.UtcNow;

                double cpuUsedMs = (currentCpuTime - prevCpuTime).TotalMilliseconds;
                double elapsedMs = (currentTime - prevTime).TotalMilliseconds;
                double cpuUsage = (cpuUsedMs / (elapsedMs * Environment.ProcessorCount)) * 100.0;
                CurrentCpuUsage = cpuUsage;

                long memoryBytes = process.WorkingSet64;
                double memoryMB = memoryBytes / (1024.0 * 1024.0);
                CurrentMemoryUsage = memoryMB;

                totalCpuUsage += cpuUsage;
                totalMemoryUsage += memoryMB;
                sampleCount++;

                AverageCpuUsage = totalCpuUsage / sampleCount;
                AverageMemoryUsage = totalMemoryUsage / sampleCount;

                prevCpuTime = currentCpuTime;
                prevTime = currentTime;
            }
        }
    }
}
