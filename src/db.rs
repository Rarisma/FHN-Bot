use mysql_async::prelude::*;
use mysql_async::Pool;
use std::collections::HashSet;
use std::error::Error;

/// Create (if needed) the tables used by this scanner.
pub async fn initialize_db(pool: &Pool) -> Result<(), Box<dyn Error + Send + Sync>> {
    let mut conn = pool.get_conn().await?;
    // Create Raw_Articles table (if not exists)

    // Create Discovered table (new)
    conn.query_drop(
        r"CREATE TABLE IF NOT EXISTS Discovered (
            id INT AUTO_INCREMENT PRIMARY KEY,
            url VARCHAR(2048) UNIQUE NOT NULL,
            discovered_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )",
    )
        .await?;
    conn.disconnect().await?;
    Ok(())
}

/// Retrieve all URLs already present in Raw_Articles and Discovered tables.
pub async fn get_existing_urls(pool: &Pool) -> Result<HashSet<String>, Box<dyn Error + Send + Sync>> {
    let mut conn = pool.get_conn().await?;
    let raw_articles: Vec<String> = conn.query("SELECT url FROM Raw_Articles").await?;
    let discovered: Vec<String> = conn.query("SELECT url FROM Discovered").await?;
    conn.disconnect().await?;
    let existing_urls = raw_articles.into_iter().chain(discovered.into_iter()).collect();
    Ok(existing_urls)
}

/// Bulk insert new URLs into the Discovered table.
pub async fn bulk_insert_discovered(pool: &Pool, urls: &[String]) -> Result<(), Box<dyn Error + Send + Sync>> {
    let mut conn = pool.get_conn().await?;
    let stmt = conn.prep(r"INSERT IGNORE INTO Discovered (url) VALUES (:url)").await?;
    for url in urls {
        conn.exec_drop(&stmt, params! { "url" => url }).await?;
    }
    conn.disconnect().await?;
    Ok(())
}
