# C4 Container

Диаграмма — обычный mermaid `flowchart` с подписями в C4-нотации
(`[Container: ...]`, `[Person: ...]`, `[Software System: ...]`), без
`C4Container`: этот диалект mermaid экспериментальный и на GitHub рендерится
нестабильно. Здесь важна воспроизводимость ссылки у куратора, а не точное
соответствие спецификации C4.

```mermaid
flowchart TB
    client["[Person]<br/>Клиент проверки<br/>вызывает actions через HTTP"]

    subgraph compose["Docker Compose — единственная сеть course_default"]
        gateway["[Container: ASP.NET Core + YARP]<br/>gateway<br/>единственный опубликованный host-порт :8080<br/>whitelist-прокси, без предметной логики"]

        subgraph trust["Граница доверия — начинается здесь, см. ADR 0001"]
            api["[Container: ASP.NET Core]<br/>api<br/>JWT + server-side context<br/>action catalog, request/response schema<br/>одна Npgsql-транзакция на вызов"]
        end

        cli["[Container: .NET console]<br/>cli<br/>migration apply, action publish/activate/disable"]
        bootstrap["[Container: .NET console, одноразовый]<br/>bootstrap<br/>migration apply + публикация встроенных actions<br/>до старта api, портов не публикует"]

        postgres[("[Container: PostgreSQL 17]<br/>postgres<br/>база course, роли migrator/publisher/runtime<br/>named volume, переживает пересоздание контейнеров")]
    end

    client -->|"HTTP :8080<br/>POST /api/{module}/{action}"| gateway
    gateway -->|"Compose DNS: http://api:8080<br/>без изменения смысла запроса"| api
    api -->|"role course_runtime<br/>api.invoke(...)"| postgres
    cli -->|"role course_migrator / course_publisher<br/>Compose DNS: postgres:5432"| postgres
    bootstrap -->|"role course_migrator / course_publisher<br/>однократно при up"| postgres
    bootstrap -.->|"depends_on:<br/>service_completed_successfully"| api

    style trust stroke-dasharray: 5 5
```

## Что показано

- **Клиент** обращается только к `gateway` по `http://localhost:8080` —
  единственному опубликованному host-порту во всём Compose-файле.
- **`gateway`** проксирует whitelisted-маршруты (`/api/*`, `/openapi/*`,
  `/health/*`) на `api` по Compose DNS (`http://api:8080`), а не через
  `localhost` — DNS-имя сервиса разрешается только внутри сети Compose.
- **`api`** — единственный сервис, у которого есть роль `course_runtime` и
  который вызывает `api.invoke(...)`; граница доверия (ADR 0001) проходит
  по входу в `api`, а не по `gateway`.
- **`cli`** и **`bootstrap`** используют роли `course_migrator` и
  `course_publisher` — им не нужен `course_runtime`, и они не участвуют в
  обслуживании клиентских HTTP-запросов.
- **`bootstrap`** запускается один раз, применяет миграции и публикует
  встроенные actions, затем завершается; `api` стартует только после его
  успешного завершения (`depends_on: service_completed_successfully`) и не
  публикует host-порт.
- **`postgres`** хранит данные в named volume, переживает удаление и
  пересоздание `gateway` и `api`.
