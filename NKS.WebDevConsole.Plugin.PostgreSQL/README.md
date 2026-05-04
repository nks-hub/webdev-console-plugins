# PostgreSQL Plugin

Manages a local PostgreSQL instance from WDC-managed binaries.

## Capabilities

- Detects `~/.wdc/binaries/postgresql/<version>/bin/postgres`.
- Initializes `~/.wdc/data/postgresql` with `initdb`.
- Starts and stops PostgreSQL through `pg_ctl`.
- Checks readiness with `pg_isready`.
- Streams the PostgreSQL log from `~/.wdc/logs/postgresql/postgresql.log`.

Install binaries through WDC first:

```bash
wdc binaries install postgresql@18.3
```
