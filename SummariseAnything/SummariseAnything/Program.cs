using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Commands.Processors.SlashCommands.ArgumentModifiers;
using DSharpPlus.Commands.Processors.SlashCommands.Metadata;
using DSharpPlus.Commands.Processors.TextCommands;
using DSharpPlus.Commands.Trees;
using DSharpPlus.Entities;
using SmartReader;

namespace SummariseAnything;

public class Program
{
    public static readonly HttpClient Client = new();

    public static async Task Main(string[] args)
    {
        var config = Config.Load();
        DiscordClientBuilder builder = DiscordClientBuilder
            .CreateDefault(config.DiscordAPIKey,
                DiscordIntents.AllUnprivileged | TextCommandProcessor.RequiredIntents |
                SlashCommandProcessor.RequiredIntents | DiscordIntents.MessageContents);

        // Setup the commands extension
        builder.UseCommands((IServiceProvider serviceProvider, CommandsExtension extension) =>
        {
            extension.AddCommands([typeof(Program)]);
            SlashCommandProcessor Slashcommands = new(new()
            {
            });
        }, new()
        {
            // The default value is true, however it's shown here for clarity
            RegisterDefaultCommandProcessors = true,
        });

        DiscordClient client = builder.Build();
            
        await client.ConnectAsync();
        await Task.Delay(-1);
    }
    
    [InteractionAllowedContexts([DiscordInteractionContextType.Guild, DiscordInteractionContextType.BotDM, DiscordInteractionContextType.PrivateChannel])]
    [InteractionInstallType([DiscordApplicationIntegrationType.GuildInstall, DiscordApplicationIntegrationType.UserInstall])]
    [Command("summarise")]
    [Description("Summarise Anything...")]
    public static async Task Summarise(CommandContext ctx, string url/*, [SlashChoiceProvider<LengthProvider>] int Length*/)
    {
        await ctx.DeferResponseAsync();
        Article article = await Scrapers.Scrape(url);
        if (article == null || string.IsNullOrWhiteSpace(article.Content))
        {
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent("Unable to scrape article from the provided URL."));
            return;
        }

        string prompt = BuildPrompt(article.Content);
        try
        {
            var config = Config.Load();
            string summary = await SummariseContentAsync(prompt, 500, config.GeminiAPIKey);
            await ctx.EditResponseAsync(CreateArticleEmbed(article,summary));
        }
        catch (Exception ex)
        {
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent($"Error during summarisation: {ex.Message}"));
        }
    }

    /// <summary>
    /// Creates dynamic prompt for the user to summarise the article.
    /// </summary>
    /// <param name="content">Article Content</param>
    /// <param name="tok">Length (unimplemented)</param>
    /// <returns>LLM Prompt</returns>
    static string BuildPrompt(string content)
    {
        return $"""
                   Create a concise summary for the provided story, your summary should be roughly two paragraphs.
                   Begin by directly stating the main points and essence of the narrative. Ensure that your response is 
                   formatted in plain text, without the use of any markdown or special formatting.
                                               
                   Guidelines:
                       Summaries should be easy for the average person to understand, if an article is
                       on a complicated subject, try to make it easy for the reader to understand.
                       Stories are within the public domain and cannot contain confidential information.
                       Stories relating to politics should be written from a purely neutral stance.
                       If multiple stories appear at once, summarize only the first article and ignore the rest.
                       Ignore advertisement content if present. The article is not an advertisement; do not summarize advertisements.
                       The summary must consist of only two paragraphs, summaries must be concise or will cut off.
                       Start directly with the narrative content, omitting any introductory phrases or labels.
                       Do not include any headings, titles, or labels within your response.
                       Write solely in plain text format; do not use markdown or any other formatting system.
                       
                    Content: {content}
               """;
    }
    
    static DiscordMessageBuilder CreateArticleEmbed(Article article, string Summary)
    {
        var builder = new DiscordEmbedBuilder();
        builder.Title = article.Title;
        builder.Description = Summary;
        builder.Color = new DiscordColor(0x880808);
        builder.Timestamp = article.PublicationDate ?? DateTime.Now;
        builder.Author = new()
        {
            Name = "by " + article.Author.ToString() + " on " + article.Uri.GetLeftPart(UriPartial.Authority) ?? "Unknown",
            Url = article.Uri.GetLeftPart(UriPartial.Authority) + "/favicon.ico" ?? ""
        };
        //
        builder.ImageUrl = article.FeaturedImage ?? "";
        builder.Footer = new()
        {
            Text = "Summaries are AI Generated and may be inaccurate",
            IconUrl =
                "https://cdn.discordapp.com/avatars/1253807985688313937/66e9341f42e7653c429013d18782bb21.webp?size=128"
        };
        
        //Time saved row
        var embed = builder
            .AddField("Time Saved:", Math.Floor(article.TimeToRead.TotalMinutes) + " Minutes", true)
            .Build();

        //View Article Button
        var linkButton = new DiscordLinkButtonComponent(
            article.URL,
            "View Article"
        );        
        
        //.AddField("Category:", article., true);c
        /*if (article.Type != Article.ArticleType.File)
            {
                string[] titleParts = article.Title.Split("|");
                string authorField = article.Author;
                if (titleParts.Length > 1)
                    authorField += " · " + titleParts[1];
                embed.WithAuthor(authorField, article.URL, favi);
            }*/

        return new DiscordMessageBuilder()
            .AddEmbed(embed)
            .AddComponents(linkButton);
    }
        
    public class LengthProvider : IChoiceProvider
    {
        private static readonly IReadOnlyList<DiscordApplicationCommandOptionChoice> Lengths =
        [
            new("Short", 0),
            new("Medium", 1),
            new("Long", 2),
            new("Simple", 3),
        ];

        public ValueTask<IEnumerable<DiscordApplicationCommandOptionChoice>> ProvideAsync(CommandParameter parameter) =>
            ValueTask.FromResult<IEnumerable<DiscordApplicationCommandOptionChoice>>(Lengths);
    }

    
    public static async Task<string> SummariseContentAsync(string prompt, int maxTokens, string geminiApiKey)
    {
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            safetySettings = new[]
            {
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
            },
            generationConfig = new
            {
                maxOutputTokens = maxTokens,
                temperature = 0.4
            }
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={geminiApiKey}");
        req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var resp = await Program.Client.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            string error = await resp.Content.ReadAsStringAsync();
            throw new($"API error: {resp.StatusCode}, {error}");
        }

        string respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        if (doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out JsonElement content) &&
            content.TryGetProperty("parts", out JsonElement parts) &&
            parts.GetArrayLength() > 0)
        {
            return parts[0].GetProperty("text").GetString() ?? "";
        }

        throw new("Unexpected API response format.");
    }
}
