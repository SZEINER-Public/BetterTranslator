# Czech glossary

Copy to `rag/glossary/cs.md` in the host root to override. The sub-repository
never reads a host path; this file is the shipped default and the host copy wins.

Two tables, both optional. Parsing is positional: first column, second column.
Rows whose first cell is empty, or that are the header/separator, are ignored.

## Required terms

One English term, one Czech term. When the English appears in a line being
translated, the Czech must appear in the answer or the line is rejected and
retried.

Measured need: `provision` and its forms came back eight different ways in a
single document - left as English `provisions`, then `doplnit`, `Nastav`,
`Tyto polozky` (read as a noun, wrong), `opatreni` (the legal sense, wrong),
`zprovozneni` and `pripravenem`. Nothing was misspelled; the term simply had no
agreed translation. That is what a glossary is for, and no amount of prompt
wording replaces it.

Three columns, and the difference between the last two matters.

Column 2 is the **stem used to check** the answer: matched case-insensitively
anywhere in the translation, so it must be the part every inflected form shares.
`zprovozn` matches `zprovozní`, `zprovoznění` and `zprovoznit` alike.

Column 3 is the **word shown to the model**. A stem is not a word: prompted with
"provisions -> zprovozn", the model answered "`run.bat` zprovoznění a spouští" -
a noun where the sentence needs a verb, because it pasted the stem and guessed an
ending. Give it a real word in the right part of speech and it inflects correctly
on its own. If column 3 is empty the stem is used, which is fine for terms that
do not inflect.

| English | Czech stem (checked) | Czech word (shown to the model) |
|---|---|---|
| provisioning | zprovozn | zprovoznění |
| provisions | zprovozn | zprovozní |
| provisioned | zprovozn | zprovozněný |
| provision | zprovozn | zprovoznit |
| bootstrap | zavad | zavaděč |
| headless | bezhlav | bezhlavý |
| store | úložišt | úložiště |
| endpoint | koncov | koncový bod |
| quantisation | kvantiz | kvantizace |
| quantization | kvantiz | kvantizace |
| weights | váh | váhy |
| clone | klon | klon |

## Words that must not be added

The left column may appear in the answer ONLY if the right column appears in the
English source. This exists because the model narrates a filename instead of
leaving it alone: `# run.bat - repo-local LM Studio bootstrap` came back as
`# Spusťte soubor run.bat`, "Run the file run.bat" - a verb and a noun invented
out of nothing.

Filenames are also protected as spans now, which removes most of the cause. This
table catches the rest.

The right column is a **regular expression**, not a literal, so one Czech word can
be justified by any of several English ones. `aplikace` listed against `applicat`
alone rejected a correct rendering of "desktop **app**".

| Czech word | allowed only if the English contains |
|---|---|
| soubor | file |
| skript | script\|\.ps1\|\.bat |
| složka | folder\|director |
| adresář | director\|folder\|path |
| aplikace | app |
| uživatel | user\|account |
| systém | system\|OS\|Windows |

## Keep in English

Your decision, one term per row. A term listed here is protected as a span and
never reaches the model, so it cannot be translated, inflected or explained.

`scripts\glossary-build.ps1` proposes the candidates; it does not decide. On this
repository's README it measured `daemon` kept in 1 line and translated in 37 -
the split is the finding, and which way to settle it is yours.

| Term |
|---|
| daemon |