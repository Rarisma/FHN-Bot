mod article;

use std::error::Error;
use std::fs::File;
use std::io::{BufRead, BufReader};
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};
use chrono::{TimeZone, Utc};
use html2text::from_read;
use reqwest::Client;
use rss::Channel;
use rusqlite::{params, Connection};
use tokio::sync::Semaphore;
use url::Url;
use article::Article;

const RSS_FILE: &str = "/home/rari/Feeds.txt";
const DB_FILE: &str = "/home/rari/Articles.db";
const SKIP_FIRST: usize = 2000; // Change this value to skip the first x feeds

/// A simple metrics tracker.
struct Metrics {
    grand_total: AtomicUsize,
    already_seen: AtomicUsize,
    per_hour: AtomicUsize,
    current_hour: AtomicUsize,
}

impl Metrics {
    fn new() -> Self {
        let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap();
        let current_hour = (now.as_secs() / 3600) as usize;
        Metrics {
            grand_total: AtomicUsize::new(0),
            already_seen: AtomicUsize::new(0),
            per_hour: AtomicUsize::new(0),
            current_hour: AtomicUsize::new(current_hour),
        }
    }

    fn increment_grand_total(&self) {
        self.grand_total.fetch_add(1, Ordering::SeqCst);
        self.increment_per_hour();
    }

    fn increment_already_seen(&self) {
        self.already_seen.fetch_add(1, Ordering::SeqCst);
    }

    fn increment_per_hour(&self) {
        let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap();
        let hour = now.as_secs() / 3600;
        let stored_hour = self.current_hour.load(Ordering::SeqCst) as u64;
        if hour > stored_hour {
            self.current_hour.store(hour as usize, Ordering::SeqCst);
            self.per_hour.store(0, Ordering::SeqCst);
        }
        self.per_hour.fetch_add(1, Ordering::SeqCst);
    }

    fn get_metrics(&self) -> (usize, usize, usize) {
        (
            self.grand_total.load(Ordering::SeqCst),
            self.per_hour.load(Ordering::SeqCst),
            self.already_seen.load(Ordering::SeqCst),
        )
    }
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn Error + Send + Sync + 'static>> {
    println!("Loading feeds from {}.", RSS_FILE);
    let feed_urls = load_feed_urls(RSS_FILE).unwrap_or(Vec::new());
    println!("Loaded {} feed URL(s).", feed_urls.len());

    let mut conn = Connection::open(DB_FILE)?;
    create_table(&conn)?;

    let metrics = Arc::new(Metrics::new());

    // Skip the first SKIP_FIRST feeds
    for feed_url in feed_urls.iter().skip(SKIP_FIRST) {
        match process_feed(feed_url, &conn, metrics.clone()).await {
            Ok(articles) => {
                println!("Bulk inserting {} articles from feed {}", articles.len(), feed_url);
                if let Err(e) = bulk_insert_articles(&mut conn, &articles) {
                    eprintln!("Error bulk inserting articles for feed {}: {}", feed_url, e);
                }
            }
            Err(e) => eprintln!("Error processing feed {}: {}", feed_url, e),
        }

        let (grand_total, per_hour, already_seen) = metrics.get_metrics();
        println!(
            "Metrics: Grand Total: {}, This Hour: {}, Already Seen: {}",
            grand_total, per_hour, already_seen
        );
    }
    Ok(())
}

fn load_feed_urls<P: AsRef<Path>>(path: P) -> Result<Vec<String>, Box<dyn Error>> {
    let file = File::open(path)?;
    let reader = BufReader::new(file);
    let urls = reader.lines().collect::<Result<Vec<_>, _>>()?;
    Ok(urls)
}

/// Processes a single feed, scraping articles concurrently (up to 10 at a time).
/// Returns a Vec<Article> of successfully scraped articles.
async fn process_feed(
    feed_url: &str,
    conn: &Connection,
    metrics: Arc<Metrics>,
) -> Result<Vec<Article>, Box<dyn Error + Send + Sync + 'static>> {
    let response = reqwest::get(feed_url).await?;
    let content = response.bytes().await?;
    let channel = Channel::read_from(&content[..])?;
    println!("Loaded feed: {}", channel.title());

    let existing_urls = get_existing_urls(conn)?;
    let semaphore = Arc::new(Semaphore::new(10));
    let mut tasks = Vec::new();

    for item in channel.items() {
        let link = item.link().unwrap_or("");
        if existing_urls.contains(&link.to_string()) {
            println!("Article already in DB: {}", item.title().unwrap_or("No title"));
            metrics.increment_already_seen();
            continue;
        }

        let article_url = match Url::parse(link) {
            Ok(url) => url,
            Err(e) => {
                eprintln!("Invalid URL {}: {}", link, e);
                continue;
            }
        };

        let permit = semaphore.clone().acquire_owned().await?;
        let task = tokio::spawn(async move {
            let _permit = permit;
            scrape_article(article_url).await
        });
        tasks.push(task);
    }

    let mut articles = Vec::new();
    for task in tasks {
        match task.await {
            Ok(Ok(article)) => {
                println!("Scraped article: {}", article.title);
                articles.push(article);
                metrics.increment_grand_total();
            }
            Ok(Err(e)) => eprintln!("Error scraping article: {}", e),
            Err(e) => eprintln!("Task join error: {}", e),
        }
    }
    Ok(articles)
}

async fn scrape_article(url: Url) -> Result<Article, Box<dyn Error + Send + Sync + 'static>> {
    let client = Client::new();
    let scraper = article_scraper::ArticleScraper::new(None).await;
    let scraped = scraper.parse(&url, false, &client, None).await?;

    let html_content = scraped.html.ok_or("No HTML content found")?;
    let plain_text = from_read(html_content.as_bytes(), 8000);

    let publish_date = if let Some(date) = scraped.date {
        date.to_string()
    } else {
        Utc.timestamp_nanos(0).to_string()
    };

    Ok(Article {
        title: scraped.title.unwrap_or_else(|| "No title".to_string()),
        content: plain_text.unwrap_or("No content".to_string()),
        publish_date,
        top_image: scraped.thumbnail_url.unwrap_or_default(),
        keywords: String::new(),
        url: url.to_string(),
    })
}

fn bulk_insert_articles(conn: &mut Connection, articles: &[Article]) -> rusqlite::Result<()> {
    let tx = conn.transaction()?;
    {
        let mut stmt = tx.prepare(
            "INSERT INTO articles (title, article_text, publish_date, top_image, keywords, url)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        )?;
        for article in articles {
            stmt.execute(params![
                article.title,
                article.content,
                article.publish_date,
                article.top_image,
                article.keywords,
                article.url,
            ])?;
        }
    }
    tx.commit()
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
