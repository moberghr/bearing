# MySQL Grammar (Positive Technologies)

Vendored from [antlr/grammars-v4](https://github.com/antlr/grammars-v4) `sql/mysql/Positive-Technologies`
at commit `9fa9f94b20cd3225bd84f27554515da207ec39ff`. MIT licence (see the header of each `.g4`).

Serves both MySQL and MariaDB in Bearing: MariaDB's extensions (`RETURNING`, sequences, `/*M! */`) are a
superset the parser recovers over, and completion only needs the shared core (`FROM`, `JOIN`, expressions).

## Local changes (each marked `// Bearing:` in the source)

1. `STRING_LITERAL` no longer includes the backtick form, and `uid` admits `REVERSE_QUOTE_ID`. Upstream lexed
   `` `name` `` as a string literal, so a delimited identifier was invisible to every token-driven pass
   (the FROM scan, the qualifier lookup, the external name scan).
2. `DOT_ID` (`'.' ID_LITERAL` as one token) is disabled, so `schema.table` lexes as `ID DOT ID` — the shape the
   completion engine's FROM scan reads. Cost: an unquoted name that starts with digits after a dot
   (`t.123abc`) lexes as a real literal followed by an identifier.
