# Тикет 16. Атомарное финальное `operation_events` для всех terminal-путей

- **Ветка:** `feat/w3-16-final-operation-events`
- **Приоритет:** высокий — блокирующее условие учебного зачёта
- **Источник:** `week-3-intern-feedback.pdf`, «Высокий приоритет: Terminal status не имеет immutable operation event»
- **Порядок доработки:** 1 из 5 (первый)

## Наблюдение

`payment.approve_v1` (`db/migrations/033_payment_approve.sql:29-32`),
`payment.reject_v1` (`db/migrations/034_payment_reject.sql:26-29`) и
`payment.complete_v1` (`db/migrations/040_apply_receipt.sql:108-111`)
переводят `core.operations.status` в `COMPLETED`/`REJECTED`, но ни одна из
трёх не вставляет строку в `core.operation_events`. Единственный писатель
событий на предметном пути — `payment.submit_v1`
(`db/migrations/035_payment_submit.sql:114-119`, `OPERATION_SUBMITTED`).

Runtime проверяющего показал две завершённые approval-операции, у которых в
истории остались только `OPERATION_CREATED` и `OPERATION_SUBMITTED`.

## Последствие

Авторитетное состояние (`core.operations.status`) и immutable audit history
(`core.operation_events`) расходятся. Финал невозможно восстановить по
событиям, `operation.events_v1` (`035_payment_submit.sql:160-176`) отдаёт
неполную историю. Это пропущенная запись истории, а не неверный формат
существующего события — стоп-фактор «Предметное состояние и неизменяемая
история согласованы».

## Что сделать

1. Новая миграция (следующий свободный номер — проверить `ls db/migrations/`,
   на момент написания это `041`), которая `create or replace` для всех трёх
   функций.
2. В той же PostgreSQL-транзакции и под тем же lock, что и `update
   core.operations`, вставлять ровно одну строку:
   - `OPERATION_COMPLETED` — для `payment.approve_v1` и `payment.complete_v1`;
   - `OPERATION_REJECTED` — для `payment.reject_v1`.
3. Событие привязывается к persisted receipt/decision: `payload_hash`
   считается от той же полезной нагрузки, что уже сохранена в
   `core.decisions` (миграция `030`) или в `delivery.inbox` (миграция `038`),
   а не от произвольного входа.
4. Запретить повторный terminal transition: `update ... where operation_id = ?
   and status not in ('COMPLETED','REJECTED')`. Если строк не обновилось —
   вернуть текущий результат без второго события (idempotent no-op), не
   вставляя дубль.
5. Rollback / fenced failure не должны менять ни `status`, ни history: любой
   выход по ошибке происходит до обоих writes либо откатывается целиком.

## Как проверить

Регрессии (`tests/Course.Tests/Integration`, плюс SQL-фикстуры):

- **automatic path:** `payment.submit` → `check_limit` WITHIN_LIMIT →
  `payment.approve` — ровно одно `OPERATION_COMPLETED`.
- **manual path:** `payment-review` → `workflow.manual_decision` APPROVED →
  `payment.approve`, и отдельно REJECTED → `payment.reject` — ровно одно
  `OPERATION_COMPLETED` / `OPERATION_REJECTED` соответственно.
- **provider path:** provider success → `payment.apply_receipt` →
  `payment.complete` — ровно одно `OPERATION_COMPLETED`; provider reject →
  `payment.reject` — ровно одно `OPERATION_REJECTED`.
- **повторный вызов** любой terminal-функции на уже завершённой операции не
  добавляет второго события и не меняет `status`.
- **rollback:** сымитировать сбой после `update` и до commit — ни `status`,
  ни `operation_events` не изменились.

## Готово, когда

`operation.events_v1` для каждой завершённой операции возвращает финальное
событие, `select count(*) from core.operation_events where event_type in
('OPERATION_COMPLETED','OPERATION_REJECTED')` равен числу терминальных
операций, регрессии всех трёх путей зелёные.
