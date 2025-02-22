use std::{
    error::Error,
    fs::File,
    io::{BufRead, BufReader},
    path::Path,
    sync::Arc,
    time::{Duration, Instant},
};

use futures_util::{stream, StreamExt};
use log::{debug, error, info};
use mysql_async::Pool;
use reqwest::{blocking::Client as BlockingClient, Client};
use sysinfo::{System};
use tokio::time;
use url::Url;

use feed_rs::parser;

mod db;
mod metrics;
use metrics::Metrics;

const RSS_FILE: &str = "/home/rari/Feeds.txt";
const SKIP_FIRST: usize = 555;
const VERBOSE: bool = true;

macro_rules! vlog {
    ($verbose:expr, $($arg:tt)*) => {
        if $verbose {
            debug!($($arg)*);
        }
    };
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    env_logger::init();
    let rt = tokio::runtime::Builder::new_multi_thread()
        .enable_io()
        .enable_time()
        .build()?;
    rt.block_on(async_main())?;
    Ok(())
}

async fn async_main() -> Result<(), Box<dyn Error + Send + Sync>> {
    std::panic::set_hook(Box::new(|panic_info| {
        eprintln!("Global panic hook caught: {panic_info}");
    }));

    let start_time = Instant::now();
    info!("Starting feed scanning process.");

    let client = Client::builder()
        .user_agent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) \
                     AppleWebKit/537.36 (KHTML, like Gecko) \
                     Chrome/133.0.0.0 Safari/537.36")
        .build()?;

    let feed_urls: Vec<String> = load_feed_urls(RSS_FILE)?;
    let pool = Pool::new("mysql://root:Mavik@localhost:3306/Research");
    db::initialize_db(&pool).await?;
    info!("Database initialised.");

    let metrics = Arc::new(Metrics::new());

    // Spawn a background task to print metrics every 5 seconds.
    {
        let metrics = Arc::clone(&metrics);
        let start_time = Instant::now();
        tokio::spawn(async move {
            let mut sys = System::new_all();
            let mut interval = time::interval(Duration::from_secs(5));
            loop {
                interval.tick().await;
                sys.refresh_all();
              //  let cpu_usage = sys.global_cpu_info().cpu_usage();
                let memory_usage = sys.used_memory(); // in KB
                let found = metrics.found.load(std::sync::atomic::Ordering::Relaxed);
                let already_saw = metrics.already_saw.load(std::sync::atomic::Ordering::Relaxed);
                let elapsed = start_time.elapsed();
                info!(
                    "Metrics - Found: {}, Already saw: {}, Run time: {:.2?}, RAM: {} KB",
                    found, already_saw, elapsed/*, cpu_usage*/, memory_usage
                );
            }
        });
    }

    info!("Scanning {} RSS feeds.", feed_urls.len());
    let concurrency_limit = 50;

    stream::iter(feed_urls.into_iter().skip(SKIP_FIRST))
        .map(|feed_url| {
            let pool_clone = pool.clone();
            let client_clone = client.clone();
            let metrics_clone = Arc::clone(&metrics);
            async move {
                match process_feed(&feed_url, pool_clone, client_clone, metrics_clone).await {
                    Ok(()) => info!("Processed feed: {}", feed_url),
                    Err(e) => error!("Error processing feed {}: {}", feed_url, e),
                }
            }
        })
        .buffer_unordered(concurrency_limit)
        .for_each(|_| async {})
        .await;

    pool.disconnect().await?;
    info!("Program finished in {:.2?}.", start_time.elapsed());
    Ok(())
}

fn load_feed_urls<P: AsRef<Path>>(path: P) -> Result<Vec<String>, Box<dyn Error + Send + Sync>> {
    let file = File::open(path)?;
    let reader = BufReader::new(file);
    let urls = reader.lines().collect::<Result<Vec<_>, _>>()?;
    Ok(urls)
}

fn download_rss_feed(
    url: &str,
    blocking_client: &BlockingClient,
) -> Result<Vec<String>, Box<dyn Error + Send + Sync>> {
    let response = blocking_client.get(url).send()?;
    let responsestat = blocking_client.get(url).send()?;
    if !response.status().is_success() {
        error!("Non-success status {} for feed URL: {}", response.status(), url);
        return Ok(vec![]);
    }
    let bytes = response.bytes()?;
    debug!("download_rss_feed: status={} len={} url={}", responsestat.status(), bytes.len(), url);
    let feed = parser::parse(&bytes[..])?;
    let mut article_urls = Vec::new();
    for entry in feed.entries {
        if let Some(link) = entry.links.first() {
            article_urls.push(link.href.clone());
        }
    }
    println!("scraped {url} got {} article URLs", article_urls.len());
    Ok(article_urls)
}

/// Process a feed:
/// 1. Download the feed and extract URLs.
/// 2. Retrieve existing URLs from Raw_Articles and Discovered.
/// 3. Insert any new URLs into the Discovered table and update metrics.
async fn process_feed(
    feed_url: &str,
    pool: Pool,
    client: Client,
    metrics: Arc<Metrics>,
) -> Result<(), Box<dyn Error + Send + Sync>> {
    vlog!(VERBOSE, "Fetching feed: {}", feed_url);

    let article_urls: Vec<String> = tokio::task::spawn_blocking({
        let feed_url = feed_url.to_string();
        move || {
            let blocking_client = reqwest::blocking::Client::builder()
                .user_agent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) \
                             AppleWebKit/537.36 (KHTML, like Gecko) \
                             Chrome/133.0.0.0 Safari/537.36")
                .build()?;
            download_rss_feed(&feed_url, &blocking_client)
        }
    })
        .await??;

    vlog!(VERBOSE, "Loaded {} URLs from: {}", article_urls.len(), feed_url);
    let existing_urls = db::get_existing_urls(&pool).await?;
    let new_urls: Vec<String> = article_urls
        .into_iter()
        .filter(|url| {
            if existing_urls.contains(url) {
                metrics.increment_already_seen();
                false
            } else {
                true
            }
        })
        .collect();

    if !new_urls.is_empty() {
        info!("Inserting {} new discovered URLs from feed: {}", new_urls.len(), feed_url);
        db::bulk_insert_discovered(&pool, &new_urls).await?;
        metrics.increment_found(new_urls.len());
    }

    Ok(())
}
