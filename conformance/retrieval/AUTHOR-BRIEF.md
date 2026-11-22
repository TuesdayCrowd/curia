# Brief: write test questions for a technical question-and-answer board

You are writing test material. Work from this brief alone. Do not read, list or search any file or directory other than the output file you are asked to write, and do not look for other instructions: nothing else is needed and anything else you found would spoil the exercise. You may run a script to check that your output is valid JSON lines; use it for nothing else.

## The board

Software engineers and automated agents post technical questions on a board and other people answer them. Topics: programming languages, databases, networking, cryptography, deployment, build tools, security, data formats, operating systems, testing, observability, package management, protocols. A post has a `title` (one line, at most 100 characters) and a `body` (one to four sentences, at most 600 characters, plain text, no markdown). Write them the way real people write them: some terse, some chatty, some quoting an error message, a version number or a command. Vary length and tone.

## What to write

Five kinds of material, with these counts exactly.

1. **36 paraphrase pairs.** Two questions that ask for the same thing. A person who answered one would have answered the other, and would call them one question asked twice. Write the two sides as if by two different people who never saw each other's post: use different words for the same ideas, a different angle or framing, different level of detail. Share as few distinctive words as you can manage (synonyms and rewording are the point). The meaning must stay the same; do not make the second one a vaguer or a narrower question.

2. **36 near-miss pairs.** Two questions that look alike, same topic, same tool or same error, but ask different things, so that the answer to one would not answer the other. Make them hard: let them share many words and the same subject. Do not make them trivial opposites; make them the kind of pair someone skimming a list of titles might wrongly take for a repeat.

3. **18 near-copy pairs.** The same question posted twice with light edits only: a fixed typo, a reordered clause, a sentence added or dropped, different capitalisation or punctuation, a different greeting. The question is plainly the same and most of the words are the same.

4. **12 off-topic questions.** Questions in the board's post format on subjects a technical board would not hold at all (cooking, sport, travel, gardening, personal finance, pets, music, history, and so on). One text each.

5. **12 made-up queries.** Short strings of two to four invented words that mean nothing in any language (not real words, not code identifiers, not names). One string each.

## Rules for all of it

- Spread the pairs over at least fifteen different topics. No more than three pairs of one kind on one topic.
- Do not use the same text in two places. Do not write a pair whose two sides are byte-identical.
- In every pair, decide at random which text is `a` and which is `b`; do not always put the "original" first.
- Write each pair so that someone who sees only the two texts could tell what kind it is; do not rely on the `reason` field for that.

## Output format

One JSON object per line, UTF-8, no blank lines, no comments, no surrounding array, in any order. Write the file in one go or in parts, as you prefer.

Pairs (kinds 1 to 3):

```
{"id": "bp-01", "class": "paraphrase", "a": {"title": "...", "body": "..."}, "b": {"title": "...", "body": "..."}, "reason": "one line: why these are the same question"}
```

Ids are `bp-01` to `bp-36` for paraphrase pairs, `bn-01` to `bn-36` for near-miss pairs (`"class": "near-miss"`, and the reason says what differs), `bc-01` to `bc-18` for near-copy pairs (`"class": "near-copy"`).

Off-topic questions and made-up queries (kinds 4 and 5):

```
{"id": "qo-01", "class": "off-topic", "title": "...", "body": "..."}
{"id": "qm-01", "class": "made-up", "text": "..."}
```

Ids are `qo-01` to `qo-12` and `qm-01` to `qm-12`.

Write the output to the path the person who sent you this brief gave you, and nowhere else. When the file is written, reply with one line: the number of lines in it. Do not explain your choices and do not comment on the exercise.
