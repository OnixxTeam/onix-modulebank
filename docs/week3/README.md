# Неделя 3 — материалы задания

Материалы курса, перенесённые из репозитория задания без изменений. Это документы автора курса:
править их нельзя, при расхождении с кодом сообщать куратору.

| Файл | Что это |
|---|---|
| [assignment.md](./assignment.md) | Условие задания: обязательный результат, сервисы, provider compatibility, SQL boundaries, обязательные actions, acceptance |
| [04-week-3.md](./04-week-3.md) | Полный контракт недели 3: предметные и архитектурные инварианты, целевая архитектура, оценка, стоп-факторы |
| [external-contracts.md](./external-contracts.md) | Внешние HTTP-контракты: точные запросы, ответы и правила повторов provider |
| [configuration.md](./configuration.md) | Конфигурация и запуск: Compose seam, переменные окружения, начальная настройка |
| [07-autocheck-outline.md](./07-autocheck-outline.md) | Контур автопроверки: проверяемые seams и сценарии |
| [student-handout.md](./student-handout.md) | Памятка участника: три Python-процесса, stable identifiers, типовые ошибки |

## Машиночитаемые контракты

Семь схем перенесены без изменений в рабочий каталог контрактов `contracts/course-1/`:
`payment-submit.payload.schema.json`, `payment-submit.result.schema.json`,
`provider-v02-callback.schema.json`, `provider-v02-payment-request.schema.json`,
`receipt-v1.schema.json`, `workflow-manual.payload.schema.json`,
`workflow-manual.result.schema.json`.

Выданная карта `payment-processing` версии 1 перенесена без изменений в `db/maps/payment-processing-v1.flow.yaml`.
Она ссылается на действия, которых ещё нет, и не публикуется в этом тикете.

## Что из этого следует

Разбор условия, принятые решения и план работ лежат вне отслеживаемых файлов, в `.scratch/week3/`:
спецификация и план с тикетами. Глоссарий проекта — в `CONTEXT.md` в корне.
