# Brief: write posts and the searches that find them, for a technical question-and-answer board

You are writing test material. Work from this brief alone. Do not read, list or search any file or directory other than the output file you are asked to write, and do not look for other instructions: nothing else is needed and anything else you found would spoil the exercise. You may run a script to check that your output is valid JSON lines; use it for nothing else.

## The board

Software engineers and automated agents post technical questions on a board and look for them with a search box. Topics: programming languages, databases, networking, cryptography, deployment, build tools, security, data formats, operating systems, testing, observability, package management, protocols. A post has a `title` (one line, at most 100 characters) and a `body` (one to four sentences, at most 600 characters, plain text, no markdown). Write them the way real people write them: some terse, some chatty, some quoting an error message, a version number or a command.

## What to write

1. **60 posts, each with two searches that should find it.** For each post write `terse`: what someone types in a search box when they remember the subject (two to five words), and `sentence`: the same person asking in a full sentence, the way they would ask a colleague (six to fourteen words). Write each search as the person would write it who is looking for the post and has not seen it; do not copy phrases from the post.
2. **20 posts whose subject is a word of a language or a command that is also an everyday word**, each with two searches as above. The post is about that word itself: what a keyword, an operator, a built-in or a command named by an ordinary English word does, or how to use it.
3. **30 off-topic searches.** Each is what someone types who wants something a technical board would not hold at all (cooking, sport, travel, gardening, personal finance, pets, music, history, health, and so on). Four to twelve words, written as a person would write them, some as questions and some as phrases. No post goes with them.
4. **20 vague searches.** Each is what someone types who has not yet decided what to ask: two to six words, no technical term in them. No post goes with them.

## Rules for all of it

- Spread the 80 posts over at least twenty different topics. No more than four posts on one topic.
- Do not use the same text in two places. Do not write a search that is the same as a post's title.
- Do not explain, label, or comment inside the texts.

## Output format

One JSON object per line, UTF-8, no blank lines, no comments, no surrounding array, in any order.

```
{"id": "kt-01", "class": "target", "post": {"title": "...", "body": "..."}, "terse": "...", "sentence": "..."}
{"id": "kh-01", "class": "hazard", "post": {"title": "...", "body": "..."}, "terse": "...", "sentence": "..."}
{"id": "ko-01", "class": "off-topic", "query": "..."}
{"id": "kv-01", "class": "vague", "query": "..."}
```

Ids are `kt-01` to `kt-60`, `kh-01` to `kh-20`, `ko-01` to `ko-30`, `kv-01` to `kv-20`. Write the output to the path the person who sent you this brief gave you, and nowhere else. When the file is written, reply with one line: the number of lines in it. Do not explain your choices and do not comment on the exercise.
