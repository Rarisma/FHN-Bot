namespace SummariseAnything;

public class Article
{
    public string Title { get; set; }
    public string Author { get; set; }
    public Uri Uri { get; set; }
    public DateTime? PublicationDate { get; set; }
    public string FeaturedImage { get; set; }
    public TimeSpan TimeToRead { get; set; }
    public ArticleType Type { get; set; }
    public string Content { get; set; }
    public string Summary { get; set; }
    //public double Impact { get; set; }

    // Convenience property if a string URL is preferred.
    public string URL => Uri?.ToString();
}
public enum ArticleType
{
    Article,
    Video,
    File
}