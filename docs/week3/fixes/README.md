# Доработка по отчёту недели 3

Источник: `week-3-intern-feedback.pdf` (проверенный commit `1d970c8eb028`).
Итог отчёта — незачёт по одному блокирующему условию: «Предметное состояние и
неизменяемая история согласованы».

Порядок выполнения зафиксирован разделом «Порядок доработки» отчёта — тикеты
идут строго по нему.

| # | Тикет | Приоритет | Блокирует зачёт |
|---|---|---|---|
| 16 | [Атомарное финальное `operation_events`](16-final-operation-events.md) | высокий | **да** |
| 17 | [Exact raw-body conflict до generic replay](17-raw-body-conflict-before-replay.md) | высокий | нет |
| 18 | [Callback type/date/CRLF validation](18-callback-validation-hardening.md) | средний | нет |
| 19 | [Outbox test profile + bootstrap activation](19-outbox-profile-and-bootstrap-activation.md) | средний | нет |
| 20 | [Pinned Week 3 команда проверки](20-week3-pinned-check-command.md) | средний | нет |

Тикет 19 объединяет два отдельных замечания отчёта (retry budget/lease и
безусловная активация bundled flow), поскольку «Порядок доработки» ставит их
одним шагом.

Каждый тикет работает в собственной ветке `feat/w3-NN-<slug>` и вливается в
`dev`.
