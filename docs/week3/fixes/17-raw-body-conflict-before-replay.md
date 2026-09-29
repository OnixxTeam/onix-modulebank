# Тикет 17. Exact raw-body conflict до generic cached replay

- **Ветка:** `feat/w3-17-raw-body-conflict-before-replay`
- **Приоритет:** высокий (не блокирует зачёт, замечание сохраняется)
- **Источник:** `week-3-intern-feedback.pdf`, «Высокий приоритет: Generic replay обходит exact-byte conflict»
- **Порядок доработки:** 2 из 5

## Наблюдение

`api.invoke` хеширует нормализованный `p_payload::text`
(`db/migrations/018_replay_returns_the_original_envelope.sql:83`) и при
совпадении `payload_hash` возвращает закешированный envelope
(`:113-116`) — до того, как целевая функция вообще будет вызвана.

`receipt.accept_v1` при этом хранит и сравнивает точный SHA-256 сырых байтов
тела (`context.rawBodyHash`,
`db/migrations/038_receipt_accept_and_inbox.sql:77`, сравнение на `:105-111`).
Этот exact-byte-контроль недостижим, если generic replay сработал раньше.

Практически: два корректно подписанных semantic-equivalent тела с разными
точными байтами и одним `messageId`/`Idempotency-Key` дают HTTP 200/200 с
исходным `correlationId`.

`src/Course.Api/Endpoints/ActionEndpoint.cs:103-144` уже вычисляет
`signatureResult.BodyHashHex` над точными байтами до парсинга JSON и
прокидывает его в `executor.ExecuteAsync`, то есть нужное значение на
границе уже есть.

## Последствие

Изменение подписанных байтов не обнаруживается, хотя Inbox хранит exact
raw-body hash. Конфликтная доставка маскируется под успешный duplicate
replay. `delivery.inbox` остаётся с первой записью, провайдер получает 200 на
изменённую квитанцию.

## Что сделать

Один из двух вариантов (выбрать первый, если не мешает неподписанным
действиям):

1. **Включить `rawBodyHash` в identity generic-конфликта.** Для действий, у
   которых подпись проверена (`context.transport.signatureVerified = true`),
   класть доверенный `rawBodyHash` в `api.idempotency_keys.payload_hash`
   (или в отдельную колонку `raw_body_hash`) и сравнивать именно его до
   возврата кеша. Несовпадение — `idempotency.conflict`, а не replay.
2. **Signature-specific check перед cached replay.** В `api.invoke`, в ветке
   `v_claimed is not true`, до `return v_stored_envelope` выполнять сравнение
   сохранённого raw-body hash с текущим `p_context->>'rawBodyHash'`, когда
   он присутствует.

Неподписанные действия (`rawBodyHash` отсутствует) сохраняют текущее
поведение — нормализованный payload hash.

Миграция: следующий свободный номер (проверить `ls db/migrations/`).

## Как проверить

Новый интеграционный тест «differently-serialized signed body»:

1. Отправить compact подписанную квитанцию
   (`{"messageId":"m1","externalRequestId":"e1",...}`) — ожидается 200,
   исход `ACCEPTED`, одна строка в `delivery.inbox`.
2. Пересобрать то же тело с другим whitespace и другим порядком ключей,
   корректно переподписать, отправить с **тем же** `Idempotency-Key` и тем же
   `messageId`.
3. Ожидается **HTTP 409** / `idempotency.conflict`, а не 200 с кешированным
   envelope.
4. `delivery.inbox` по-прежнему содержит ровно одну запись, `core.receipts` —
   ровно одну; второй бизнес-эффект не создан.

Плюс регрессия: неподписанное действие с semantic-equivalent телом и тем же
`Idempotency-Key` по-прежнему возвращает исходный envelope (replay не сломан).

## Готово, когда

Тест из пункта 3 красный до изменения и зелёный после; существующие
replay-тесты (`018`) не сломаны.
