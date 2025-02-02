mod article;

use std::error::Error;
use std::fs::File;
use std::io::{BufRead, BufReader};
use std::path::Path;
use chrono::{TimeZone, Utc};
use html2text::from_read;
use reqwest::Client;
use rss::Channel;
use rusqlite::{params, Connection};
use url::Url;
use article::Article;
const RSS_FILE: &str = "/home/rari/Feeds.txt";
const DB_FILE: &str = "/home/rari/Articles.db";


#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    println!("Loading feeds from {}.", RSS_FILE);
    let feed_urls = load_feed_urls(RSS_FILE)?;
    println!("Loaded {} feed URL(s).", feed_urls.len());

    let conn = Connection::open(DB_FILE)?;
    create_table(&conn)?;

    for feed_url in feed_urls {
        if let Err(e) = process_feed(&feed_url, &conn).await {
            eprintln!("Error processing feed {}: {}", feed_url, e);
        }
    }
    Ok(())
}

fn load_feed_urls<P: AsRef<Path>>(path: P) -> Result<Vec<String>, Box<dyn Error>> {
    let file = File::open(path)?;
    let reader = BufReader::new(file);
    let urls = reader
        .lines()
        .collect::<Result<Vec<_>, _>>()?;
    Ok(urls)
}

async fn process_feed(feed_url: &str, conn: &Connection) -> Result<(), Box<dyn Error>> {
    let response = reqwest::get(feed_url).await?;
    let content = response.bytes().await?;
    let channel = Channel::read_from(&content[..])?;
    println!("Loaded feed: {}", channel.title());

    let existing_urls = get_existing_urls(conn)?;
    for item in channel.items() {
        let link = item.link().unwrap_or("");
        if existing_urls.contains(&link.to_string()) {
            println!(
                "Article already in DB: {}",
                item.title().unwrap_or("No title")
            );
            continue;
        }

        let article_url = match Url::parse(link) {
            Ok(url) => url,
            Err(e) => {
                eprintln!("Invalid URL {}: {}", link, e);
                continue;
            }
        };

        match scrape_article(article_url).await {
            Ok(article) => {
                println!("Inserting article: {}", article.title);
                insert_article(conn, &article)?;
            }
            Err(e) => {
                eprintln!("Error scraping article {}: {}", link, e);
            }
        }
    }
    Ok(())
}

async fn scrape_article(url: Url) -> Result<Article, Box<dyn Error>> {
    let client = Client::new();
    let scraper = article_scraper::ArticleScraper::new(None).await;
    let scraped = scraper.parse(&url, false, &client, None).await?;

    let html_content = scraped.html.ok_or("No HTML content found")?;
    
    // Extract plaintext
    let plain_text = from_read(html_content.as_bytes(), 8000);

    // Extract publish date
    let publish_date = if let Some(date) = scraped.date {
        date.to_string()
    } 
    // Default to epoch if no date is found
    else { Utc.timestamp_nanos(0).to_string() };

    Ok(Article {
        title: scraped.title.unwrap_or_else(|| "No title".to_string()),
        content: plain_text.unwrap_or("No content".to_string()),
        publish_date,
        top_image: scraped.thumbnail_url.unwrap_or_default(),
        keywords: String::new(),
        url: url.to_string(),
    })
}

fn insert_article(conn: &Connection, article: &Article) -> rusqlite::Result<usize> {
    conn.execute(
        "INSERT INTO articles (title, article_text, publish_date, top_image, keywords, url)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![
            article.title,
            article.content,
            article.publish_date,
            article.top_image,
            article.keywords,
            article.url
        ],
    )
}

fn get_existing_urls(conn: &Connection) -> rusqlite::Result<Vec<String>> {
    let mut stmt = conn.prepare("SELECT url FROM articles")?;
    let url_iter = stmt.query_map([], |row| row.get(0))?;
    let mut urls = Vec::new();
    for url in url_iter {
        urls.push(url?);
    }
    Ok(urls)
}

fn create_table(conn: &Connection) -> rusqlite::Result<()> {
    conn.execute(
        "CREATE TABLE IF NOT EXISTS articles (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            article_text TEXT NOT NULL,
            publish_date TEXT,
            top_image TEXT,
            keywords TEXT,
            url TEXT NOT NULL UNIQUE
         )",
        [],
    )?;
    Ok(())
}
