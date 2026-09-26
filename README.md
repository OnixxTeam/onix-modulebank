# Неделя 1-3. Database-first action runtime, workflow-ядро и Python-периметр

C# gateway и generic action runtime поверх PostgreSQL: клиент вызывает
опубликованный action через единый маршрут, но не выбирает базу, schema,
функцию, SQL, policy или предметный результат. Неделя 2 продолжает тот же
репозиторий: поверх action runtime добавлен versioned workflow-движок —
опубликованные карты процессов, персистентное состояние в PostgreSQL и общий
C# worker, исполняющий шаги без пересборки под конкретные actions или карты.
Неделя 3 добавляет реального внешнего собеседника: Python-периметр из трёх
процессов между ядром и симулятором платёжного провайдера, Outbox/Inbox в
PostgreSQL и общую границу подписи в C# API — подробности в
[Python-периметр](#python-периметр) и [Provider](#provider) ниже.

Исходное условие недели 1 без изменений — [`docs/week1/TASK.md`](docs/week1/TASK.md).
Точные контракты, JSON envelopes, error matrix, CLI semantics и колонки
проверочных views — [`docs/week1/contract-reference.md`](docs/week1/contract-reference.md).
Условие недели 2 и связанные материалы — [`docs/week2/`](docs/week2/). Условие
недели 3, внешние HTTP-контракты провайдера и материалы участника —
[`docs/week3/`](docs/week3/).

## Решение

### Архитектура

Десять сервисов в Compose: шесть из недели 2 ([`docs/week2/TASK.md`](docs/week2/TASK.md#обязательный-интерфейс-запуска))
плюс четыре из недели 3 — Python-периметр и provider-simulator
([`docs/week3/04-week-3.md`](docs/week3/04-week-3.md#обязательный-compose-seam)).
Открытая проверка строит и поднимает их все, по имени:

- **`gateway`** (ASP.NET Core + YARP) — единственный сервис с опубликованным
  host-портом `8080`. Whitelist-прокси на `api` по Compose DNS: пропускает
  только `/api/*`, `/openapi/*` и `/health/*`. Не содержит catalog, предметную
  логику и доступ к PostgreSQL.
- **`api`** (ASP.NET Core, без опубликованного порта) — generic HTTP executor.
  Проверяет JWT, строит server-side context, резолвит target из immutable
  action catalog, валидирует request/response schema, вызывает
  `api.invoke(...)` в одной Npgsql-транзакции, коммитит или откатывает.
  `/health/ready` готов сразу по готовности PostgreSQL, ещё до применения
  миграций — `api` не имеет `depends_on` ни на что, что могло бы это
  отложить, только на `postgres`.
- **`cli`** (`Course.Cli`) — entrypoint для contract commands: `migration
  apply`, `action validate/publish/list/activate/disable`, `flow
  validate/publish/list/activate/start/get/signal`, `bootstrap`. Ad hoc вызовы
  (проверкой и `make`) идут через `compose run --rm cli <args>`, который
  подставляет свою команду. Но у сервиса как такового, без override, есть
  свой command по умолчанию — `bootstrap`: применяет migrations и публикует
  встроенные actions/maps один раз и завершается. Так `docker compose up`
  без ручного вмешательства получает готовый каталог, не заводя седьмой
  сервис вне обязательного списка.
- **`postgres`** — база `course`, роль `postgres`, данные в named volume. Роли
  и `docker-entrypoint-initdb.d` запекаются в образ сборкой
  ([`db/Dockerfile`](db/Dockerfile)) — Compose не держит ни одного bind mount
  на репозиторий.
- **`worker-a`**, **`worker-b`** — два экземпляра одного и того же образа
  `workflow-worker:local` (см. [Worker](#worker) ниже).
- **`outbox-dispatcher`**, **`receipt-adapter`**, **`inbox-reconciler`** — три
  entrypoints одного локально собранного образа `perimeter:local`
  (см. [Python-периметр](#python-периметр) ниже).
- **`provider-simulator`** — внешний платёжный провайдер v0.2.0 по
  закреплённому digest (см. [Provider](#provider) ниже).

Направление вызовов: клиент → `gateway:8080` → `api` (Compose DNS) → JWT и
context → catalog → `api.invoke` → зарегистрированная PostgreSQL-функция →
commit/rollback. Подробности, границы доверия и принятые решения —
[C4 Container](docs/c4/container.md), [ADR 0001](docs/adr/0001-trust-boundary.md) (граница
доверия), [ADR 0002](docs/adr/0002-technical-vs-domain-result.md) (технический/предметный
результат), [ADR 0003](docs/adr/0003-lease-fencing-effect-idempotency.md) (аренда и
идемпотентность эффекта), [ADR 0004](docs/adr/0004-graph-interpreted-in-database.md) (граф в
PostgreSQL, не в C#), [ADR 0005](docs/adr/0005-adapter-signs-exact-bytes.md) (анти-коррупционный
слой подписывает точные байты), [ADR 0006](docs/adr/0006-outbox-inbox-state-stays-in-postgres.md)
(Outbox/Inbox и состояние периметра остаются в PostgreSQL).

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

Аренда Outbox подчиняется тому же профилю, но живёт в базе: `bootstrap`
записывает её в `delivery.retry_policy.lease_ms` (2s при
`COURSE_TEST_PROFILE=1`, иначе 30s; точечный override —
`COURSE_OUTBOX_LEASE_MS`), а `delivery.claim_outbox` читает оттуда — его
сигнатура закреплена контрактом и не принимает аренду аргументом. Бюджет
повторов (4 попытки, задержки 200/400/800 мс) в обоих профилях одинаков и
задаётся миграцией.
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

### Python-периметр

Три entrypoints одного локально собранного образа (`python/Dockerfile`,
`python:3.12-slim`), пакет `perimeter` в [`python/src`](python/src/perimeter):

- **`outbox-dispatcher`** (`perimeter/dispatcher.py`) — забирает due-строки из
  `delivery.claim_outbox`, вызывает провайдера через `httpx`, классифицирует
  ответ (`perimeter/classify.py`: `408`/`429`/`5xx`/таймаут/транспортная ошибка
  — повторяемые, прочие `4xx` — окончательные) и сообщает результат обратно
  `delivery.succeed_outbox`/`delivery.fail_outbox`. Не считает задержки и не
  решает исчерпан ли лимит попыток сам — это делает PostgreSQL
  ([ADR 0006](docs/adr/0006-outbox-inbox-state-stays-in-postgres.md)).
- **`receipt-adapter`** (`perimeter/adapter.py`, FastAPI/uvicorn) —
  единственный HTTP-вход периметра: принимает legacy callback провайдера на
  `/callbacks/provider-v02/{capability}`, читает **сырые байты** тела
  (Pydantic не встаёт между байтами и подписью), строго валидирует поля
  (`perimeter/callback.py`), строит канонический receipt v1 и подписывает его
  (`perimeter/receipt.py`, `perimeter/signature.py`), отправляет
  `POST /api/receipt/accept` через `gateway` — как любой другой клиент
  платформы, без обхода [ADR 0001](docs/adr/0001-trust-boundary.md). Не имеет
  учётных данных к PostgreSQL, не подтверждает callback до ответа `api` и не
  повторяет вызов сам — политика повторов одна, провайдерская.
- **`inbox-reconciler`** (`perimeter/reconciler.py`) — вызывает
  `delivery.reconcile_inbox` по таймеру, применяя принятые квитанции к
  закреплённым процессам через workflow-сигнал.

Каждый цикл разрезан на чистую функцию решения (без ввода-вывода,
юнит-тестируема без базы и сети — `python/tests`) и тонкую оболочку
ввода-вывода (`perimeter/runloop.py`, `db.py`, `request.py`): без этого
разреза проверка политики повторов и классификации ответов потребовала бы
фейков базы и сети — четвёртого шва тестирования вместо трёх принятых (см.
[Проверка](#проверка) ниже).

`outbox-dispatcher` и `inbox-reconciler` подключаются к PostgreSQL напрямую
ролями `outbox_dispatcher`/`inbox_reconciler` (стандартные libpq-переменные
`PGHOST`/`PGDATABASE`/`PGUSER`/`PGPASSWORD`/`PGAPPNAME`) с `EXECUTE` только на
свои функции схемы `delivery` — ни то, ни другое не имеет прямого DML ни к
одной таблице.

### Provider

`provider-simulator` — выданный образ v0.2.0 по закреплённому digest
(`compose.yaml`), внешний платёжный провайдер вне доверия платформы. Его
контракт (запросы, ответы, правила повторов) — во внешних материалах курса,
[`docs/week3/external-contracts.md`](docs/week3/external-contracts.md).

Провайдер получает запрос на списание от `outbox-dispatcher`
(`PROVIDER_URL`, идемпотентно по `externalRequestId`) и асинхронно шлёт
legacy callback на `receipt-adapter` по `CALLBACK_URL` с непредсказуемым
сегментом `{capability}` (`PROVIDER_CALLBACK_CAPABILITY`). Callback приходит
без подписи — подпись появляется только после анти-коррупционного слоя
([ADR 0005](docs/adr/0005-adapter-signs-exact-bytes.md)). Не публикует
host-порт; аудит провайдера (`GET /internal/audit/{externalRequestId}`)
доступен только внутри сети Compose.

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
  `cli` своим command по умолчанию (`bootstrap`).

### Конфигурация

`api` читает (значения открытого профиля, реальный секрет не в репозитории):

| Переменная | Назначение |
|---|---|
| `COURSE_JWT_ISSUER` | ожидаемый issuer JWT (`moduledev-course`) |
| `COURSE_JWT_AUDIENCE` | ожидаемая audience JWT (`moduledev-api`) |
| `COURSE_JWT_SIGNING_KEY` | HS256-ключ ≥ 32 байт; проверка подменяет через Compose override |
| `COURSE_RUNTIME_DSN` | строка подключения ролью `course_runtime` |
| `PROVIDER_HMAC_SECRET` | ключ общей границы подписи (ADR 0005); те же точные байты, что и у `receipt-adapter` |

`cli` (тем же образом бежит и его команда по умолчанию, `bootstrap`) собирает
строку подключения сама:

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
`cli` своим command по умолчанию (`bootstrap`) при `docker compose up`, ролью
`course_migrator`, независимо от `api` (не блокирует и не блокируется его
стартом, см. [Архитектура](#архитектура)). `api` подключается ролью
`course_runtime` и не имеет migration credentials. Повторный
`docker compose up` не переприменяет уже применённые миграции — раннер
сверяет checksum.

### Проверка

Три шва, из них один существующий:

1. **HTTP-действия поверх реальной базы** — существующий шов недель 1-2, всё
   предметное, включая `payment.submit`, `payment-review`, `receipt.accept`.
2. **Чистые функции периметра** — Python unit-тесты без ввода-вывода
   (`python/tests`): классификация ответов провайдера, построение и подпись
   receipt v1, валидация callback.
3. **Живой compose** — тонкий шов, только то, чему нужны настоящие
   контейнеры: `scripts/e2e_smoke.py` гонит обе карты (`payment-processing`,
   `payment-review`) через реальный `provider-simulator` от `payment.submit`
   до финального статуса.

```bash
./check.sh      # то же, что make check — открытая black-box проверка задания
make test        # dotnet test: unit + integration
cd python && uv run pytest   # Python unit-тесты периметра, без базы и сети
make e2e          # сквозной прогон обеих карт поверх уже собранного compose
make ci           # dotnet format --verify-no-changes + build -warnaserror + test
```

`./check.sh` запускает закреплённый открытый checker недели 3
(`autocheck/`, `week-3-public-check/0.3`), поднимает полный стек из десяти
сервисов на изолированном порту и пишет отчёт `week-3-public-report.json`.
Ему нужен только Docker; аргумент `--repo` подставляется автоматически, явный
`--repo PATH` по-прежнему имеет приоритет.

Checkers прошлых недель заархивированы и остаются воспроизводимыми:
`oldcheck/week2/check.sh` (`week-2-public-check/1.1`) и
`oldcheck/week1/check.sh`. Они не запускаются `make check` — это проверки
прошлых инкрементов, а не текущего.

### Диагностика

- `make logs SERVICE=<gateway|api|postgres|cli|outbox-dispatcher|receipt-adapter|inbox-reconciler|provider-simulator>`
  — логи сервиса;
- `make psql` — psql-сессия в контейнер `postgres` (роль `postgres`);
- `GET /health/live` (gateway и api) — liveness процесса;
- `GET /health/ready` (api, через gateway) — готовность соединения с
  PostgreSQL (не ждёт применения миграций — это делает `cli bootstrap`
  параллельно); переживает остановку/восстановление PostgreSQL без
  пересоздания `api`;
- `GET /openapi/default.json` и `/openapi/actions/{module}/{action}/{version}.json`
  — manifest-driven OpenAPI documents;
- собрать путь одного платежа по одному идентификатору: `operation_id`
  платежа детерминирует `externalRequestId` (`payment.prepare_external`) →
  строка `outbox`/`external_requests` по этому `externalRequestId` →
  callback провайдера несёт тот же `operationId` и свой `providerPaymentId`
  → `providerPaymentId` становится `messageId` квитанции в `receipts`/`inbox`
  → применённая запись `inbox` даёт `signals.message_id` (префиксованный
  производный от `messageId`) → сигнал продвигает `process_id`, чьё
  `operation_id` замыкает цепочку на исходный платёж. Все шесть проекций
  (`external_requests`, `receipts`, `outbox`, `inbox`, `decisions` плюс
  `workflow`-проекции недели 2) читаются через `make psql`.

### Ограничения

Не входило в неделю 1 (см. также [`docs/week1/TASK.md`](docs/week1/TASK.md#не-входит-в-неделю))
и закрыто неделей 2: workflow maps и worker, lease/retries/failpoints. Закрыто
неделей 3 выше: `payment.submit` и provider-simulator, Outbox/Inbox,
подписанные квитанции.

Всё ещё вне трёх недель: отдельные C# handlers для предметных actions,
CORS/rate limiting на gateway, параллельные развилки, таймеры, циклы,
подпроцессы, компенсации, импорт и экспорт BPMN, графический редактор карт,
перевод запущенного процесса между версиями, произвольные выражения и запросы
из карты, ветвление кода по именам, завершение ручного шага через публичное
действие.

Явно отложено с недели 3 на неделю 4 (не недоделка этой недели — граница
обещаний, которую никто не давал): детерминированные failpoints
Python-периметра (крашевые сценарии проверяются остановкой и пересозданием
контейнеров, не воспроизводимой точкой останова), несколько параллельных
отправителей на один Outbox, разброс задержек между попытками доставки,
окончательное мёртвое состояние доставки (`DEAD` — конечное состояние этой
недели, не терминальный workflow-провал с ручной реакцией), метрики
периметра. Доставка ровно один раз этой неделей не обещана нигде — обещан
at-least-once с идемпотентным приёмом на обеих границах.
