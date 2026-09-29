# Тикет 19. Outbox test profile (4 attempts / 2 s lease) и bootstrap перестаёт перезаписывать operator choice

- **Ветка:** `feat/w3-19-outbox-profile-and-bootstrap-activation`
- **Приоритет:** средний (ни одно замечание не блокирует зачёт)
- **Источник:** `week-3-intern-feedback.pdf`, «Outbox test profile использует неверный retry budget и lease» + «Bootstrap безусловно возвращает bundled flow v1 в active state»
- **Порядок доработки:** 4 из 5

Два независимых дефекта операционной конфигурации, объединены в один тикет по
порядку доработки из отчёта.

---

## Часть A. Retry budget и lease не соответствуют тестовому профилю

### Наблюдение

`db/migrations/039_delivery_functions_and_perimeter_roles.sql:13-20` —
`delivery.retry_policy` заполняется `max_attempts = 5`, `delays_ms =
[200, 400, 800, 1600, 3200]`.

`delivery.claim_outbox` (`:40-70`) вычисляет аренду литералом:
`lease_expires_at = now() + interval '30 seconds'` — профиль сюда не
доходит вообще. Для сравнения, `workflow.claim_job`
(`db/migrations/022_jobs_lease_claim.sql:144,181`) уже принимает `p_lease_ms`
и считает `make_interval(secs => p_lease_ms / 1000.0)`.

`delivery.fail_outbox` (`:131-193`) читает `max_attempts`/`delays_ms` из
таблицы корректно — проблема только в самих значениях.

Опубликованный тестовый профиль (`docs/week3/07-autocheck-outline.md:483-493`):
`Максимум попыток Outbox = 3` (три задержки `200, 400, 800` мс поверх первой
попытки, то есть **четыре total attempts**), `Lease = 2 секунды`.

### Последствие

Сбойный провайдер получает лишний пятый запрос. Crash-recovery dispatcher
может занимать чужую запись до 30 секунд вместо 2 — проверочный бюджет
сценария на это не рассчитан.

### Что сделать

1. Миграция (следующий свободный номер — проверить `ls db/migrations/`):
   привести `delivery.retry_policy` к `max_attempts = 4`, `delays_ms =
   '[200, 400, 800]'`.
2. Добавить в профиль lease: колонка `lease_ms integer not null` в
   `delivery.retry_policy` (test = `2000`), либо параметр
   `p_lease_ms integer` у `delivery.claim_outbox` по образцу
   `workflow.claim_job`. Литерала `interval '30 seconds'` в теле функции не
   остаётся.
3. Значения приходят из test/production configuration, не из литералов в
   теле claim/fail (`SPEC`: «Предел числа попыток и задержки берутся из
   конфигурации профиля, а не зашиты»). Production-профиль может остаться
   консервативнее — семантика не меняется.
4. Обновить вызывающий Python-dispatcher, если сигнатура `claim_outbox`
   меняется (`python/src/perimeter/`).

### Как проверить

- Persistent transport failure: запись переходит в `DEAD` **ровно после
  четырёх** claims, задержки между попытками — 200 / 400 / 800 мс.
- После crash владельца lease доставка reclaimed **примерно через 2 секунды**
  в тестовом профиле, не через 30.
- `delivery.claim_outbox` не содержит числовых литералов аренды.

---

## Часть B. Bootstrap безусловно активирует bundled flow v1

### Наблюдение

`src/Course.Cli/Commands/BootstrapCommand.cs:88-120`: для каждого файла из
`ProductionFlowMapFiles` (`payment-processing`, `payment-review`) после
`PublishAsync` **всегда** вызывается `flowPublisher.ActivateAsync(flowName,
flowVersion)`. Комментарий в коде (`:108-111`) прямо фиксирует допущение
«других версий не будет».

Для actions это уже исправлено выше в том же методе (`:80-84`) — action
activate после idempotent publish больше не вызывается безусловно.

### Последствие

После того как оператор опубликовал и активировал flow v2, очередной
`docker compose up` (bootstrap) молча вернёт active version на bundled v1,
перезаписав operator-controlled operational state.

### Что сделать

Активировать bundled flow **только при first install** — когда у данного
`flowName` нет активной версии. Если активная версия уже есть (любая),
bootstrap оставляет выбор оператора нетронутым и просто сообщает об этом в
envelope (`activatedFlows` / новое поле `keptActive`).

Симметрично тому, как ветка actions уже отличает publish от activate.

### Как проверить

1. Чистый volume → bootstrap → `payment-processing` активна в v1.
2. Опубликовать и активировать flow v2.
3. Повторить bootstrap → active остаётся **v2**, `flow` v1 не активируется.
4. Новый чистый volume по-прежнему автоматически активирует v1.

## Готово, когда

Оба сценария из «Как проверить» воспроизводятся, `make test` и
`uv run pytest` зелёные.
