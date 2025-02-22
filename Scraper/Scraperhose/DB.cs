using MySqlConnector;

namespace Scraperhose;

    static class DB
    {
        public static async Task InitializeDB(string connectionString)
        {
            using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"CREATE DATABASE IF NOT EXISTS Research;
                  USE Research;
                  CREATE TABLE IF NOT EXISTS Raw_Articles (
                      title TEXT NOT NULL,
                      article_text MEDIUMTEXT NOT NULL,
                      publish_date TEXT,
                      top_image TEXT,
                      url VARCHAR(512) NOT NULL PRIMARY KEY
                  );";
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task<HashSet<string>> GetExistingUrls(string connectionString)
        {
            var urls = new HashSet<string>();
            using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "USE Research; SELECT url FROM Raw_Articles;";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    urls.Add(reader.GetString(0));
                }
            }

            return urls;
        }

        public static async Task BulkInsertArticles(string connectionString, List<Article> articles)
        {
            using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync();
            using var transaction = await conn.BeginTransactionAsync();
            foreach (var article in articles)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"INSERT IGNORE INTO Raw_Articles 
                                    (title, article_text, publish_date, top_image, url)
                                    VALUES (@title, @content, @publish_date, @top_image, @url);";
                cmd.Parameters.AddWithValue("@title", article.Title);
                cmd.Parameters.AddWithValue("@content", article.Content);
                cmd.Parameters.AddWithValue("@publish_date", article.PublishDate);
                cmd.Parameters.AddWithValue("@top_image", article.TopImage);
                cmd.Parameters.AddWithValue("@url", article.Url);
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
    }