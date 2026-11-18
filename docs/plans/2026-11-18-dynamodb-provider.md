# Plan — Amazon DynamoDB as a fourth engine (read-focused v1)

Spec: `docs/specs/2026-11-18-dynamodb-provider.md` (+ `.json`, revision 2, approved 2026-11-18).
Branch: `feat/add-dynamodb-support` (from `main` @ `c4a55d6`). Todo: `tasks/todo.md`.

## Package legitimacy (checked 2026-11-18 on nuget.org)

| Package | Version | Author / licence | Note |
|---|---|---|---|
| `AWSSDK.DynamoDBv2` | 4.1.0.2 | Amazon Web Services / Apache-2.0 | SDK V4 line |
| `AWSSDK.SSO` | 4.1.0.2 | same | needed at run time for SSO profiles |
| `AWSSDK.SSOOIDC` | 4.1.0.2 | same | needed at run time for SSO profiles |
| `AWSSDK.SecurityToken` | 4.1.0.2 | same | assumed-role profiles |
| `AWSSDK.Core` | 4.1.1.1 | same | transitive; pinned explicitly (`CentralPackageTransitivePinningEnabled`) |

The spec said "latest 4.0.x"; the V4 line has moved to 4.1.x — same major, same API. 4.1.0.2 is used.

## Commit plan

Each commit builds and keeps the full suite green. Messages follow §8.2 (short, lowercase, imperative).

| # | Batch | Commit message | Contents | SC |
|---|---|---|---|---|
| 1 | 1 | `add provider capability flags and a continuation seam to core` | `CredentialKind.AwsProfile`, `ConnectionInfo.ConfirmFullScans`, `QueryResult.Continuation`, four `IDbProvider` flags, `IQueryExecutor.ExecuteContinuationAsync` default, `ISqlDialect.ScanTargets` default (+ `ScanTarget` record); every implementer answers the flags (3 providers, Demo, test fakes) | SC17 |
| 2 | 2 | `add a partiql dialect with full-scan target reduction` | `PartiQlDialect`, `PartiQlScan`, `PartiQlDialectTests`, `PartiQlScanTests` | SC13, SC15 |
| 3 | 3a | `add the aws sdk and a dynamodb client seam` | packages, `IDynamoDbApi` (+ SDK impl), `DynamoDbConnectionFactory`, `DynamoDbErrors`, `DynamoDbConnectionFactoryTests`, `DynamoDbErrorTextTests`, `ProviderClassificationTests` | SC5, SC6 |
| 4 | 3b | `map dynamodb items to result rows` | `DynamoDbValues`, `DynamoDbContinuation`, `PartiQlBatch`, `PartiQlBatchTests` | SC15 (split) |
| 5 | 3c | `add the dynamodb executor and metadata reader and register the provider` | `DynamoDbQueryExecutor`, `DynamoDbMetadataReader`, `DynamoDbProvider`, `ProviderRegistry`, `DynamoDbQueryExecutorTests`, `ProviderRegistryTests` | SC1, SC11 |
| 6 | 4 | `add dynamodb local live tests` | `build/test-db-dynamodb.sh`, `tests/Shared/DynamoDbTestServer.cs`, `DynamoDbLiveTests`, CI step | SC7–SC10 |
| 7 | 5 | `add dynamodb traits, aws profile sign-in and the scan guard` | `ProviderTraits` arm + `GridEditUnavailable` / `OffersTableDdl`, `CredentialResolver` arm, `ScanGuard`, tests | SC1, SC4, SC13 |
| 8 | 6 | `add dynamodb fields to the connection editor` | `CredentialKindOptions`, port rule, dialog gating, scan checkbox, tests | SC2, SC3 |
| 9 | 7a | `page dynamodb results by continuation` | `ContinuationPaging`, `ResultSetViewModel.Continuation`, delegating branches in `ExecutionViewModel`, `ContinuationPagingTests` | SC12 |
| 10 | 7b | `confirm full table scans before they run` | `ScanConfirmation`, `ConfirmScanDialog`, `IDialogService.ConfirmScanAsync`, run / fetch-all / export hooks, `ScanConfirmationTests` | SC14 |
| 11 | 7c | `withhold editing, manual commit and table ddl on dynamodb` | `ResultSetBuilder` lock reason, `WorkspaceContext`, `TabTransactions`, `SchemaNodes`, tests | SC16 |
| 12 | 8 | `run only keyed dynamodb reads from the bearing command` | `BearingHost` refusal + export continuation, `DynamoDbExposureTests`, `ConnectionListingTests` | SC19 |
| 13 | 9 | `document dynamodb in the readme, cli guide and rules` | README, CLAUDE.md, AGENTS.md, docs/CLI.md, `.claude/rules/*` | SC18 |

Batch dependencies: 2, 3 and 5 depend only on 1; 4 on 3; 6 on 5; 7 and 8 on 3 and 5.
Execution order is sequential (1 → 13) so each commit lands on a green tree.

## Per-batch acceptance

- **Every batch:** `dotnet build` exit 0, `dotnet test` exit 0 (live suites may skip only where no container
  was asked for), `git diff --stat` touches only the batch's manifest files, then the commit.
- **Batch 1:** no new behaviour reachable; `Bearing.Core.csproj` gains no reference.
- **Batch 4 (commit 6):** `./build/test-db-dynamodb.sh` started and `DynamoDbLiveTests` pass with 0 skipped
  before the commit (§4.2). If the container cannot run here, the commit is made and the gap is reported, not
  hidden.
- **Batches 6–7:** headless assertions only; dialog / tree / scan dialog need eyeball QA (§0.2, §4.3).
- **Batch 9:** SC17 grep and the full suite on the final tree.

## Risks carried from the spec

See the spec's *Risks and assumptions*. Implementation-time watch items:
`ExecutionViewModel` growth (§9.1, delegate only), decorators not forwarding `ExecuteContinuationAsync`,
`AWSSDK.Core` transitive pin, and the PostgreSQL lexer corpus for PartiQL.
