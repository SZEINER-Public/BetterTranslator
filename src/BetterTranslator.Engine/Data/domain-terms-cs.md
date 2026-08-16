# Software-domain terms, English to Czech

The project glossary in `glossary-cs.md` states what ONE body of documents has
agreed to call things, and it is switched on by the composer's Memory chip. This
file is the other half: what the words of software engineering mean in Czech at
all, independent of any project, applied to every send.

The defect it exists for is measured. An English engineering brief sent to Czech
came back with `Engine`, naming a source project, rendered `motor` -- the machine
in a car. Nothing was misspelled and no gate could see it: `motor` is a real
Czech word, correctly inflected, in a grammatical sentence. Word-for-word
plausibility with no domain is exactly what a general model produces, and the
failure class is homographs: `file` is also a metal tool, `driver` is also
somebody driving, `commit` is also committing a crime, `thread` is also sewing
thread, `scope` is also a telescope.

Parsing is positional, first column through fourth. Rows whose first cell is
empty, and the header and separator rows, are ignored. Comparison is
case-insensitive.

Column 1 is the English term as it appears in a source text.

Column 2 is the accepted Czech rendering. Where the Czech of the trade keeps the
English word -- and for most of these it does -- the accepted rendering IS the
English word, which is a statement about Czech usage and not a refusal to
translate. A row whose second cell is `-` marks the term do-not-translate: it
names a component, a command or a format and must survive byte-identical.

Column 3 is the renderings that are wrong here, separated by semicolons. This is
the column that does the work: a translation carrying one of these while its
source carried the term is corrected in code, because a wrong rendering can be
named but a right one cannot be guessed. Leave it empty when the term has no
common wrong form.

Column 4 is why, in one clause, shown to the reader in the correction popover.
It is the only part of this file a person reads at runtime, so write it for them.

## Terms

| English | Czech | Wrong renderings | Why |
|---|---|---|---|
| engine | engine | motor; stroj; hnací jednotka | motor is the machine in a car; Czech software keeps engine |
| runtime | runtime | doba běhu; běhový čas | the hosting environment, not an elapsed duration |
| build | sestavení | stavba; budova; postavit | the compiled output, not construction |
| stack | technologie | hromada; komín; halda | the set of technologies; a data-structure stack is zásobník |
| project | projekt | promítat; průmět | the unit of work, not a projection |
| service | služba | servis | servis is repair work; a running service is služba |
| host | hostitel | hostitel večírku; hostina | the machine running a process |
| endpoint | koncový bod | koncovka; konec | the addressable API surface |
| batch | dávka | várka; šarže | a group processed together |
| file | soubor | pilník; brousit | pilník is the metal tool; a file on disk is soubor |
| index | index | ukazováček; rejstřík prstu | the lookup structure, not the finger |
| scope | rozsah | dalekohled; puška; mířidlo | the boundary of what applies, not an optical sight |
| model | model | maketa; modelka | the trained network |
| token | token | žeton; známka; poukaz | the unit of text a model reads |
| thread | vlákno | nit; závit; příze | a thread of execution; závit is a screw thread |
| commit | commit | spáchat; dopustit se; provinit | spáchat is committing a crime |
| branch | větev | pobočka; odvětví; filiálka | a line of development; pobočka is a bank branch |
| driver | ovladač | řidič; šofér | řidič is a person driving a car |
| pipeline | pipeline | ropovod; potrubí; plynovod | the sequence of processing stages |
| wrapper | wrapper | balicí papír; obal na dárek | the thin layer over another API |
| library | knihovna | půjčovna knih | the linked code, by the ordinary Czech word |
| repository | repozitář | skladiště; sklad | the version-controlled source tree |
| string | řetězec | provázek; struna; šňůra | a sequence of characters |
| key | klíč | tónina; klávesa | a lookup key; klávesa is a keyboard key |
| value | hodnota | statečnost | the stored datum; cena is deliberately absent, its stem collides with cenit |
| array | pole | louka; niva | the indexed collection; pole is correct, a meadow is not |
| queue | fronta | řada lidí | the data structure |
| cache | cache | skrýš; úkryt | the fast store, kept as cache in Czech software |
| buffer | buffer | nárazník; tlumič | the staging memory |
| socket | socket | zásuvka; objímka | the network endpoint |
| port | port | přístav; kotviště | the numbered network port |
| bug | chyba | brouk; hmyz | a defect; brouk is the insect |
| patch | oprava | záplata na kalhoty; náplast | a code fix |
| release | vydání | propuštění; uvolnění vězně | a shipped version |
| deploy | nasadit | rozmístit vojsko | putting a build into service |
| mount | připojit | nasednout; vylézt | attaching a filesystem |
| root | root | kořen stromu | the superuser or the tree root of a repository |
| shell | shell | mušle; skořápka; lastura | the command interpreter |
| kernel | jádro | jádro ořechu; pecka | the operating-system core |
| daemon | démon | ďábel; zlý duch | the background process |
| handle | handle | klika; rukojeť | the opaque reference; klika is a door handle |
| frame | rámec | obraz v rámu | a stack frame or a rendered frame |
| stream | proud | potok; říčka | the sequence of bytes |
| lock | zámek | zámek na kopci | a synchronisation lock; a castle is also zámek |
| pool | fond | bazén; koupaliště | the reusable set; bazén is a swimming pool |
| trace | trasování | stopa zvířete | the execution record |
| hook | hook | hák; udice | the extension point |
| flag | přepínač | vlajka; prapor | a command-line switch |
| dependency | závislost | závislost na drogách | what a project requires to build |

## Component and command names

Terms in this table are do-not-translate: they name a thing in this repository
or a literal command, and a translated form is always wrong. The second column
is `-` and the fourth still carries the reason.

| English | Czech | Wrong renderings | Why |
|---|---|---|---|
| BetterTranslator | - |  | the product name |
| Core | - | jádro; podstata | the name of a source project |
| Engine | - | motor; stroj | the name of a source project, capitalised |
| Runtime | - | doba běhu; prostředí; běhové prostředí | the name of a source project, capitalised |
| Indexing | - | indexování | the name of a source project, capitalised |
| Map | - | mapa | the name of a source project, capitalised |
| App | - | aplikace | the name of a source project, capitalised |
| Markdown | - | značkovací jazyk | a format name |
| JSON | - | JSON objekt |  a format name |
| Hunspell | - |  | a library name |
| llmster | - |  | the runtime name |
| TranslateGemma | - |  | a model name |
| EuroLLM | - |  | a model name |
| LM Studio | - |  | a product name |
| stdin | - | standardní vstup | a literal stream name |
| stdout | - | standardní výstup | a literal stream name |
