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
| 5 | Classes, sessions, attendance and eligibility | done |
| 6 | Assessment, retake limit and certification | done |
| 7 | Sales capture, POS import and aggregation | done |
| 8 | Pilot programs, the launch gate, the rollout decision | done |
| 9 | Impact analysis, change propagation, audit trail | done |

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
The three drinks of origin EXISTING (`R05`, `R07`, `R08`) are on sale at every
branch since 1 January 2026, so sales can be entered and imported for them
straight away; `samples/pos-import-sample.csv` is a POS export for branch B01
with 20 good lines and 4 bad ones.

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
samples/              Demonstration files that are also test fixtures.
src/BrewForge.Domain          Entities, state machines, validators. No dependencies.
src/BrewForge.Application     Use cases, DTOs, ports (persistence, tokens, language model).
src/BrewForge.Infrastructure  EF Core mapping, Argon2id, JWT, OpenAI adapter, CSV and XLSX reader, seed.
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
| BR-32 eligible only with all modules and enough attendance | `Enrollment.EvaluateEligibility`, `EnsureEligibleForAssessment`, `AttendanceSummary` | `TrainingTests.BR_32_*`, `TrainingApiTests.BR_32_*` |
| BR-34 a trainer must be certified on the bound version | `TrainingClass.AddSession` | `TrainingTests.BR_34_*`, `TrainingApiTests.BR_34_*` |
| BR-33 the retake limit locks the enrolment | `Enrollment.AttemptQuiz`, `RetakesLeft` | `AssessmentTests.BR_33_*`, `AssessmentApiTests.BR_33_*` |
| BR-14 a trainee never evaluates themselves | `Enrollment.EvaluatePractical` (403) | `AssessmentTests.BR_14_*`, `AssessmentApiTests.BR_14_*` |
| BR-17 no automated assessment of movement | `PracticalEvaluation` holds a pass flag per checklist item and nothing else | `AssessmentTests.BR_17_*` |
| BR-21 certificate only when modules, quiz and practical all pass | `Enrollment.TryCertify`, the only producer of a `Certificate`; no POST, PUT or DELETE route | `AssessmentTests.BR_21_*`, `A_certificate_cannot_be_created_any_other_way`, `AssessmentApiTests.BR_21_*`, `A_certificate_cannot_be_created_changed_or_deleted_through_the_api` |
| BR-13 a certificate is bound to a recipe version | `Enrollment.TryCertify`, `Certificate.Certifies` | `AssessmentTests.BR_13_*`, `AssessmentApiTests.BR_13_*` |
| Certified staff are recounted when a certificate is issued | `LaunchReadinessService`, `BranchLaunchStatus.RecomputeCoverage` | `AssessmentApiTests.Issuing_a_certificate_recounts_*` |
| BR-24 a sale belongs to the version live at the branch that day | `SalesRecord.Record`, `BranchLaunchStatus.VersionSoldOn`; the request has no version field | `SalesTests.BR_24_*`, `SalesApiTests.BR_24_*`, `PosImportApiTests.BR_24_*` |
| BR-25 one sales record per drink, branch and day | `SalesBook`; `SalesRecord.ReplaceFromImport`; `UNIQUE (branch_id, recipe_id, trading_date)` | `SalesTests.BR_25_*`, `SalesApiTests.BR_25_*`, `PosImportApiTests.BR_25_*` |
| A POS line is accepted whole or rejected with one reason | `PosImportLine.Parse`, `PosImportService` | `SalesTests` (layout, quantity, date), `PosImportApiTests.Sample_file_*`, `Every_kind_of_bad_line_*` |
| BR-23 PREPARING never goes straight to LIVE | `BranchLaunchStatus.GoLive`, `OpenForSale`, `RecomputeCoverage`; `PilotProgram.GoLive` counts the certified staff at that moment | `PilotTests.BR_23_*`, `PilotApiTests.BR_23_*`, `Readiness_checker_*`, `Rollout_plans_*` |
| BR-26 criteria are read-only once the pilot runs | `PilotProgram.EnsureEditable`, `Update` | `PilotTests.BR_26_*`, `PilotApiTests.BR_26_*` |
| BR-27 a decision only on an ENDED pilot, with the figures of that moment | `PilotProgram.Decide`; `LaunchDecision` has no mutators; `UNIQUE (pilot_program_id)` | `PilotTests.BR_27_*`, `PilotApiTests.BR_27_*` |
| BR-28 one DRAFT or RUNNING pilot per version, which cannot be superseded meanwhile | `PilotProgram.Create`, `RecipeRelease.Prepare`; index `ux_pilot_active_version` | `PilotTests.BR_28_*`, `PilotApiTests.BR_28_*` |
| BR-36 an existing drink is LIVE without a pilot, coverage not met and not blocked | `BranchLaunchStatus.LiveForExistingRecipe`, `LaunchReadinessService.GoLiveWithoutPilotAsync`, `OpenExistingDrinksAtAsync` (a branch that opens or reopens later), `PilotProgram.Create` | `PilotTests.BR_36_*`, `PilotApiTests.BR_36_*` |
| A pilot ends by the calendar, whether or not anybody looks at it | `PilotEndScheduler`, `PilotService.EndDueAsync` | `PilotApiTests.Pilot_is_ended_by_the_scheduler_*`, `Pilot_ends_by_the_calendar_*` |
| A branch with missing trading days is incomplete, not scored | `PilotEvaluator` | `PilotTests.Branch_with_missing_trading_days_*`, `PilotApiTests.Branch_with_missing_trading_days_*` |
| BR-15 propagation flags and never deletes | `ImpactRun.Commit`, `Course.MarkOutOfDate`, `Certificate.FlagForRecertification`; `INeverDeleted` guard | `ImpactTests.BR_15_*`, `ImpactApiTests.BR_15_*`, `Releasing_a_new_version_*` |
| A what-if writes nothing but uncommitted `change_impact` rows | `ImpactAnalysisService.AnalyzeAsync` (no audit entry, nothing tracked) | `ImpactTests.What_if_*`, `ImpactApiTests.What_if_leaves_the_database_unchanged_*` (row count of all 34 tables) |
| The affected set is complete at commit | `ImpactRun.Resume`, `DependencyGraph` | `ImpactTests.Commit_takes_in_*`, `ImpactApiTests.Commit_takes_in_*` |
| A module markedly below the others is highlighted | `CourseEffectiveness.PerModule` | `ImpactTests.First_attempt_pass_rate_*`, `AuditApiTests.Course_effectiveness_*` |
| Enrolment state model | `Enrollment` transition table | `TrainingTests.Enrolment_state_model_*`, `Transition_that_is_not_in_the_table_*` |
| A regulation change spares enrolments in flight | `TrainingRegulation.Resolve`, `EnsureNotRetroactive` | `TrainingTests.Enrolment_keeps_the_rule_*`, `TrainingApiTests.Regulation_change_*` |
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

**Which regulation applies to an enrolment.** The schema keeps no copy of the
rules on the enrolment, so they are resolved by date: the regulation for the
course type most recently in force on the day the enrolment was created. To
keep that stable, a regulation may not take effect on or before the day of an
existing enrolment of that type (`409 REGULATION_RETROACTIVE`); a rule is
changed by adding a regulation that takes effect later.

**Attendance.** A session counts once it exists; an EXCUSED session is left out
of the count; a session not yet recorded counts as not attended. A trainee is
flagged as soon as the remaining sessions can no longer reach the minimum.

**ELIGIBLE is re-checked at the gate.** The state model has no way back from
ELIGIBLE, but attendance can be corrected afterwards, so the assessment gate
runs the eligibility check again instead of trusting the state alone.

**Who is in a class.** `POST /training-classes/{id}/open` enrols every active
trainee of the class's branch, or the users named in an optional
`{ traineeIds }`. A trainer may be named: that is how a trainer becomes
certified on a version before teaching it (BR-34), which is also why the
"TRAINEE (own)" endpoints of the contract admit a TRAINER acting on their own
enrolment.

**Enrolment close and reset** (`POST /enrollments/{id}/close`, `/reset`) are
not in the contract table. The state model gives both to the Training Manager
and names no endpoint, so these two were added.

**Notifications** are recorded as `NOTIFY` entries of the audit log against
the user they are for; the schema has no notification table and no channel is
integrated yet.

**Prerequisites** of a regulation are reported by `GET /training-needs` but
not enforced when a course is assigned.

**The quiz as the learner sees it.** `GET /enrollments/{id}/quiz` is not in the
contract table; the quiz screen needs the questions, and the trainer's
`GET /courses/{id}/quiz/questions` carries the answers. It serves questions and
options only, behind the same eligibility gate as the attempt.

**Retakes.** `max_retakes` counts attempts after the first: with two retakes
the third failure locks the enrolment. A failed attempt resets the modules in
which a question was answered wrongly, so the gate (BR-32) stays closed until
they are studied again. `quiz_attempt` has no cycle column, so a reset by the
Training Manager restarts the enrolment as of that moment (`enrolled_at`, the
due date and the regulation in force); attempts made before it stay in the
history and no longer count against the limit.

**The practical verdict that counts** is the latest one. The marks must cover
the checklist exactly: an item left out is not an item passed.

**Re-certification on the same version** renews the existing certificate
instead of adding a second row for the same user, course and version. A
certificate earned on a newer version supersedes the older one, which is kept.

**A course that is OUT_OF_DATE** refuses the quiz (`409 MSG-W05`) and the
practical (`409 MSG-W02`), both with rule BR-15: nobody is assessed against a
recipe version that has been superseded.

**`GET /dashboards/training-progress`** returns one row per branch and course:
learners by enrolment state, overdue, and staff holding a valid certificate or
one that needs re-certification.

**Trading days** are calendar days in Vietnam (UTC+7): `live_since` is an
instant, a sale has a date, and the two are compared on that calendar.

**The version a sale belongs to.** `branch_launch_status` keeps only the
present version and status. A withdrawal and a move of a branch to another
version are therefore read back from their audit entries (`WITHDRAW`,
`MOVE_VERSION`), so that a day entered or imported late is attached to the
version the branch sold on that day, and a withdrawn drink still accepts the
days of its live period and none after it.

**Duplicate day in an import.** The slice asks for a per-row "duplicate day"
error and for a re-import to replace. Both hold: a day that appears twice in
one file is rejected on its second line (`MSG-E23`), because the import cannot
know which line is right; a day that is already stored, from an earlier import
or from manual entry, is replaced, and the old figure goes to the audit log as
`REPLACE_SALES`. Entering a second record for a day by hand is `409 BR-25`;
the count is corrected with `PUT /sales/{id}`.

**Reasons a POS line is rejected** beyond the three codes of the contract
(`MSG-E21` unknown drink, `MSG-E22` not live, `MSG-E23` duplicate day) have
codes of their own, since the message list has none: `IMPORT_INVALID_QUANTITY`,
`IMPORT_INVALID_DATE`, `IMPORT_UNKNOWN_BRANCH`, `IMPORT_BRANCH_NOT_PERMITTED`
(a branch manager imports for their own branch) and `IMPORT_MISSING_VALUE`. A
file that is not in the layout at all is refused whole with `400 IMPORT_LAYOUT`.

**Import jobs.** The schema has no table for them. The result of an import is
the payload of its `POS_IMPORT` audit entry, the job id is that entry's entity
id in hexadecimal, and the `Idempotency-Key` is looked up among the caller's
own imports. Files are read up to 5 MB and 20 000 lines; XLSX is read without
a spreadsheet library (first sheet, shared and inline strings, date cells).

**`GET /sales/drinks`** is not in the contract table: it lists the drinks on
sale at a branch on a day, which the entry screen needs for its drink list.

**`GET /sales/aggregate`** also takes `groupBy=day`, `recipeId` (every version
of the drink), `branchId`, `from`, `to` and `controlRecipeId`. Trading days
are counted per branch, so cups per day is always cups per day per branch. The
control drink is counted on the branches and days the drink itself traded.

**`GET /dashboards/branch-performance`** returns one row per branch and drink
that has a launch status, with the sales of the period (the last four weeks
unless `from` and `to` say otherwise) beside the coverage figures.

**When a pilot ends.** RUNNING to ENDED belongs to the calendar: a pilot ends
the day after its `end_date`. A background service of the API makes the
transition every `Scheduler:PilotEndIntervalMinutes` (15 by default; 0 turns
it off) and audits it as `END` with no user, a system action. The same
transition is also made the first time anything looks at pilots after that day
(a pilot endpoint, or a release, which needs to know for BR-28), so nothing
waits for the next run.

**The gate and two records of it.** `branch_launch_status` says what a branch
sells; `pilot_branch` says how far a branch is through the gate of one pilot.
Starting a pilot plans the drink at its branches (PREPARING, or READY at once
where enough staff are already certified). `go-live` counts the certified
staff at that moment rather than trusting a stored READY, and READY falls back
to PREPARING if coverage is lost before the branch opens. A branch that sells
an earlier version of the drink moves to the pilot's version through the same
gate, and that move is recorded for BR-24.

**Planning a drink again.** The data dictionary has no transition out of
WITHDRAWN, and a branch has one row per drink. A later pilot or a rollout at a
branch where the drink was withdrawn, or never got through the gate, plans the
same row again from PREPARING.

**Pilots are for drinks being launched.** A pilot for a recipe of origin
EXISTING is refused with `409 BR-36`. Such a drink is LIVE at every active
branch the moment a version of it is released, with `coverage_met` counted and
normally false, and a later version replaces the earlier one at the branches
that sell it. A branch created afterwards sells every active existing drink
from its first day, on the version released at that moment. A closed branch is
passed over by a release; when it reopens it moves to the versions sold now
(recorded as `MOVE_VERSION` for BR-24) and takes on the existing drinks
released meanwhile. A drink withdrawn at a branch stays withdrawn there.

**Evaluation.** A branch is expected to have a sales record for every day from
the day it went live (or the pilot's first day) to the pilot's last; a day
with nothing sold is recorded as 0 cups. A branch short of that is INCOMPLETE
and left out of the scoring, and `coverageComplete` is false. ABSOLUTE is cups
per day per branch over the scored branches; RELATIVE counts the control drink
on the same branches and days; RETENTION compares cups per day per branch of
the two halves of the period (the first half has the extra day of an odd
period, and growth is a negative drop). A criterion that cannot be computed is
INCOMPLETE, and so is the pilot if no branch can be scored. A running pilot
is evaluated up to today.

**The decision.** `evaluated_json` holds the whole evaluation as it stood.
ROLLOUT plans the drink at every other active branch, each of which then goes
live through the same `go-live` endpoint and the same gate; DISCONTINUE
withdraws it from the pilot branches; REVISE changes nothing at the branches.
A note sent with the decision is kept in its audit entry.

**Pilot endpoints beyond the contract table:** `POST /pilots/{id}/cancel` and
`POST /branch-launch-status/{id}/withdraw`. The state models give both
transitions to the R&D Manager and name no endpoint. The pilot body carries
its criteria as `criteria: [ ... ]`; they are stored as `{ "criteria": [...] }`.

**Training needs from a branch shortfall** list the active trainees of a
branch whose coverage of a drink is not met and who hold no valid certificate
on the version bound there, against the published course of that version.

**What an impact analysis starts from.** `entityType` is `Ingredient`,
`StandardEquipment` or `RecipeVersion`. The graph reaches the versions that
have been in production (RELEASED or SUPERSEDED) and use the trigger; a draft
is re-validated when it is released and nothing was built on it. Of what is
bound to those versions, a course is affected while PUBLISHED, a certificate
while VALID, a branch while the version is LIVE there.

**What-if and commit.** `impact_type` has three values, so a run stores rows
for courses, certificates and branches only; the affected versions are derived
from the trigger. A what-if writes those rows and nothing else, not even an
audit entry, and a run that affects nothing leaves no rows and so cannot be
read back. Committing computes the affected set again at that moment and adds
rows for whatever became affected since, so that a certificate issued after
the what-if is not left valid. A run is committed once
(`409 IMPACT_ALREADY_COMMITTED`). Live branches are reported and not changed;
their certified staff are recounted, so `coverage_met` may fall to false.

**Propagation on release.** Releasing a version that supersedes another runs
the same analysis for the superseded version and commits it at once, as the
state models require: its published course becomes OUT_OF_DATE and the
certificates bound to it NEEDS_RECERT, with notices to the certificate holders
and to the trainer who built the course. The trainer then rebuilds the course
with `POST /courses/{id}/rebuild`, which is not in the contract table.

**A course needs a practical checklist to be submitted.** Like the quiz
without questions, a course built on a recipe version whose checklist is
empty could never certify anyone (BR-21), so submission is refused; the
trainer puts at least one step on it with `PUT /courses/{id}/practical-checklist`.

**The audit log endpoint** filters by entity, action, actor, the branch of the
actor and a period in whole UTC days. Entries the system keeps in the log for
want of a table (notifications, import jobs, token revocations) are part of it.

**The trace** follows a recipe version; `Course`, `Certificate` and `Recipe`
are accepted as starting points and lead to their version. It lists everyone
ever certified on the version, whatever the status of the certificate now.

**The compliance report** has one line per certificate ever issued, narrowed
by day of issue, branch, course or version. CSV is UTF-8 with a byte order
mark; a text cell that begins like a formula is written with a leading
apostrophe. XLSX is written without a spreadsheet library, with text as inline
strings. Every export is recorded as `EXPORT_COMPLIANCE`.

**Course effectiveness.** A module is passed on an attempt when all of its
questions were answered correctly; the rate is over the first attempt of each
enrolment. A module is highlighted when its rate lies at least 20 points below
the average of the other modules. On-time completion is judged for enrolments
that were passed or whose due date has gone by. Sales are the cups of the
bound version at the branches where at least one enrolment was passed.

## Known gaps

- A course that is not bound to a recipe version (INDUCTION, for instance) has
  no practical checklist, because checklist items are steps of a version. Such
  a course can be authored, published, assigned and studied, and its quiz can
  be taken, but nobody can be certified on it until the practical evaluation
  is given a checklist that does not come from a recipe.
- Notifications are recorded, not delivered: no e-mail or push channel is
  integrated.
- Prerequisites of a training regulation are reported and not enforced.
- The React frontend described by the developer pack is not part of this
  repository.

## Reference documents

Report 3 (the SRS) in the team's shared folder defines BR-01 to BR-22 and six
roles. The developer pack is newer and refers to BR-01 to BR-36, eight roles
and thirty-three screens; where the two differ, this code follows the pack.
