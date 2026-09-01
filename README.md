# Неделя 1-2. Database-first action runtime и persistent workflow-ядро

C# gateway и generic action runtime поверх PostgreSQL: клиент вызывает
опубликованный action через единый маршрут, но не выбирает базу, schema,
функцию, SQL, policy или предметный результат. Неделя 2 продолжает тот же
репозиторий: поверх action runtime добавлен versioned workflow-движок —
опубликованные карты процессов, персистентное состояние в PostgreSQL и общий
C# worker, исполняющий шаги без пересборки под конкретные actions или карты.

Исходное условие недели 1 без изменений — [`docs/TASK.md`](docs/TASK.md).
Точные контракты, JSON envelopes, error matrix, CLI semantics и колонки
проверочных views — [`docs/contract-reference.md`](docs/contract-reference.md).
Условие недели 2 и связанные материалы — [`docs/week2/`](docs/week2/).

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
  apply`, `action validate/publish/list/activate/disable`, `flow
  validate/publish/list/activate/start/get/signal`, `bootstrap`.
- **`postgres`** — база `course`, роль `postgres`, данные в named volume. Роли
  и `docker-entrypoint-initdb.d` запекаются в образ сборкой
  ([`db/Dockerfile`](db/Dockerfile)) — Compose не держит ни одного bind mount
  на репозиторий.
- **`bootstrap`** — одноразовый сервис (`command: bootstrap`), применяет
  migrations и публикует встроенные actions до старта `api`
  (`depends_on: service_completed_successfully`). Не публикует портов, не
  входит в обязательную четвёрку и не завершает работу постоянно.
- **`worker-a`**, **`worker-b`** — два экземпляра одного и того же образа
  `workflow-worker:local` (см. [Worker](#worker) ниже).

Направление вызовов: клиент → `gateway:8080` → `api` (Compose DNS) → JWT и
context → catalog → `api.invoke` → зарегистрированная PostgreSQL-функция →
commit/rollback. Подробности и границы доверия — [C4 Container](docs/c4/container.md),
[ADR 0001](docs/adr/0001-trust-boundary.md), [ADR 0002](docs/adr/0002-technical-vs-domain-result.md).

### Worker

`Workflow.Worker` — общий C# исполнитель шагов `automatic`: не знает предметных
имён actions или карт, только протокол шага. Цикл (`WorkerLoop.cs`):

1. Захватывает готовую `job` под своим владельцем (`WORKFLOW_WORKER_OWNER`,
   значения `worker-a`/`worker-b` в Compose) на аренду (`lease`).
2. Отображает вход задачи через `input_mapping` карты, вызывает целевой action
   через тот же `PostgresActionExecutor`, что и `api` — в чужой транзакции
   исполнителя (см. [ADR](docs/adr) о префакторе тикета 01), под principal
   `workflow_worker` с ограниченными scopes (`WORKFLOW_WORKER_SCOPES`).
3. Атомарно завершает `job` и продвигает граф процесса в одной транзакции,
   разграничивая устаревшее завершение по `lease version` (fencing).

`worker-a` и `worker-b` собираются из одного `Workflow.Worker/Dockerfile` и
запускают идентичный образ — Compose строит его один раз для `worker-a`,
`worker-b` только на него ссылается (`image: workflow-worker:local` без
собственного `build`). Публикация новых actions или карт не требует
пересборки этого образа: он читает их из базы через тот же generic catalog.

Интервалы аренды и поллинга берутся из `COURSE_TEST_PROFILE` (`1` — тестовый
профиль: аренда 2s, poll ≤100ms; иначе production-профиль: аренда 30s, poll
1s) с точечным override через `WORKFLOW_WORKER_LEASE_MS`/`_POLL_MS`.
`COURSE_FAILPOINT` включает именованные точки останова для воспроизводимых
аварийных сценариев (`Failpoints.cs`) — используется только тестовым
профилем, в production не задан.

### Workflow-карты

Карта (`map`, документ `course-1`) описывает одну неизменяемую версию flow:
`steps` (`automatic`, `wait_signal`, `manual`, `end`), `transitions` по
outcome и `tasks` — контракт вызова action с policy, retry и mapping.
Жизненный цикл, все через `cli`:

```bash
flow validate map.json          # схема + графовая семантика, без публикации
flow publish  map.json          # идемпотентно по каноническому хешу карты
flow list     <flow-name>        # опубликованные версии и активная
flow activate <flow-name> <ver>  # ровно одна активная версия на flow
flow start    <flow-name> --business-key <key> --data data.json
flow get      <process-id>       # компактное состояние процесса
flow signal   <process-id> --type <type> --message-id <id> --payload body.json
```

Запущенный процесс навсегда привязан (`pinned`) к версии карты, под которой
стартовал — активация новой версии на уже запущенные процессы не влияет.
Шаг `wait_signal` и шаг `manual` продвигаются одной и той же `flow signal`:
идемпотентно по `message-id`, ранний сигнал (до того, как процесс дошёл до
шага) допустим и учитывается при попадании на шаг. Полная выборка состояния
процесса (история, попытки) идёт как обычный action (`workflow.get`) через
`api.invoke`, а не через прямое соединение CLI.

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

Не входило в неделю 1 (см. также [`docs/TASK.md`](docs/TASK.md#не-входит-в-неделю))
и закрыто неделей 2 выше: workflow maps и worker, lease/retries/failpoints.
Всё ещё вне обеих недель: `payment.submit` и provider-simulator, Outbox/Inbox,
отдельные C# handlers для предметных actions, CORS/rate limiting на gateway,
параллельные развилки, таймеры, циклы, подпроцессы, компенсации, импорт и
экспорт BPMN, графический редактор карт, перевод запущенного процесса между
версиями, произвольные выражения и запросы из карты, ветвление кода по
именам, завершение ручного шага через публичное действие.

Пятый сервис `bootstrap` не публикует портов и завершается после разового
применения миграций и публикации встроенных actions — обязательная четвёрка
(`gateway`, `api`, `cli`, `postgres`) от этого не страдает.
