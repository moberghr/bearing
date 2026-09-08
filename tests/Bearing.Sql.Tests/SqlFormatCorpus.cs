using System.Collections.Generic;

namespace Bearing.Sql.Tests;

/// <summary>
/// The SQL every safety property in <see cref="SqlFormatSafetyTests"/> is run against. Kept apart from the
/// tests so the corpus can grow without touching them: one entry added here becomes a case in every property
/// at once, which is the cheapest way this suite gets stronger.
/// <para>
/// Stocked from three places. Postgres shapes #102 calls out; the categories the
/// <c>sql-formatter</c> family's own suite covers (its dialect tests were read for exactly this — operator
/// tables, quoting styles, placeholder syntaxes, unicode identifiers, brutal whitespace); and the things
/// this codebase already knows are load-bearing — <c>WriteGuard</c>'s risky verbs, <c>SqlRedactor</c>'s
/// literal shapes, temporal types (§5.5).
/// </para>
/// <para>
/// Every entry must <b>parse</b>. One that does not is silently refused by the formatter, so it would sit
/// here proving nothing while looking like coverage — <see cref="SqlFormatSafetyTests"/> asserts the absence
/// of a refusal for exactly that reason. SQL that should be refused belongs in the refusal tests instead.
/// </para>
/// </summary>
internal static class SqlFormatCorpus
{
    public static IEnumerable<string> All()
    {
        // ---- the everyday shapes -------------------------------------------------------------
        yield return "select id, name from users where id = 1";
        yield return "select * from t";
        yield return "select 1";
        yield return "select distinct a, b from t";
        yield return "select distinct on (a) a, b from t order by a, b";
        yield return "select all a from t";
        yield return "select a as x, b b2, c from t";
        yield return "select t.*, u.* from t, u";
        yield return "select count(*) from t";
        yield return "select public.t.a from public.t";
        yield return "select a from public.t as x";

        // ---- joins ----------------------------------------------------------------------------
        yield return "select * from a join b on b.id = a.id";
        yield return "select * from a left join b on b.id = a.id";
        yield return "select * from a left outer join b on b.id = a.id";
        yield return "select * from a right join b using (id)";
        yield return "select * from a full outer join b on true";
        yield return "select * from a cross join b";
        yield return "select * from a natural join b";
        yield return "select * from a natural left join b";
        yield return "select * from a join b on a.id = b.id join c on c.id = b.id join d on d.id = c.id";
        yield return "select * from (a join b on a.id = b.id) join c on c.id = a.id";
        yield return "select * from a, b, c where a.id = b.id and b.id = c.id";
        yield return "select * from orders o, lateral (select 1 from items i where i.o = o.id) s";
        yield return "select * from generate_series(1, 10) as g(n)";
        yield return "select * from t tablesample bernoulli (10)";

        // ---- predicates -----------------------------------------------------------------------
        yield return "select a from t where b between 1 and 2";
        yield return "select a from t where b not between 1 and 2";
        yield return "select a from t where b between symmetric 1 and 2 and c = 3";
        yield return "select a from t where b between 1 and 2 and c between 3 and 4";
        yield return "select a from t where a = 1 and b = 2 and c = 3 and d = 4";
        yield return "select a from t where a = 1 or b = 2 or c = 3";
        yield return "select a from t where (a = 1 and b = 2) or (c = 3 and d = 4)";
        yield return "select a from t where a in (1, 2, 3)";
        yield return "select a from t where a in (select b from u)";
        yield return "select a from t where exists (select 1 from u where u.id = t.id)";
        yield return "select a from t where a is null and b is not null";
        yield return "select a from t where a is distinct from b";
        yield return "select a from t where a is not distinct from b";
        yield return "select a from t where a like 'x%' and b ilike 'y%'";
        yield return "select a from t where a similar to 'x'";
        yield return "select a from t where not (a and b)";
        yield return "select a from t where a = any (array[1, 2])";
        yield return "select a from t where a = all (select b from u)";

        // ---- expressions -----------------------------------------------------------------------
        yield return "select case when a = 1 then 'x' when a = 2 then 'y' else 'z' end from t";
        yield return "select case a when 1 then 'x' else 'y' end from t";
        yield return "select case when a then case when b then 1 else 2 end else 3 end from t";
        yield return "select coalesce(a, b, c), nullif(a, b), greatest(a, b) from t";
        yield return "select (a + b * (c - now())) from t";
        yield return "select -1, +1, -a, - (a + b) from t";
        yield return "select a from t where b = (select max(c) from u)";
        yield return "select (select max(c) from u) as m from t";
        yield return "select interval '1 day', date '2020-01-01', time '10:00' from t";
        yield return "select now() at time zone 'utc' from t";
        yield return "select extract(year from d) from t";
        yield return "select substring(a from 1 for 2) from t";
        yield return "select trim(both ' ' from a) from t";
        yield return "select cast(a as int), a::int, a::text[], a::numeric(10, 2) from t";
        yield return "select array[1, 2, 3], x[1], y[1:2], array(select a from t) from t";
        yield return "select row(a, b), (row(a, b)).f1 from t";

        // ---- aggregates and windows ---------------------------------------------------------------
        yield return "select count(*) filter (where status = 'x') from t";
        yield return "select sum(a) filter (where b), avg(c) from t group by d";
        yield return "select string_agg(a, ',' order by b) from t";
        yield return "select array_agg(distinct a) from t";
        yield return "select row_number() over (partition by a order by b desc) from t";
        yield return "select sum(a) over (order by b rows between unbounded preceding and current row) from t";
        yield return "select rank() over w from t window w as (partition by a order by b)";
        yield return "select a, count(*) from t group by a having count(*) > 1";
        yield return "select a, b, count(*) from t group by rollup (a, b)";
        yield return "select a, b, count(*) from t group by grouping sets ((a), (b), ())";
        yield return "select a from t group by cube (a, b)";

        // ---- ordering, limits, locking --------------------------------------------------------------
        yield return "select a from t order by a";
        yield return "select a from t order by a desc, b asc nulls last";
        yield return "select a from t order by 1, 2";
        yield return "select a from t limit 10";
        yield return "select a from t limit all";
        yield return "select a from t limit 10 offset 5";
        yield return "select a from t offset 5 rows fetch first 10 rows only";
        yield return "select a from t for update";
        yield return "select a from t for update of t nowait";
        yield return "select a from t for share skip locked";

        // ---- set operations and CTEs -----------------------------------------------------------------
        yield return "select a from t1 union select a from t2";
        yield return "select a from t1 union all select a from t2 order by a";
        yield return "select a from t1 except select a from t2";
        yield return "select a from t1 intersect select a from t2";
        yield return "select a from t1 union select a from t2 union select a from t3";
        yield return "(select a from t1) union (select a from t2)";
        yield return "with a as (select 1 as x) select * from a";
        yield return "with a as (select 1 as x), b as (select x from a) select * from b join a on a.x = b.x";
        yield return "with recursive t(n) as (values (1) union all select n + 1 from t where n < 10) select n from t";
        yield return "with ids as (values (4), (5), (6)) select * from ids";
        yield return "with a as materialized (select 1) select * from a";
        yield return "with a as not materialized (select 1) select * from a";
        yield return "with w as (delete from t returning *) select * from w";
        yield return "select * from (select id from users where active) u where u.id > 5";
        yield return "select * from (select * from (select 1 as x) a) b";
        yield return "select * from (select * from (select * from (select 1 as x) a) b) c";

        // ---- writes -----------------------------------------------------------------------------------
        yield return "insert into t (a, b) values (1, 2)";
        yield return "insert into t (a, b) values (1, 2), (3, 4), (5, 6)";
        yield return "insert into t default values";
        yield return "insert into t (a) select x from other where x is not null";
        yield return "insert into t (a, b) values (1, 2) on conflict (a) do nothing";
        yield return "insert into t (a, b) values (1, 2) on conflict (a) do update set b = excluded.b returning *";
        yield return "insert into t as x (a) values (1) returning x.a, x.b";
        yield return "update t set a = 1";
        yield return "update t set a = 1, b = 2 where id = 3 returning id";
        yield return "update t set (a, b) = (1, 2) where id = 3";
        yield return "update t set a = u.a from u where u.id = t.id";
        yield return "delete from t";
        yield return "delete from t where id = 1 returning *";
        yield return "delete from t using u where t.id = u.id returning *";

        // ---- operators that must never be split ----------------------------------------------------
        yield return "select a #>> '{x}', b -> 'k', c ->> 'k', d #> '{y}', e #- '{z}' from t";
        yield return "select a || ' ' || b from t where c <@ d and e @> f";
        yield return "select a from t where b ?| array['x'] and c ?& array['y'] and d ? 'z'";
        yield return "select a from t where b ~ 'p' and c ~* 'p' and d !~ 'p' and e !~* 'p'";
        yield return "select a from t where b ~~ 'p' and c ~~* 'p' and d !~~ 'p' and e !~~* 'p'";
        yield return "select a <> b, c != d, e >= f, g <= h from t";
        yield return "select a << b, c >> d, e & f, g | h, i # j from t";
        yield return "select |/ 25, ||/ 27 from t";
        yield return "select a % b, c ^ d from t";
        yield return "select a from t where b @@ to_tsquery('x')";
        yield return "select a::text, b::int from t";

        // ---- literals and quoting -----------------------------------------------------------------
        yield return "select $$ a '' \" quoted $$ from t";
        yield return "select $tag$ nested $$ inside $tag$ from t";
        yield return "select $x$ line\none\nline two $x$ from t";
        yield return "select 'it''s', 'a  b   c' from t";
        yield return "select E'line\\nbreak', E'tab\\t' from t";
        yield return "select U&'\\0041', U&\"d\\0061ta\" from t";
        yield return "select B'1010', X'1f' from t";
        yield return "select '{\"k\": [1, 2]}'::jsonb -> 'k' from t";
        yield return "select \"MixedCase\", \"with space\", \"select\" from \"Quoted Table\"";
        yield return "select \"a\"\"b\" from t";
        yield return "select 1.5, 1e10, 1e-9, 1.5e-10, 3.5E12, .5, 0.5 from t";
        yield return "select -123.4, +123.4 from t";
        yield return "select 組合使用, тест, 안녕하세요 from t";
        yield return "select 'ünïcödé literal ✓' from t";

        // ---- placeholders a query editor actually sees ------------------------------------------
        yield return "select $1, $2 from tbl where a = $3";
        yield return "insert into t (a, b) values ($1, $2)";

        // ---- comments in every position ------------------------------------------------------------
        yield return "-- leading\nselect a from t";
        yield return "/* leading block */\nselect a from t";
        yield return "select a, /* between */ b from t";
        yield return "select a from t -- trailing\nwhere a = 1";
        yield return "select a from t /* multi\nline\ncomment */ where a = 1";
        yield return "select a -- one\n, b -- two\nfrom t";
        yield return "select\n-- above the target\na\nfrom t";
        yield return "select a from t where\n-- why\na = 1";
        yield return "select a from t; -- after the semicolon";
        yield return "select a from t\n-- the very last line";
        yield return "select /* a */ /* b */ c from t";
        yield return "select a from t where b = 1 -- comment with 'quote' and -- dashes";
        yield return "select a from t /* comment with a ; semicolon */";

        // ---- whitespace the formatter has to normalise ---------------------------------------------
        yield return "select     a     from     t";
        yield return "select\n\n\na\n\n\nfrom\n\n\nt";
        yield return "select * from foo LEFT \t\n JOIN bar on true ORDER \n BY blah";
        yield return "select distinct * frOM foo WHERe a > 1 and b = 3";
        yield return "SELECT A FROM T WHERE B = 1";
        yield return "   select a from t   ";
        yield return "select a from t;\n";
        yield return "select a\r\nfrom t\r\nwhere b = 1";

        // ---- batches -------------------------------------------------------------------------------
        yield return "select 1; select 2";
        yield return "select 1;\nselect 2;\nselect 3";
        yield return "select a from t; insert into u (a) values (1); delete from v";
        yield return "select 1;";
        yield return "create index i on t (a);\nselect a from t";
        yield return "select a from t;\ncreate index i on t (a)";

        // ---- shapes with no layout rules: these must come back untouched ---------------------------
        yield return "create table foo (\n    id int primary key,\n    name text\n)";
        yield return "create table foo (id int, constraint c check (id > 0))";
        yield return "create function f() returns int as $$ begin return 1; end $$ language plpgsql";
        yield return "create or replace function g(a int) returns table (x int) as $b$ select 1; $b$ language sql";
        yield return "alter table t\n    add column b int";
        yield return "drop table if exists t cascade";
        yield return "truncate table t restart identity";
        yield return "explain analyze select a from t";
        yield return "explain (analyze, buffers) select a from t";
        yield return "create view v as select a from t";
        yield return "create materialized view mv as select a from t";
        yield return "grant select on t to public";
        yield return "begin";
        yield return "commit";
        yield return "set search_path to public";
        yield return "vacuum analyze t";
        yield return "copy t (a, b) from stdin";
        yield return "comment on table t is 'a table'";
        yield return "do $$ begin perform 1; end $$";
    }
}
