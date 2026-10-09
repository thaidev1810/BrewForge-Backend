# Samples

## `pos-import-sample.csv`

A POS export in the one supported layout, `branch_code, drink_code,
trading_date, quantity`, for branch B01 and the three seeded drinks the chain
already sells (R05, R07, R08). It is a test fixture and a demonstration asset:
sign in as `branchmgr` and upload it with `POST /api/v1/sales/import`.

It holds 20 lines that are accepted and 4 that are rejected, one of each kind:

| Row | Line | Rejected with |
|---|---|---|
| 6 | `B01,TRA-99,2026-09-02,31` | `MSG-E21` unknown drink code |
| 12 | `B01,R07,2025-12-31,54` | `MSG-E22` the branch was not live that day |
| 18 | `B01,R05,2026-09-01,110` | `MSG-E23` duplicate day (row 2 is the same drink, branch and day) |
| 22 | `B01,R08,2026-09-06,abc` | `IMPORT_INVALID_QUANTITY` not a whole number |

Row numbers are the line numbers of the file, the header being row 1.
