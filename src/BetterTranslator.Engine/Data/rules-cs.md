# Czech translation rules

Shipped example. Copy to `<project root>/rag/rules/cs.md` and edit; the parent copy
wins. These are injected into the system prompt on every request, so keep them
short and imperative — this is instruction, not documentation.

## Register

- Use formal address (vykání) throughout. Never tykání, even in short UI labels.
- Prefer the impersonal form for instructions: "Zadejte heslo", not "Ty zadej heslo".

## Grammar

- Decline correctly. A noun following a preposition or a number takes the case that
  preposition or number requires; do not leave it in the nominative because English
  has no case.
- Agree in gender and number with the subject, including past participles.
- Czech has no articles. Never render "the" or "a" as a word.
- Do not copy English word order. Czech is far freer; put the new or emphasised
  information later in the clause.
- Use perfective or imperfective aspect according to meaning: a completed action
  takes the perfective ("uložit"), a repeated or ongoing one the imperfective
  ("ukládat").

## Typography

- Use Czech quotation marks: low-opening and high-closing, „like this".
- Use a hyphen where the English used a hyphen. Do not introduce em dashes.
- Keep a non-breaking space after single-letter prepositions (v, k, s, z, o, u)
  only where the source already had one.
- Decimal comma, not decimal point, in prose. Leave numbers inside code untouched.

## Never translate

- Product and brand names: BetterGuard, SZEINER, SZEINER s.r.o.
- Technology names: .NET, C#, IL, CLR, Argon2, DLL, EXE.
- Anything inside backticks, a code block, a URL, or a filesystem path.
- Interpolation placeholders of every kind. Copy each one byte for byte, including
  its case and its surrounding braces, colons or percent signs.
- Bracketed numeric tokens. These are protected blocks that were removed before
  translation and are restored afterwards; reproduce each exactly once, in the
  same order, and never renumber one.

<!--
  Deliberately no literal placeholder examples above.

  An earlier version listed them, and the model copied the examples themselves
  into its answers: a chunk containing one protected token came back carrying the
  sample placeholders from these rules instead. Rules are part of the prompt, so
  anything written here that looks like output is liable to be reproduced as
  output. Describe the shapes; never demonstrate them.
-->

- Never invent a placeholder that was not in the source text.

## Output

- Reply with the translation only. No preamble, no explanation, no "Here is the
  translation:", no surrounding quotes, no code fence unless the source had one.
- Never return the English source unchanged as though it were a translation. If a
  term genuinely stays in English, keep only that term in English, not the sentence.
- Preserve every line break and blank line exactly as in the source.
