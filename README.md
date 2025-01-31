# Firehose News Real Time
This is an opensource implementation of Firehose, a private closed source news aggregator.
Currently the original server implementation is closed source due to potential legal issues.
Thus FHNRT was created to deal with the following issues:
 1) Firehose had a major scope creep which caused many development problems
 2) I have no server and can't leave my PC to summarise stories constantly as I'd like to use it and my roommates would like to live in an appartment that isn't super hot.
 3) Running firehose was too computationally expensive to summarise every story

So how do we fix these problems?
I present my latest nightmare, FHNRT.

### So how did old firehose work?
- Collect stories from RSS feeds
- Scrape them
- Summarise them with 4o-mini, Llama-8b or 3b
  (The Model changed frequently)
- Use the LLM to decide the story category and importance

### How will new FHN-RT work?
- Collect Stories from RSS feeds
- Determine importance and category on device with ML Models
- Scrape articles when asked to summarise or in reader mode
Note: Summarisation will be done on device with a model such as Gemmini Nano, Phi-3.5 or LLama-3.2 3B, but will be determined in the future.

#### Models
I have had some success with some private models I've trained on the Firehose Dataset (About 100k Articles) but this isn't quite good enough to use
so we need more data but we will get onto that later. 
Currently I plan on training two ML Models:
  - Taggiatelle 3
    Can pick between 8 tags such as Tech, Health, War, Law, etc.
  - Impactotron 3
    Determines if an article is important or not.
    
I plan to scrape 2 million news and blog articles and train it on output, currently both will output the same values as the V2 models
but in the future I would like to try and create models that can output multiple tags for a headline and give a value of importance 
instead of a boolean.

The models will be published here or on my hugging face account depending on size.
Due to legal limitations the datasets these models are trained on will not be published.

#### Why headlines?
The only thing guaranteed in an RSS feed is the headline and the link, we don't have full article content and may not have any at all in some cases.
This isn't a problem for the original FHN as the LLM had all the article text to decide if the article was important or not and to tag it, we can't 
get an LLM to summarise, tag and decide the importance of hundreds of articles in seconds on a PC, nevermind a phone. So compromising by instead
summarising when asked explictly by the user and using TFLite ML Models to quickly decide the Story importance and tags will make FHN-RT run on a phone
without any server.


### Repo Structure
This repo will contain multiple subprojects

/Scraperhose/
Dataset generation

/Generators/
Model generation

/FHN-App/
User app, android only for now
