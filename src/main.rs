use std::fs::File;
use std::{
    io::{prelude::*, BufReader},
    path::Path,
};
use std::error::Error;
use rss::Channel;
use reqwest::Client;
use url::Url;
use article_scraper::ArticleScraper;
use html2text::from_read;
use rusqlite::{params, Connection, Result};

const RSS_FILE: &str = "/mnt/c/Users/RARI/Desktop/verified_feeds.txt";
const DB_FILE: &str = "/mnt/c/Users/RARI/Desktop/rss_articles.db";

/* TODO:
- Parallel
- Error handling
- URL checking
*/


#[tokio::main]
async fn main() -> Result<(), Box<dyn Error>> {
    println!("Loading feeds.");
    let lines = lines_from_file(RSS_FILE);
    println!("Loaded {} feeds.", lines.len());
    
    for line in lines {
        if let Err(e) = read_feed(&line).await {
            eprintln!("Error processing feed {}: {}", line, e);
        }
    }
    Ok(())
}

async fn read_feed(url: &str) -> Result<(), Box<dyn Error>> {
    let content = reqwest::get(url).await?.bytes().await?;
    let feed: Channel = Channel::read_from(&content[..])?;
    println!("Loaded feed: {}", feed.title);
    let DB = Connection::open(DB_FILE)?;
    let urls = get_urls(&DB)?;
    println!("Loaded {} articles from DB.", urls.len());
    let client = Client::new();

    for item in feed.items() {

        //Check if already in DB
        if urls.contains(&item.link().unwrap_or("").to_string()) {
            println!("Article already in DB: {}", item.title().unwrap_or("No title"));
            continue;
        }

        let scraper = ArticleScraper::new(None).await;
        let title = item.title().unwrap_or("No title");
        let link = item.link().ok_or("No link found")?;
        let url = Url::parse(link)?;
        let article = scraper.parse(&url, false, &client, None).await?;

        let plain_text = from_read(article.html.expect("REASON").as_bytes(), 8000)
            .unwrap_or_else(|_| "Failed to extract text".to_string());
            
        let pub_date = item.pub_date().unwrap_or("");
        update_article(&DB, title, &plain_text, pub_date, "", "", &url.to_string())?;
        println!("Inserted article: {}", title);
    }
    Ok(())
}

fn lines_from_file(filename: impl AsRef<Path>) -> Vec<String> {
    let file = File::open(filename).expect("no such file");
    let buf = BufReader::new(file);
    buf.lines()
        .map(|l| l.expect("Could not parse line"))
        .collect()
}

pub fn update_article( conn: &Connection, title: &str, article_text: &str,
    publish_date: &str, top_image: &str, keywords: &str, url: &str) -> Result<usize> {
        conn.execute(
            "INSERT INTO articles (title, article_text, publish_date, top_image, keywords, url)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![title, article_text, publish_date, top_image, keywords, url],
        )
}

fn get_urls(conn: &Connection) -> Result<Vec<String>> {
    print!("Getting urls from DB...");
    let mut stmt = conn.prepare("SELECT url FROM articles")?;
    println!("running.");
    let urls = stmt.query_map([], |row| row.get(0))?;
    println!("mapping.");
    let mut url_vec = Vec::new();
    for url in urls {
        url_vec.push(url?);
    }
    println!("Got {} URLs.", url_vec.len());

    Ok(url_vec)
}