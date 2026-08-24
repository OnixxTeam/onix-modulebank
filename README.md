# Неделя 1. Database-first action runtime

C# gateway и generic action runtime поверх PostgreSQL: клиент вызывает
опубликованный action через единый маршрут, но не выбирает базу, schema,
функцию, SQL, policy или предметный результат.

Исходное условие задания без изменений — [`docs/TASK.md`](docs/TASK.md).
Точные контракты, JSON envelopes, error matrix, CLI semantics и колонки
проверочных views — [`docs/contract-reference.md`](docs/contract-reference.md).

## Решение

### Архитектура

Четыре сервиса в Compose плюс одноразовый `bootstrap`:

- **`gateway`** (ASP.NET Core + YARP) — единственный сервис с опубликованным
  host-портом `8080`. Whitelist-прокси на `api` по Compose DNS: пропускает
  только `/api/*`, `/openapi/*` и `/health/*`. Не содержит catalog, предметную
  логику и доступ к PostgreSQL.
- **`api`** (ASP.NET Core, без опубликованного порта) — generic HTTP executor.
  Проверяет JWT, строит server-side context, резолвит target из immutable
  action catalog, валидирует request/response schema, вызывает
  `api.invoke(...)` в одной Npgsql-транзакции, коммитит или откатывает.
- **`cli`** (`Course.Cli`) — entrypoint для contract commands: `migration
  apply`, `action validate/publish/list/activate/disable`, `bootstrap`.
- **`postgres`** — база `course`, роль `postgres`, данные в named volume.
- **`bootstrap`** — одноразовый сервис (`command: bootstrap`), применяет
  migrations и публикует встроенные actions до старта `api`
  (`depends_on: service_completed_successfully`). Не публикует портов, не
  входит в обязательную четвёрку и не завершает работу постоянно.

Направление вызовов: клиент → `gateway:8080` → `api` (Compose DNS) → JWT и
context → catalog → `api.invoke` → зарегистрированная PostgreSQL-функция →
commit/rollback. Подробности и границы доверия — [C4 Container](docs/c4/container.md),
[ADR 0001](docs/adr/0001-trust-boundary.md), [ADR 0002](docs/adr/0002-technical-vs-domain-result.md).

### Запуск

Требуется Docker Compose (проверено на Docker 29.4.1 / Compose 5.4.0).

```bash
make start_prod # docker compose up -d --build
```

Все команды выполняются из корня чистого clone. После запуска:

- `http://localhost:8080/health/live` и `.../health/ready` отвечают `200`;
- `http://localhost:8080/openapi/default.json` содержит включённые default
  actions;
- ручные SQL-команды и публикация встроенных actions не нужны — их выполняет
  `bootstrap`.

### Конфигурация

`api` читает (значения открытого профиля, реальный секрет не в репозитории):

| Переменная | Назначение |
|---|---|
| `COURSE_JWT_ISSUER` | ожидаемый issuer JWT (`moduledev-course`) |
| `COURSE_JWT_AUDIENCE` | ожидаемая audience JWT (`moduledev-api`) |
| `COURSE_JWT_SIGNING_KEY` | HS256-ключ ≥ 32 байт; проверка подменяет через Compose override |
| `COURSE_RUNTIME_DSN` | строка подключения ролью `course_runtime` |

`cli` и `bootstrap` (один и тот же образ) собирают строку подключения сами:

| Переменная | Назначение |
|---|---|
| `COURSE_DB_HOST` | хост PostgreSQL (`postgres` внутри Compose) |
| `COURSE_DB_PORT` | порт PostgreSQL (`5432`) |
| `COURSE_DB_NAME` | имя базы (`course`) |
| `COURSE_MIGRATOR_PASSWORD` | пароль роли `course_migrator`, только для `migration apply` |
| `COURSE_PUBLISHER_PASSWORD` | пароль роли `course_publisher`, только для команд `action` |

Роль выбирается командой, а не конфигурацией: миграции идут под
`course_migrator`, публикация манифестов — под `course_publisher`, поэтому у
CLI нет одной общей пары credentials.

`postgres` получает `POSTGRES_PASSWORD` для собственного суперпользователя и
все три пароля ролей — их читает [`db/init/00-roles.sh`](db/init/00-roles.sh)
при первичной инициализации тома. `COURSE_RUNTIME_PASSWORD` больше нигде из
окружения не берётся: в `api` он попадает уже внутри `COURSE_RUNTIME_DSN`.

Все переменные, которые Compose берёт из окружения, записаны как
`${VAR:-default}` в [`compose.yaml`](compose.yaml): если переменная не задана,
применяется dev-плейсхолдер, поэтому стек поднимается и без файла `.env`.
Остальные значения (`COURSE_DB_HOST`, `COURSE_JWT_ISSUER`, `COURSE_RUNTIME_DSN`
и прочие адреса внутри Compose-сети) заданы в `compose.yaml` литералами и
через окружение не переопределяются.

Скопируйте [`.env.example`](.env.example) в `.env` — реальных секретов там нет,
только dev-плейсхолдеры. Файл разделён на два блока: первый подставляется в
`compose.yaml`, второй нужен целям `make start_local_api` и
`make start_local_cli`, которые запускают проекты вне Docker и читают `.env`
напрямую. Второй блок на `docker compose up` не влияет.

### Миграции

Checksummed migrations в [`db/migrations`](db/migrations) применяет только
сервис `bootstrap` при `docker compose up`, ролью `course_migrator`, до
старта `api`. `api` подключается ролью `course_runtime` и не имеет migration
credentials. Повторный `docker compose up` не переприменяет уже применённые
миграции — раннер сверяет checksum.

### Проверка

```bash
./check.sh      # то же, что make check — открытая black-box проверка задания
make test        # dotnet test: unit + integration
make ci           # dotnet format --verify-no-changes + build -warnaserror + test
```

### Диагностика

- `make logs SERVICE=<gateway|api|postgres|cli>` — логи сервиса;
- `make psql` — psql-сессия в контейнер `postgres` (роль `postgres`);
- `GET /health/live` (gateway и api) — liveness процесса;
- `GET /health/ready` (api, через gateway) — готовность PostgreSQL и
  завершённой инициализации; переживает остановку/восстановление PostgreSQL
  без пересоздания `api`;
- `GET /openapi/default.json` и `/openapi/actions/{module}/{action}/{version}.json`
  — manifest-driven OpenAPI documents.

### Ограничения

Не входит в неделю 1 (см. также [`docs/TASK.md`](docs/TASK.md#не-входит-в-неделю)):
workflow maps и worker, `payment.submit` и provider-simulator, Outbox/Inbox,
lease/retries/failpoints, отдельные C# handlers для предметных actions,
CORS/rate limiting на gateway.

Пятый сервис `bootstrap` не публикует портов и завершается после разового
применения миграций и публикации встроенных actions — обязательная четвёрка
(`gateway`, `api`, `cli`, `postgres`) от этого не страдает.
