using Xunit;

// Every test in this assembly talks to the same PostgreSQL, and several of them create and drop schemas in
// it. Run in parallel, the writers destabilise the readers: the size read (#76) and the object-kind reads
// (#119) each wrap their catalog queries in a best-effort guard, so a query that fails because another test
// is holding an AccessExclusiveLock or has just dropped an object comes back as an *empty list* rather than
// as an error — and the reader's assertion then fails with "Sequence contains no matching element" about an
// extension or a type that is plainly there. Two of those failed on CI while passing here, which is the
// signature of a race rather than a bug.
//
// A shared mutable external resource is exactly what xUnit's collections are for, and serialising this
// assembly is the whole fix: 143 tests in about a minute.
//
// This is **not** the thing §4.5 rejected. There, disabling parallelism in the App suite would have hidden
// two real thread-affinity bugs — the tests were genuinely wrong and parallelism was the messenger. Here the
// tests are correct and the resource is singular: no amount of fixing them makes two `DROP SCHEMA CASCADE`s
// and a catalog scan safe to interleave. The distinction is whether parallelism is revealing a defect or
// manufacturing one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
