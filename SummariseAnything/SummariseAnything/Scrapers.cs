using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Google.Apis.Services;
using SmartReader;
using YoutubeTranscriptApi;
using Google.Apis.YouTube.v3;
namespace SummariseAnything;

public static class Scrapers
{
    public static async Task<Article> Scrape(string Source)
    {
        //Custom scrape for youtube videos
        if (IsYoutube(Source))
        {
            var videoId = ExtractVideoId(Source);
            return await GetYoutubeVideo(videoId);
        }
        else //Scrape Website by default
        {
            var x = await Reader.ParseArticleAsync(Source);
            return new Article()
            {
                Uri = new Uri(Source),
                Title = x.Title,
                Author = x.Author ?? "Unknown",
                PublicationDate = x.PublicationDate ?? DateTime.Now,
                FeaturedImage = x.FeaturedImage,
                TimeToRead = x.TimeToRead,
                Type = ArticleType.Article,
                Content = x.Content
            };
        }
    }
    
    static bool IsYoutube(string Source)
    {
        return Regex.Match(Source, @"(?:youtu\.be\/|youtube\.com.*(?:\/|v=))([^?&""'>]+)",
            RegexOptions.IgnoreCase).Success;
    }
    
    public static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // This pattern matches common YouTube URL formats.
        string pattern = @"(?:youtu\.be\/|youtube\.com.*(?:\/|v=))([^?&""'>]+)";
        var match = Regex.Match(url, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
    
    public static string GetTranscript(string videoId)
    {
        YouTubeTranscriptApi transcriptApi = new();
        try
        {
            var transcriptItems = transcriptApi.GetTranscript(videoId, new[] { "en" });
            var sb = new StringBuilder();
            foreach (var item in transcriptItems)
            {
                sb.Append(item.Text).Append(" ");
            }
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"Error fetching transcript: {ex.Message}";
        }
    }
    
    public static async Task<Article> GetYoutubeVideo(string videoId)
    {
        using var youtube = new YouTubeService(new BaseClientService.Initializer()
        {
            ApiKey = Config.Load().YoutubeAPIKey
        });

        var videoRequest = youtube.Videos.List("snippet,contentDetails");
        videoRequest.Id = videoId;
    
        var response = await videoRequest.ExecuteAsync();
        var video = response.Items.FirstOrDefault();
    
        if (video == null)
            return null;

        var duration = XmlConvert.ToTimeSpan(video.ContentDetails.Duration);
    
        return new Article
        {
            Title = video.Snippet.Title,
            Author = video.Snippet.ChannelTitle,
            Uri = new Uri($"https://youtube.com/watch?v={videoId}"),
            PublicationDate = video.Snippet.PublishedAt,
            FeaturedImage = video.Snippet.Thumbnails.Maxres?.Url ?? 
                            video.Snippet.Thumbnails.High?.Url ?? 
                            video.Snippet.Thumbnails.Medium?.Url,
            TimeToRead = duration,
            Type = ArticleType.Video,
            Content = GetTranscript(videoId)
        };
    }
}