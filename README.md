# BrewForge — Backend

Recipe standardization and barista training for a specialty tea and coffee
chain. Capstone FA26SE311, Group Uni17T.

.NET 10 · ASP.NET Core Web API · EF Core 10 · PostgreSQL 16 · xUnit · Clean
Architecture (Domain / Application / Infrastructure / Api).

The project is built one vertical slice at a time, in the order of the team's
developer pack. Each slice is finished only when every business rule it names
is enforced in the domain layer and has a test proving the violation is
rejected.

## Status

| # | Slice | State |
|---|---|---|
| 1 | Master data and authorization | done |
| 2 | Structured recipe model, the three validators, the dependency graph | done |
| 3 | Review, approval and immutable release | done |
| 4 | Course generation and authored modules | done |
| 5 | Classes, sessions, attendance and eligibility | not started |
| 6 | Assessment, retake limit and certification | not started |
| 7 | Sales capture, POS import and aggregation | not started |
| 8 | Pilot programs, the launch gate, the rollout decision | not started |
| 9 | Impact analysis, change propagation, audit trail | not started |

This repository is the backend only. The slice prompts also describe a React
frontend; it is not part of this repository.

## Run it

You need the .NET 10 SDK and PostgreSQL 16.

```bash
docker compose up -d
```

```bash
dotnet run --project src/BrewForge.Api
```

The API listens on `http://localhost:5113`. On its first start in the
Development environment it applies `db/migrations/001_initial_schema.sql` and
seeds the reference data. The OpenAPI document is at `/openapi/v1.json`.

Without Docker, on Windows, `scripts/start-db.ps1` starts a portable
PostgreSQL 16 from `%LOCALAPPDATA%\BrewForge` with the same credentials, and
`scripts/dev-env.ps1` puts a per-user `dotnet` on the PATH of the current
shell. `scripts/reset-db.ps1` empties the local database.

### Seeded users

One user per role, plus a few more so that branch scoping and separation of
duty can be demonstrated. They all share the password in
`Seed:DefaultPassword` of `src/BrewForge.Api/appsettings.Development.json`.

| Username | Role | Branch |
|---|---|---|
| `admin` | ADMIN | — |
| `rdspec` | RD_SPECIALIST | — |
| `rdmanager`, `rdmanager2` | RD_MANAGER | — |
| `trainer` | TRAINER | — |
| `auditor` | QUALITY_AUDITOR | — |
| `trainingmgr` | TRAINING_MANAGER | — |
| `branchmgr` / `branchmgr2` | BRANCH_MANAGER | B01 / B02 |
| `trainee`, `trainee2` / `trainee3`, `trainee4` | TRAINEE | B01 / B02 |

Also seeded: three branches, sixteen ingredients, seven equipment classes and
eleven recipes — nine released (`R01`–`R08`, `R10`), one validated and waiting
for review (`R09`), and one draft that deliberately fails all three validator
checks (`R99`) so the validation and repair screens have something to show.

### AI drafting

`POST /recipe-versions/{id}/generate-draft` and the AI repair call the OpenAI
Structured Outputs endpoint with `docs/reference/recipe-draft.schema.json` as
the `json_schema` and `strict: true`. Configure `Llm:ApiKey` and `Llm:Model`
(environment variables `Llm__ApiKey`, `Llm__Model`). Without them the two
endpoints answer `422 LLM_NOT_CONFIGURED` and everything else works.

## Test it

```bash
dotnet test
```

- `tests/BrewForge.Domain.Tests` — pure unit tests of the domain. No database.
- `tests/BrewForge.Api.Tests` — the real API hosted in-process against a real
  PostgreSQL 16. Each run creates its own database, applies the same migration
  as production, and drops the database afterwards, so the triggers and partial
  unique indexes that enforce business rules are exercised for real. The server
  is `localhost:5432` with the docker-compose credentials, or whatever
  `BREWFORGE_TEST_DB` points at. The language model is replaced by a scripted
  fake; no test reaches a real one.

## Layout

```
db/migrations/        The schema. Authoritative; EF Core never creates or alters tables.
docs/reference/       The fixed JSON schema of the LLM call. The rest of the developer pack is not published here.
src/BrewForge.Domain          Entities, state machines, validators. No dependencies.
src/BrewForge.Application     Use cases, DTOs, ports (persistence, tokens, language model).
src/BrewForge.Infrastructure  EF Core mapping, Argon2id, JWT, OpenAI adapter, seed.
src/BrewForge.Api             Controllers, authorization policies, the error envelope.
```

## Where each business rule is enforced

| Rule | Enforced in | Proved by |
|---|---|---|
| BR-01 released version is immutable | `RecipeVersion.EnsureMutable`; `BrewForgeDbContext.GuardReleasedVersions`; DB triggers | `RecipeReleaseTests.BR_01_*`, `ReleaseTests.BR_01_*` |
| BR-02 one released version per recipe | `RecipeRelease` (supersede, then seal); index `ux_recipe_one_released` | `RecipeReleaseTests.BR_02_*`, `ReleaseTests.BR_02_*` |
| BR-03 version numbers never reused | `RecipeRelease.AllocateVersionNo`; `UNIQUE (recipe_id, version_no)` | `RecipeReleaseTests.BR_03_*`, `ReleaseTests.BR_03_*` |
| BR-04 rollback creates a new version | `RecipeReleaseService.RollbackAsync` | `ReleaseTests.BR_04_*` |
| BR-05 an AI draft is only a draft | `RecipeVersion.CreateDraft`, state model | `RecipeVersionTests`, `AiDraftingTests`, `ReleaseTests.Draft_cannot_be_released_*` |
| BR-06 every LLM call is logged | `RecipeDraftingService.AskModelAsync` | `AiDraftingTests.Every_call_is_logged_*`, `Malformed_answer_twice_*` |
| BR-07 non-conforming answer retried once, then rejected | `RecipeDraftParser`, `RecipeDraftingService` | `RecipeDraftParserTests`, `AiDraftingTests` |
| BR-08 a partial pass is a failure | `ValidationReport.Passed`, `RecipeVersion.Submit` | `RecipeValidatorTests` |
| BR-09 dependency graph is acyclic | `StepDependencyGraph`, `OrderingCheck` | `StepDependencyGraphTests`, `OrderingCheckTests` |
| BR-10 dose within the equipment range | `EquipmentCheck`, `UnitConverter` | `EquipmentCheckTests`, `UnitConverterTests` |
| BR-11 ingredient within its shelf life | `IngredientCheck` | `IngredientCheckTests` |
| BR-12 approver is not the author | `RecipeVersion.EnsureReleasable`; DB check | `RecipeReleaseTests.BR_12_*`, `ReleaseTests.BR_12_*` |
| BR-16 master data is never deleted | no DELETE route; `INeverDeleted` guard in the DbContext | `MasterDataIsNeverDeletedTests`, `MasterDataTests` (domain) |
| BR-18 a course is bound to one version | `Course.RebuildOn` is the only way a binding moves; index `ux_course_one_per_version` | `CourseTests.BR_18_*`, `CourseApiTests.BR_18_*` |
| BR-19, BR-31 no submission with an empty module | `Course.Submit` | `CourseTests.BR_31_*`, `BR_19_*`, `CourseApiTests.BR_31_*` |
| BR-20 only a released version is a source | `Course.EnsureUsableAsSource` | `CourseTests.BR_20_*`, `CourseApiTests.BR_20_*` |
| BR-22 reference values are read at render time | `CourseRenderer`; generated lessons store no content | `CourseTests.BR_22_*`, `CourseApiTests.BR_22_*` |
| BR-29 exactly seven modules in fixed order | `CourseModuleGenerator.CreateModules`; DB unique and check constraints | `CourseTests.BR_29_*`, `CourseApiTests.Course_is_created_*` |
| BR-30 generated modules are not edited, authored modules are not regenerated | `CourseModule.EnsureAuthorable`, `CourseModuleGenerator.Generate` | `CourseTests.BR_30_*`, `CourseApiTests.BR_30_*` |
| BR-35 every quiz question is tagged with a module | `Quiz.AddQuestion` | `CourseTests.BR_35_*`, `CourseApiTests.BR_35_*` |
| Authorization matrix | policies per role; branch query filters | `AuthorizationMatrixTests`, `BranchScopeTests` |
| Append-only audit trail | `AuditLog` has no mutators; DbContext guard; DB trigger | `AuditTrailTests` |

`AuthorizationMatrixTests` fails when an endpoint exists that is not in
`AuthorizationMatrix.cs`, so an endpoint cannot be added without deciding who
may reach it.

## Decisions the developer pack left open

These are the places where the pack was silent, or where the schema has no
column for what a slice asks. The schema was not changed for any of them.

**Refresh tokens.** The schema has no table for them. A refresh token is a
signed JWT with its own audience and a random id; revoking one writes an
`audit_log` entry (`entity_type = 'RefreshToken'`), and a token is revoked
exactly when such an entry exists. Every refresh rotates the token.

**AI repair count.** The slice asks to "count them on the draft", and
`recipe_version` has no counter. The count is the number of `AI_REPAIR` audit
entries of the version, which cannot be reset because the log is append-only.

**The equipment check and units.** A step is range-checked on the quantities
whose unit has the dimension of the machine's dosing unit. Water in millilitres
on a brewer dosed in grams of leaf is not a dose of that machine. A class dosed
in `sec` checks the step's duration. A class dosed in `degC` or `bar` has no
numeric field to compare against in the schema, so only its catalogue
membership is checked. There is no density table: grams never become
millilitres.

**Shelf life.** The window of an ingredient runs from the start of the first
step that uses it to the end of the last step, summed in step order.

**Version numbers.** A draft takes the next free number when it is created. If
drafts are released out of order, the one released later takes the next free
number at release, so released numbers only go up.

**Review and the state model.** The slice says an edit in review puts the
version back to DRAFT; the data dictionary has no VALIDATED → DRAFT transition.
An edit goes VALIDATED → REJECTED → DRAFT, both of which exist. The reviewer
becomes the author on record, so BR-12 keeps them from releasing their own edit.

**A release that fails re-validation** answers `409 MSG-E08` and moves the
version to REJECTED with the failed run stored, so the specialist can repair it.

**`GET /branches`** is open to every authenticated role, not only ADMIN as the
contract table says: scheduling a class, setting up a pilot and entering sales
all need to name a branch. A BRANCH_MANAGER or TRAINEE sees only their own
branch, through the persistence query filter.

**Model answers that invent an ingredient code** are treated as non-conforming
(BR-07), because such a draft cannot be stored at all.

**The practical checklist.** The schema has no table for it. Its items are the
gate lessons of the TECHNIQUE module, each linked to a step of the bound
version; by default one per step that has a technique gate.
`PUT /courses/{id}/practical-checklist` chooses which steps are on it.

**Durations of generated modules** are estimated from the recipe (step
durations, number of ingredients and machines), because a generated module
cannot be edited (BR-30) and every module needs a duration (BR-31).

**A course needs at least one quiz question to be submitted.** Without one no
trainee could ever be certified on it (BR-21), so submission is refused.

**Rebuilding an out-of-date course** on the new version exists as
`CourseService.RebuildAsync` and is covered by tests, but has no endpoint yet:
a course only becomes OUT_OF_DATE through change propagation (slice 9).

## Reference documents

Report 3 (the SRS) in the team's shared folder defines BR-01 to BR-22 and six
roles. The developer pack is newer and refers to BR-01 to BR-36, eight roles
and thirty-three screens; where the two differ, this code follows the pack.
